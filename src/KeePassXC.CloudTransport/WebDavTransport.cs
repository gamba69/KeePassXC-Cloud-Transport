using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace KeePassXC.CloudTransport;

/// <summary>HTTPS WebDAV transport; conditional writes require strong server ETags.</summary>
public sealed class WebDavTransport : ICloudTransport, IConditionalCloudTransport
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly Uri _base;
    private readonly string _prefix;
    private readonly string _authorization;
    private readonly HttpClient _http;
    private readonly Action<CloudTransportEvent>? _events;
    private readonly IProgress<CloudTransferProgress>? _progress;

    public CloudProvider Provider => CloudProvider.WebDav;
    public CloudCapabilities Capabilities { get; } = new(true, true, true);

    public WebDavTransport(CloudConnectionOptions options, CloudTransportServices services)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new CloudTransportException("CONFIGURATION", CloudFailureKind.Configuration, "WebDAV endpoint must be an HTTPS URL without credentials, query or fragment");
        _base = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        _events = services.Events;
        _progress = services.TransferProgress;
        _prefix = CloudPath.Normalize(options.RemotePrefix, true);
        var (user, password) = services.Secrets.Read(options.CredentialTarget ?? throw new CloudTransportException(
            "CREDENTIALS_MISSING", CloudFailureKind.Configuration, "WebDAV credential target not configured"));
        if (string.IsNullOrWhiteSpace(user))
            throw new CloudTransportException("CREDENTIALS_MISSING", CloudFailureKind.Configuration, "WebDAV username missing");
        _authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
        _http = HttpHelpers.NewClient(options, services);
    }

    private string Path(string path, bool allowEmpty = false) => CloudPath.Combine(_prefix, path, allowEmpty);
    private Uri Url(string path, bool allowEmpty = false)
    {
        var combined = Path(path, allowEmpty);
        return combined.Length == 0 ? _base : new Uri(_base, CloudPath.Encode(combined));
    }
    private HttpRequestMessage Request(HttpMethod method, string path, bool allowEmpty = false)
    {
        var r = new HttpRequestMessage(method, Url(path, allowEmpty));
        r.Headers.TryAddWithoutValidation("Authorization", _authorization);
        return r;
    }
    private HttpRequestMessage RequestCombined(HttpMethod method, string combinedPath)
    {
        // Used exclusively for ensuring every component of the *already-prefixed* root.
        var r = new HttpRequestMessage(method, new Uri(_base, CloudPath.Encode(combinedPath)));
        r.Headers.TryAddWithoutValidation("Authorization", _authorization);
        return r;
    }
    private async Task<HttpResponseMessage> PropFindAsync(string directory, int depth, CancellationToken ct)
    {
        using var req = Request(new HttpMethod("PROPFIND"), directory, true);
        req.Headers.TryAddWithoutValidation("Depth", depth.ToString(CultureInfo.InvariantCulture));
        req.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:getlastmodified/><d:getcontentlength/><d:getetag/><d:resourcetype/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
        // Caller owns/disposes the response. The request is disposed after SendAsync returns.
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    public async Task<bool> CheckConnectionAsync(CancellationToken ct)
    {
        try
        {
            using var r = await PropFindAsync("", 0, ct);
            HttpHelpers.Ensure(r, "WebDAV probe");
            if (r.StatusCode != (HttpStatusCode)207)
                throw new CloudSafetyException("WebDAV server did not return multistatus");
            return true;
        }
        catch (CloudTransportException e) when (e.Kind == CloudFailureKind.Authentication) { return false; }
    }
    public async Task<bool> DownloadAsync(string path, string destination, CancellationToken ct)
    {
        _events?.Invoke(new("Download", "WebDAV download started"));
        using var req = Request(HttpMethod.Get, path);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return false;
        HttpHelpers.Ensure(res, "WebDAV GET");
        await HttpHelpers.DownloadToNewFileAsync(res, destination, ct,
            new TransferMeter(Provider, CloudTransferDirection.Download, _progress));
        _events?.Invoke(new("Download", "WebDAV download finished"));
        return true;
    }
    public async Task UploadAsync(string source, string path, CloudWriteMode mode, CancellationToken ct)
    {
        _events?.Invoke(new("Upload", "WebDAV upload started"));
        using var req = Request(HttpMethod.Put, path);
        if (mode == CloudWriteMode.CreateNew) req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        var meter = new TransferMeter(Provider, CloudTransferDirection.Upload, _progress);
        req.Content = HttpHelpers.UploadContent(source, meter);
        using var res = await _http.SendAsync(req, ct);
        HttpHelpers.Ensure(res, "WebDAV PUT");
        meter.Confirm();
        meter.Complete();
        _events?.Invoke(new("Upload", "WebDAV upload finished"));
    }
    public async Task<CloudObjectInfo?> GetInfoAsync(string path, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Head, path);
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        HttpHelpers.Ensure(res, "WebDAV HEAD");
        return new CloudObjectInfo(CloudPath.Normalize(path), res.Content.Headers.LastModified ?? default,
            res.Content.Headers.ContentLength, res.Headers.ETag?.ToString());
    }
    public async Task<string?> ReadRevisionAsync(string path, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Head, path);
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        HttpHelpers.Ensure(res, "WebDAV HEAD");
        var tag = res.Headers.ETag;
        if (tag is null || tag.IsWeak)
            throw new CloudSafetyException("WebDAV server did not provide a strong ETag");
        return tag.ToString();
    }
    public async Task UploadConditionalAsync(string source, string path, string? observedRevision, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Put, path);
        if (observedRevision is null) req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        else if (EntityTagHeaderValue.TryParse(observedRevision, out var tag) && !tag.IsWeak && tag != EntityTagHeaderValue.Any)
            req.Headers.IfMatch.Add(tag);
        else throw new CloudSafetyException("Invalid or weak WebDAV revision token");
        var meter = new TransferMeter(Provider, CloudTransferDirection.Upload, _progress);
        req.Content = HttpHelpers.UploadContent(source, meter);
        using var res = await _http.SendAsync(req, ct);
        HttpHelpers.Ensure(res, "WebDAV conditional PUT");
        meter.Confirm();
        meter.Complete();
    }
    public async Task EnsureDirectoryAsync(string directory, CancellationToken ct)
    {
        // Ensure the configured remote prefix as well as the requested child folder.
        // A path is combined only once; running it through Request() would prefix twice.
        var path = Path(directory, true);
        if (path.Length == 0) return;
        var parts = path.Split('/');
        for (var i = 1; i <= parts.Length; i++)
        {
            var full = string.Join("/", parts.Take(i));
            using var req = RequestCombined(new HttpMethod("MKCOL"), full);
            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Created) continue;
            if (res.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                // A 405 may mean an existing collection. Verify with Depth: 0.
                using var probe = RequestCombined(new HttpMethod("PROPFIND"), full);
                probe.Headers.TryAddWithoutValidation("Depth", "0");
                probe.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
                using var checkedResponse = await _http.SendAsync(probe, ct);
                HttpHelpers.Ensure(checkedResponse, "WebDAV MKCOL existing directory");
                if (checkedResponse.StatusCode != (HttpStatusCode)207)
                    throw new CloudSafetyException("WebDAV existing directory cannot be verified");
                continue;
            }
            HttpHelpers.Ensure(res, "WebDAV MKCOL");
        }
    }
    public async Task<IReadOnlyList<CloudObjectInfo>> ListAsync(string directory, CancellationToken ct)
    {
        using var res = await PropFindAsync(directory, 1, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return [];
        HttpHelpers.Ensure(res, "WebDAV PROPFIND");
        if (res.StatusCode != (HttpStatusCode)207)
            throw new CloudSafetyException("WebDAV PROPFIND did not return 207 Multi-Status");
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 8_000_000 };
        using var reader = XmlReader.Create(stream, settings);
        var xml = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        var requested = Url(directory, true);
        var parent = new Uri(requested.AbsoluteUri.TrimEnd('/') + "/");
        var canonicalParent = Uri.UnescapeDataString(parent.AbsolutePath).TrimEnd('/');
        var prefix = canonicalParent + "/";
        var result = new List<CloudObjectInfo>();
        foreach (var node in xml.Descendants(Dav + "response"))
        {
            var href = node.Element(Dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) throw new CloudSafetyException("WebDAV response has no href");
            if (!Uri.TryCreate(parent, href, out var itemUri) || itemUri.Scheme != parent.Scheme ||
                itemUri.Authority != parent.Authority) throw new CloudSafetyException("WebDAV response contains foreign origin");
            var abs = Uri.UnescapeDataString(itemUri.AbsolutePath).TrimEnd('/');
            if (abs == canonicalParent) continue;
            if (!abs.StartsWith(prefix, StringComparison.Ordinal) || abs[prefix.Length..].Contains('/'))
                throw new CloudSafetyException("WebDAV listing escaped the selected directory");
            var name = abs[prefix.Length..];
            _ = CloudPath.Normalize(name);
            var prop = node.Descendants(Dav + "prop").FirstOrDefault();
            if (prop is null || prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null) continue;
            DateTimeOffset.TryParse(prop.Element(Dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var modified);
            long? size = long.TryParse(prop.Element(Dav + "getcontentlength")?.Value, out var length) ? length : null;
            var etag = prop.Element(Dav + "getetag")?.Value;
            if (etag is not null && !EntityTagHeaderValue.TryParse(etag, out _)) etag = null;
            var relative = CloudPath.Normalize(directory, true);
            result.Add(new CloudObjectInfo(relative.Length == 0 ? name : relative + "/" + name, modified, size, etag));
        }
        return result;
    }
    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Delete, path);
        using var res = await _http.SendAsync(req, ct);
        HttpHelpers.Ensure(res, "WebDAV DELETE");
    }
    public void Dispose() => _http.Dispose();
}
