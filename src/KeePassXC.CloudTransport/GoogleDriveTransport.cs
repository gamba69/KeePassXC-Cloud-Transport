using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KeePassXC.CloudTransport;

/// <summary>Google Drive v3 files API with duplicate-name refusal and short-lived positive folder cache.</summary>
public sealed class GoogleDriveTransport : ICloudTransport, ICloudAuthentication
{
    private const string FilesApi = "https://www.googleapis.com/drive/v3/files";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3/files";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private readonly string _prefix;
    private readonly int _cacheSeconds;
    private readonly HttpClient _http;
    private readonly GoogleOAuthSession _oauth;
    private readonly Action<CloudTransportEvent>? _events;
    private readonly IProgress<CloudTransferProgress>? _progress;
    private readonly ConcurrentDictionary<string, (string Id, DateTimeOffset Checked)> _folders = new(StringComparer.Ordinal);
    public CloudProvider Provider => CloudProvider.GoogleDrive;
    public CloudCapabilities Capabilities { get; } = new(false, false, false);
    public GoogleDriveTransport(CloudConnectionOptions options, CloudTransportServices services)
    {
        if (string.IsNullOrWhiteSpace(options.GoogleClientId) || string.IsNullOrWhiteSpace(options.GoogleRefreshTokenTarget))
            throw new CloudTransportException("OAUTH_CONFIGURATION", CloudFailureKind.Configuration, "Google OAuth client and refresh-token target are required");
        _prefix = CloudPath.Normalize(options.RemotePrefix, true);
        _events = services.Events;
        _progress = services.TransferProgress;
        _cacheSeconds = Math.Clamp(options.GoogleFolderCacheSeconds, 0, 60);
        _http = HttpHelpers.NewClient(options, services);
        _oauth = new GoogleOAuthSession(options, services, _http);
    }
    private string Path(string path, bool allowEmpty = false) => CloudPath.Combine(_prefix, path, allowEmpty);
    private static string UrlEncode(string value) => Uri.EscapeDataString(value);
    private static string QuoteJson(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _oauth.AccessTokenAsync(ct));
        var res = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)res.StatusCode is >= 300 and < 400)
        {
            res.Dispose();
            throw new CloudSafetyException("Unexpected Google Drive API redirect");
        }
        return res;
    }
    private static Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct) =>
        HttpHelpers.ReadJsonLimitedAsync(response, 8_000_000, ct);
    private sealed record Entry(string Id, string Name, string Mime, DateTimeOffset Modified, long? Size);
    private static Entry Parse(JsonElement item)
    {
        var id = item.GetProperty("id").GetString() ?? throw new CloudSafetyException("Google Drive file ID missing");
        var name = item.GetProperty("name").GetString() ?? throw new CloudSafetyException("Google Drive filename missing");
        _ = CloudPath.Normalize(name);
        var mime = item.GetProperty("mimeType").GetString() ?? "";
        DateTimeOffset.TryParse(item.TryGetProperty("modifiedTime", out var modified) ? modified.GetString() : null,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time);
        long? size = item.TryGetProperty("size", out var value) && long.TryParse(value.GetString(), out var bytes) ? bytes : null;
        return new Entry(id, name, mime, time, size);
    }
    private async Task<IReadOnlyList<Entry>> ChildrenAsync(string parentId, CancellationToken ct)
    {
        var result = new List<Entry>();
        string? token = null;
        for (var page = 0; page < 1000; page++)
        {
            var q = "'" + parentId.Replace("'", "\\'", StringComparison.Ordinal) + "' in parents and trashed = false";
            var url = FilesApi + "?q=" + UrlEncode(q) + "&pageSize=1000&fields=" +
                UrlEncode("nextPageToken,files(id,name,mimeType,modifiedTime,size)");
            if (token is not null) url += "&pageToken=" + UrlEncode(token);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await SendAsync(req, ct);
            HttpHelpers.Ensure(res, "Google Drive list");
            using var json = await ReadJsonAsync(res, ct);
            foreach (var entry in json.RootElement.GetProperty("files").EnumerateArray())
                result.Add(Parse(entry));
            if (result.Count > 100_000) throw new CloudSafetyException("Google Drive listing exceeds safety limit");
            token = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (string.IsNullOrEmpty(token)) return result;
        }
        throw new CloudSafetyException("Google Drive pagination limit exceeded");
    }
    private async Task<Entry?> OneAsync(string parent, string name, CancellationToken ct)
    {
        var matches = (await ChildrenAsync(parent, ct)).Where(x => x.Name == name).ToArray();
        if (matches.Length > 1) throw new CloudSafetyException("Ambiguous duplicate Google Drive filename");
        return matches.Length == 0 ? null : matches[0];
    }
    private async Task<string?> FolderIdAsync(string folder, bool create, CancellationToken ct)
    {
        if (folder.Length == 0) return "root";
        var cachePath = "";
        var parent = "root";
        foreach (var component in folder.Split('/'))
        {
            cachePath = cachePath.Length == 0 ? component : cachePath + "/" + component;
            if (_cacheSeconds > 0 && _folders.TryGetValue(cachePath, out var cached) &&
                cached.Checked.AddSeconds(_cacheSeconds) > DateTimeOffset.UtcNow)
            {
                parent = cached.Id;
                continue;
            }
            var found = await OneAsync(parent, component, ct);
            if (found is null && create)
            {
                var metadata = "{\"name\":" + QuoteJson(component) + ",\"mimeType\":" + QuoteJson(FolderMime) +
                    ",\"parents\":[" + QuoteJson(parent) + "]}";
                using var request = new HttpRequestMessage(HttpMethod.Post, FilesApi + "?fields=id,name,mimeType")
                { Content = new StringContent(metadata, Encoding.UTF8, "application/json") };
                using var response = await SendAsync(request, ct);
                HttpHelpers.Ensure(response, "Google Drive create folder");
                using var obj = await ReadJsonAsync(response, ct);
                var id = obj.RootElement.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id)) throw new CloudSafetyException("Google Drive did not confirm folder creation");
                // Do not select arbitrarily when concurrent creation made duplicate names.
                var verified = await OneAsync(parent, component, ct);
                if (verified is null || verified.Id != id || verified.Mime != FolderMime)
                    throw new CloudSafetyException("Google Drive folder creation could not be verified");
                found = verified;
            }
            if (found is null) return null;
            if (found.Mime != FolderMime) throw new CloudSafetyException("Google Drive parent is not a folder");
            parent = found.Id;
            if (_cacheSeconds > 0) _folders[cachePath] = (parent, DateTimeOffset.UtcNow);
        }
        return parent;
    }
    private async Task<Entry?> FindAsync(string objectPath, CancellationToken ct)
    {
        var path = Path(objectPath);
        var cut = path.LastIndexOf('/');
        var folder = cut < 0 ? "" : path[..cut];
        var parent = await FolderIdAsync(folder, false, ct);
        return parent is null ? null : await OneAsync(parent, path[(cut + 1)..], ct);
    }
    public async Task<bool> CheckConnectionAsync(CancellationToken ct)
    {
        try { _ = await _oauth.AccessTokenAsync(ct); _ = await ChildrenAsync("root", ct); return true; }
        catch (CloudTransportException e) when (e.Kind == CloudFailureKind.Authentication) { return false; }
    }
    public async Task<bool> LoginAsync(CancellationToken ct)
    {
        _ = await _oauth.LoginAsync(ct);
        _folders.Clear();
        return await CheckConnectionAsync(ct);
    }
    public async Task<CloudObjectInfo?> GetInfoAsync(string path, CancellationToken ct)
    {
        var node = await FindAsync(path, ct);
        if (node is null) return null;
        if (node.Mime == FolderMime || node.Mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal))
            throw new CloudSafetyException("Google Drive object is not a regular binary file");
        return new CloudObjectInfo(CloudPath.Normalize(path), node.Modified, node.Size);
    }
    public async Task<bool> DownloadAsync(string path, string destination, CancellationToken ct)
    {
        _events?.Invoke(new("Download", "Google Drive download started"));
        var node = await FindAsync(path, ct);
        if (node is null) return false;
        if (node.Mime == FolderMime || node.Mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal))
            throw new CloudSafetyException("Google Drive object is not a regular binary file");
        using var request = new HttpRequestMessage(HttpMethod.Get, FilesApi + "/" + UrlEncode(node.Id) + "?alt=media");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new CloudSafetyException("Google Drive object changed during download");
        HttpHelpers.Ensure(response, "Google Drive download");
        await HttpHelpers.DownloadToNewFileAsync(response, destination, ct,
            new TransferMeter(Provider, CloudTransferDirection.Download, _progress));
        _events?.Invoke(new("Download", "Google Drive download finished"));
        return true;
    }
    public async Task UploadAsync(string source, string path, CloudWriteMode mode, CancellationToken ct)
    {
        _events?.Invoke(new("Upload", "Google Drive upload started"));
        var key = Path(path);
        var cut = key.LastIndexOf('/');
        var folder = cut < 0 ? "" : key[..cut];
        var name = key[(cut + 1)..];
        var parent = await FolderIdAsync(folder, true, ct) ?? throw new CloudSafetyException("Google Drive parent missing");
        var existing = await OneAsync(parent, name, ct);
        if (mode == CloudWriteMode.CreateNew && existing is not null)
            throw new CloudTransportException("CONCURRENT_WRITE", CloudFailureKind.ConcurrentWrite, "Google Drive object already exists");
        var meter = new TransferMeter(Provider, CloudTransferDirection.Upload, _progress);
        if (existing is not null)
        {
            if (existing.Mime == FolderMime) throw new CloudSafetyException("Cannot overwrite Google Drive directory");
            using var req = new HttpRequestMessage(new HttpMethod("PATCH"),
                UploadApi + "/" + UrlEncode(existing.Id) + "?uploadType=media");
            req.Content = HttpHelpers.UploadContent(source, meter);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var res = await SendAsync(req, ct);
            HttpHelpers.Ensure(res, "Google Drive update");
        }
        else
        {
            var metadata = "{\"name\":" + QuoteJson(name) + ",\"parents\":[" + QuoteJson(parent) +
                "],\"mimeType\":\"application/octet-stream\"}";
            using var req = new HttpRequestMessage(HttpMethod.Post, UploadApi + "?uploadType=multipart&fields=id");
            var content = new MultipartContent("related");
            content.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));
            var body = HttpHelpers.UploadContent(source, meter);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(body);
            req.Content = content;
            using var res = await SendAsync(req, ct);
            HttpHelpers.Ensure(res, "Google Drive create");
            using var json = await ReadJsonAsync(res, ct);
            if (string.IsNullOrWhiteSpace(json.RootElement.GetProperty("id").GetString()))
                throw new CloudSafetyException("Google Drive did not confirm created file");
        }
        _folders.Clear();
        // A simultaneous create may have yielded duplicate names. This is not an atomic CreateNew.
        var verified = await OneAsync(parent, name, ct);
        if (verified is null) throw new CloudSafetyException("Google Drive upload not visible after completion");
        meter.Confirm();
        meter.Complete();
        _events?.Invoke(new("Upload", "Google Drive upload finished"));
    }
    public async Task EnsureDirectoryAsync(string directory, CancellationToken ct)
    {
        _ = await FolderIdAsync(Path(directory, true), true, ct);
    }
    public async Task<IReadOnlyList<CloudObjectInfo>> ListAsync(string directory, CancellationToken ct)
    {
        var parent = await FolderIdAsync(Path(directory, true), false, ct);
        if (parent is null) return [];
        var relative = CloudPath.Normalize(directory, true);
        return (await ChildrenAsync(parent, ct)).Where(x => x.Mime != FolderMime)
            .Select(x => new CloudObjectInfo(relative.Length == 0 ? x.Name : relative + "/" + x.Name, x.Modified, x.Size)).ToArray();
    }
    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        var node = await FindAsync(path, ct) ?? throw new CloudSafetyException("Google Drive object vanished before deletion");
        using var req = new HttpRequestMessage(HttpMethod.Delete, FilesApi + "/" + UrlEncode(node.Id));
        using var res = await SendAsync(req, ct);
        HttpHelpers.Ensure(res, "Google Drive delete");
        _folders.Clear();
    }
    public void Dispose() => _http.Dispose();
}
