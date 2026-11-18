using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace KeePassXC.CloudTransport;

/// <summary>AWS Signature V4 REST transport; path-style bucket addressing (also Cloudflare R2).</summary>
public sealed class S3Transport : ICloudTransport, IConditionalCloudTransport
{
    private readonly CloudProvider _provider;
    private readonly string _bucket, _region, _keyId, _secret, _prefix;
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly Action<CloudTransportEvent>? _events;
    public CloudProvider Provider => _provider;
    public CloudCapabilities Capabilities { get; } = new(true, true, true);

    public S3Transport(CloudConnectionOptions options, CloudTransportServices services)
    {
        _provider = options.Provider;
        _events = services.Events;
        if (_provider is not (CloudProvider.S3 or CloudProvider.CloudflareR2)) throw new ArgumentOutOfRangeException(nameof(options));
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length > 0 ||
            endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.AbsolutePath != "/")
            throw Config("S3 endpoint must be an HTTPS origin, without path or credentials");
        _endpoint = endpoint;
        _bucket = options.Bucket ?? "";
        if (_bucket.Length is < 3 or > 63 || _bucket[0] is '.' or '-' || _bucket[^1] is '.' or '-' ||
            _bucket.Contains("..", StringComparison.Ordinal) || _bucket.Contains(".-", StringComparison.Ordinal) ||
            _bucket.Contains("-.", StringComparison.Ordinal) ||
            _bucket.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
            throw Config("S3 bucket name is invalid");
        _region = options.S3Region;
        if (_region.Length is < 1 or > 64 || _region.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw Config("S3 signing region is invalid");
        if (_provider == CloudProvider.CloudflareR2 && (_region != "auto" ||
            !_endpoint.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase)))
            throw Config("R2 requires official account endpoint and region 'auto'");
        _prefix = CloudPath.Normalize(options.RemotePrefix, true);
        (_keyId, _secret) = services.Secrets.Read(options.CredentialTarget ?? throw Config("S3 credential target missing"));
        if (string.IsNullOrWhiteSpace(_keyId) || string.IsNullOrWhiteSpace(_secret) || _keyId.Any(char.IsWhiteSpace))
            throw Config("S3 access key credentials missing");
        _http = HttpHelpers.NewClient(options, services);
    }
    private static CloudTransportException Config(string message) => new("CONFIGURATION", CloudFailureKind.Configuration, message);
    private string Key(string path, bool allowEmpty = false) => CloudPath.Combine(_prefix, path, allowEmpty);
    private static string Encode(string value) => Uri.EscapeDataString(value);
    private static string Query(IEnumerable<KeyValuePair<string, string>> pairs) => string.Join("&",
        pairs.Select(p => Encode(p.Key) + "=" + Encode(p.Value)).OrderBy(x => x, StringComparer.Ordinal));
    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);
    private static byte[] DeriveKey(string secret, string date, string region)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), date);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, "s3");
        return Hmac(kService, "aws4_request");
    }
    private Uri ObjectUrl(string key, string query = "")
    {
        var encoded = CloudPath.Encode(key);
        var path = "/" + Encode(_bucket) + "/" + encoded;
        return new Uri(_endpoint.GetLeftPart(UriPartial.Authority) + path + (query.Length == 0 ? "" : "?" + query));
    }
    private Uri BucketUrl(string query)
        => new(_endpoint.GetLeftPart(UriPartial.Authority) + "/" + Encode(_bucket) + "/?" + query);
    private async Task<HttpRequestMessage> SignAsync(HttpMethod method, Uri url, string? source, CancellationToken ct,
        string? ifMatch = null, bool ifNoneMatch = false)
    {
        var contentHash = Hex(SHA256.HashData(Array.Empty<byte>()));
        if (source is not null)
        {
            await using var hashInput = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            contentHash = Hex(await SHA256.HashDataAsync(hashInput, ct));
        }
        var stamp = DateTimeOffset.UtcNow;
        var date = stamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var amzDate = stamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentHash);
        if (ifNoneMatch) req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        if (ifMatch is not null)
        {
            if (!EntityTagHeaderValue.TryParse(ifMatch, out var etag) || etag.IsWeak || etag == EntityTagHeaderValue.Any)
                throw new CloudSafetyException("Invalid S3 revision token");
            req.Headers.IfMatch.Add(etag);
        }
        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        // URI already uses RFC3986 escaping. The canonical query must be sorted by encoded key/value.
        var canonical = method.Method + "\n" + url.AbsolutePath + "\n" + url.Query.TrimStart('?') + "\n" +
            "host:" + url.Authority.ToLowerInvariant() + "\n" +
            "x-amz-content-sha256:" + contentHash + "\n" +
            "x-amz-date:" + amzDate + "\n\n" + signedHeaders + "\n" + contentHash;
        var scope = date + "/" + _region + "/s3/aws4_request";
        var stringToSign = "AWS4-HMAC-SHA256\n" + amzDate + "\n" + scope + "\n" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var signature = Hex(Hmac(DeriveKey(_secret, date, _region), stringToSign));
        // Use the typed Authorization header. TryAddWithoutValidation can store a raw
        // value that is not reliably exposed through HttpRequestHeaders.Authorization.
        // Both real HTTP handlers and diagnostic/test handlers must see SigV4.
        req.Headers.Authorization = new AuthenticationHeaderValue("AWS4-HMAC-SHA256",
            "Credential=" + _keyId + "/" + scope +
            ", SignedHeaders=" + signedHeaders + ", Signature=" + signature);
        if (source is not null) req.Content = new StreamContent(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read));
        return req;
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, string? source, CancellationToken ct,
        string? ifMatch = null, bool ifNoneMatch = false)
    {
        using var req = await SignAsync(method, url, source, ct, ifMatch, ifNoneMatch);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    public async Task<bool> DownloadAsync(string path, string destination, CancellationToken ct)
    {
        _events?.Invoke(new("Download", "S3 download started"));
        using var res = await SendAsync(HttpMethod.Get, ObjectUrl(Key(path)), null, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return false;
        HttpHelpers.Ensure(res, "S3 GET");
        await HttpHelpers.DownloadToNewFileAsync(res, destination, ct);
        _events?.Invoke(new("Download", "S3 download finished"));
        return true;
    }
    public async Task UploadAsync(string source, string path, CloudWriteMode mode, CancellationToken ct)
    {
        _events?.Invoke(new("Upload", "S3 upload started"));
        using var res = await SendAsync(HttpMethod.Put, ObjectUrl(Key(path)), source, ct,
            ifNoneMatch: mode == CloudWriteMode.CreateNew);
        HttpHelpers.Ensure(res, "S3 PUT");
        _events?.Invoke(new("Upload", "S3 upload finished"));
    }
    public async Task<CloudObjectInfo?> GetInfoAsync(string path, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Head, ObjectUrl(Key(path)), null, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        HttpHelpers.Ensure(res, "S3 HEAD");
        return new CloudObjectInfo(CloudPath.Normalize(path), res.Content.Headers.LastModified ?? default,
            res.Content.Headers.ContentLength, res.Headers.ETag?.ToString());
    }
    public async Task<string?> ReadRevisionAsync(string path, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Head, ObjectUrl(Key(path)), null, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        HttpHelpers.Ensure(res, "S3 HEAD");
        var etag = res.Headers.ETag;
        if (etag is null || etag.IsWeak) throw new CloudSafetyException("S3 server did not provide a strong ETag");
        return etag.ToString();
    }
    public async Task UploadConditionalAsync(string source, string path, string? observedRevision, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Put, ObjectUrl(Key(path)), source, ct,
            observedRevision, ifNoneMatch: observedRevision is null);
        HttpHelpers.Ensure(res, "S3 conditional PUT");
    }
    public Task EnsureDirectoryAsync(string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = Key(directory, true); // S3 directories are prefixes, never create placeholders.
        return Task.CompletedTask;
    }
    public async Task<IReadOnlyList<CloudObjectInfo>> ListAsync(string directory, CancellationToken ct)
    {
        var key = Key(directory, true);
        var prefix = key.Length == 0 ? "" : key + "/";
        var result = new List<CloudObjectInfo>();
        string? cursor = null;
        do
        {
            var query = new List<KeyValuePair<string, string>> { new("list-type", "2"), new("prefix", prefix), new("delimiter", "/"), new("max-keys", "1000") };
            if (cursor is not null) query.Add(new("continuation-token", cursor));
            using var res = await SendAsync(HttpMethod.Get, BucketUrl(Query(query)), null, ct);
            HttpHelpers.Ensure(res, "S3 LIST");
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 10_000_000 });
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
            var root = document.Root ?? throw new CloudSafetyException("Empty S3 listing");
            var ns = root.Name.Namespace;
            foreach (var element in root.Elements(ns + "Contents"))
            {
                var remote = element.Element(ns + "Key")?.Value ?? throw new CloudSafetyException("S3 object missing key");
                if (!remote.StartsWith(prefix, StringComparison.Ordinal) || remote.Length <= prefix.Length ||
                    remote[prefix.Length..].Contains('/')) throw new CloudSafetyException("S3 listing escaped requested prefix");
                var local = remote[(_prefix.Length == 0 ? 0 : _prefix.Length + 1)..];
                _ = CloudPath.Normalize(local);
                DateTimeOffset.TryParse(element.Element(ns + "LastModified")?.Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var modified);
                var size = long.TryParse(element.Element(ns + "Size")?.Value, out var bytes) ? bytes : (long?)null;
                result.Add(new CloudObjectInfo(local, modified, size, element.Element(ns + "ETag")?.Value));
            }
            var truncated = root.Element(ns + "IsTruncated")?.Value == "true";
            cursor = truncated ? root.Element(ns + "NextContinuationToken")?.Value : null;
            if (truncated && string.IsNullOrWhiteSpace(cursor)) throw new CloudSafetyException("S3 listing truncated without continuation token");
            if (result.Count > 100_000) throw new CloudSafetyException("S3 listing exceeds object limit");
        } while (cursor is not null);
        return result;
    }
    public async Task<bool> CheckConnectionAsync(CancellationToken ct)
    {
        try { _ = await ListAsync("", ct); return true; }
        catch (CloudTransportException e) when (e.Kind == CloudFailureKind.Authentication) { return false; }
    }
    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Delete, ObjectUrl(Key(path)), null, ct);
        HttpHelpers.Ensure(res, "S3 DELETE");
    }
    public void Dispose() => _http.Dispose();
}
