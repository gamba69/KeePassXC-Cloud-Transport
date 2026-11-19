using System.Globalization;
using System.Text.Json;

namespace KeePassXC.CloudTransport;

/// <summary>Official Proton Drive CLI transport, derived from KeePassXC Cloud Sync 8.2.4 command contracts.</summary>
public sealed class ProtonDriveTransport : ICloudTransport, ICloudAuthentication
{
    private const string Root = "/my-files";
    private readonly string _executable, _prefix;
    private readonly TimeSpan _timeout;
    private readonly ICloudCliRunner _runner;
    private readonly IProtonLoginLauncher? _login;
    private readonly Action<CloudTransportEvent>? _events;
    private readonly IProgress<CloudTransferProgress>? _progress;
    private readonly IDisposable _usage;
    private readonly FileStream _binaryLease;
    public CloudProvider Provider => CloudProvider.ProtonDrive;
    public CloudCapabilities Capabilities { get; } = new(false, false, false);

    public ProtonDriveTransport(CloudConnectionOptions options, CloudTransportServices services)
    {
        if (string.IsNullOrWhiteSpace(options.ProtonCliExecutable))
            throw new CloudTransportException("PROTON_CLI_MISSING", CloudFailureKind.Configuration, "Official Proton CLI path is not configured");
        _executable = System.IO.Path.GetFullPath(options.ProtonCliExecutable);
        if (!File.Exists(_executable))
            throw new CloudTransportException("PROTON_CLI_MISSING", CloudFailureKind.Configuration, "Official Proton CLI executable not found");
        _prefix = CloudPath.Normalize(options.RemotePrefix, true);
        _timeout = options.Timeout;
        _runner = services.Cli ?? new ProcessCloudCliRunner();
        _login = services.ProtonLogin;
        _events = services.Events;
        _progress = services.TransferProgress;
        _usage = ProtonCliLease.BeginUse();
        try
        {
            // Prevent Windows file replacement while the transport is alive; release only on Dispose().
            _binaryLease = new FileStream(_executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch { _usage.Dispose(); throw; }
    }
    private string PathFor(string path, bool allowEmpty = false)
    {
        var combined = CloudPath.Combine(_prefix, path, allowEmpty);
        return combined.Length == 0 ? Root : Root + "/" + combined;
    }
    private async Task<string> RunAsync(CancellationToken ct, params string[] args)
    {
        var output = await _runner.RunAsync(_executable, _timeout, ct, args);
        if (output.Length > 12_000_000) throw new CloudSafetyException("Proton CLI response too large");
        return output;
    }
    private sealed record Node(string Name, string Type, DateTimeOffset Modified);
    private static string? NameOf(JsonElement node)
    {
        if (!node.TryGetProperty("name", out var name)) return null;
        if (name.ValueKind == JsonValueKind.String) return name.GetString();
        if (name.ValueKind == JsonValueKind.Object && name.TryGetProperty("ok", out var ok) &&
            ok.ValueKind == JsonValueKind.True && name.TryGetProperty("value", out var value)) return value.GetString();
        return null;
    }
    private async Task<IReadOnlyList<Node>> ListFolderAsync(string absolutePath, CancellationToken ct)
    {
        var output = await RunAsync(ct, "filesystem", "list", absolutePath, "--json");
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new CloudSafetyException("Proton CLI returned unexpected listing format");
        var nodes = new List<Node>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var name = NameOf(item);
            if (string.IsNullOrWhiteSpace(name)) throw new CloudSafetyException("Unreadable Proton remote name");
            _ = CloudPath.Normalize(name);
            var type = item.GetProperty("type").GetString() ?? "";
            DateTimeOffset.TryParse(item.TryGetProperty("modificationTime", out var modified) ? modified.GetString() : null,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time);
            nodes.Add(new Node(name, type, time));
        }
        return nodes;
    }
    private async Task<bool> ExistsAsync(string fullPath, bool wantFolder, CancellationToken ct)
    {
        if (fullPath == Root) return wantFolder;
        if (!fullPath.StartsWith(Root + "/", StringComparison.Ordinal))
            throw new CloudSafetyException("Proton remote path outside the fixed CLI root");
        var parts = fullPath[(Root.Length + 1)..].Split('/');
        var current = Root;
        for (var i = 0; i < parts.Length; i++)
        {
            var items = (await ListFolderAsync(current, ct)).Where(x => x.Name == parts[i]).ToArray();
            if (items.Length > 1) throw new CloudSafetyException("Proton path is ambiguous");
            if (items.Length == 0) return false;
            var folder = i < parts.Length - 1 || wantFolder;
            if (items[0].Type != (folder ? "folder" : "file")) throw new CloudSafetyException("Proton remote object type mismatch");
            current += "/" + parts[i];
        }
        return true;
    }
    public async Task<bool> CheckConnectionAsync(CancellationToken ct)
    {
        try { _ = await ListFolderAsync(Root, ct); return true; }
        catch (CloudTransportException e) when (e.Kind == CloudFailureKind.Authentication) { return false; }
    }
    public async Task<bool> LoginAsync(CancellationToken ct)
    {
        if (_login is null) throw new CloudTransportException("CONFIGURATION", CloudFailureKind.Configuration,
            "Proton login launcher is not configured");
        if (!await _login.StartLoginAsync(_executable, ct)) return false;
        // Login can finish in an external browser after the launcher process returns.
        // Only a successful official read-only CLI listing confirms authentication.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            while (true)
            {
                if (await CheckConnectionAsync(deadline.Token)) return true;
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
    public async Task<CloudObjectInfo?> GetInfoAsync(string path, CancellationToken ct)
    {
        var full = PathFor(path);
        var cut = full.LastIndexOf('/');
        var parent = full[..cut];
        if (!await ExistsAsync(parent, true, ct)) return null;
        var children = (await ListFolderAsync(parent, ct)).Where(x => x.Name == full[(cut + 1)..]).ToArray();
        if (children.Length > 1) throw new CloudSafetyException("Ambiguous Proton filename");
        if (children.Length == 0) return null;
        if (children[0].Type != "file") throw new CloudSafetyException("Proton object is not a regular file");
        return new CloudObjectInfo(CloudPath.Normalize(path), children[0].Modified);
    }
    public async Task<bool> DownloadAsync(string path, string destination, CancellationToken ct)
    {
        _events?.Invoke(new("Download", "Proton Drive download started"));
        var remote = PathFor(path);
        if (!await ExistsAsync(remote, false, ct)) return false;
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cloudtransport-proton-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var meter = new TransferMeter(Provider, CloudTransferDirection.Download, _progress, live: false);
        try
        {
            meter.Begin(null); // Official CLI --json has no live progress; do not simulate it.
            var output = await RunAsync(ct, "filesystem", "download", remote, folder, "--json", "--file-conflict-strategy", "skip");
            using var report = JsonDocument.Parse(output);
            var result = report.RootElement;
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("failedItems", out var failures) || failures.GetInt32() != 0 ||
                !result.TryGetProperty("transferredItems", out var count) || count.GetInt32() != 1)
                throw new CloudSafetyException("Proton CLI did not confirm download");
            var actual = System.IO.Path.Combine(folder, CloudPath.Normalize(path).Split('/')[^1]);
            if (!File.Exists(actual)) throw new CloudSafetyException("Proton CLI did not produce downloaded file");
            var length = new FileInfo(actual).Length;
            File.Move(actual, destination); // Move only completed temporary file.
            meter.CompleteFromCli(length);
            _events?.Invoke(new("Download", "Proton Drive download finished"));
            return true;
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public async Task UploadAsync(string source, string path, CloudWriteMode mode, CancellationToken ct)
    {
        _events?.Invoke(new("Upload", "Proton Drive upload started"));
        var full = PathFor(path);
        var slash = full.LastIndexOf('/');
        var parent = full[..slash];
        var filename = full[(slash + 1)..];
        var relative = CloudPath.Normalize(path);
        var parentRelative = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
        await EnsureDirectoryAsync(parentRelative, ct);
        if (mode == CloudWriteMode.CreateNew && await ExistsAsync(full, false, ct))
            throw new CloudTransportException("CONCURRENT_WRITE", CloudFailureKind.ConcurrentWrite, "Proton destination already exists");
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cloudtransport-proton-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var staged = System.IO.Path.Combine(temp, filename);
            File.Copy(source, staged, false);
            var meter = new TransferMeter(Provider, CloudTransferDirection.Upload, _progress, live: false);
            meter.Begin(new FileInfo(staged).Length);
            var strategy = mode == CloudWriteMode.Replace ? "create-new-revision" : "skip";
            var output = await RunAsync(ct, "filesystem", "upload", staged, parent, "--json", "--file-conflict-strategy", strategy, "--skip-thumbnails");
            using var report = JsonDocument.Parse(output);
            var root = report.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("failedItems", out var failed) || failed.GetInt32() != 0 ||
                !root.TryGetProperty("transferredItems", out var transferred) ||
                (mode == CloudWriteMode.CreateNew && transferred.GetInt32() != 1))
                throw new CloudSafetyException("Proton transfer was not confirmed");
            var sentBytes = root.TryGetProperty("transferredBytes", out var sent) && sent.TryGetInt64(out var amount)
                ? amount : (transferred.GetInt32() == 1 ? new FileInfo(staged).Length : 0L);
            meter.CompleteFromCli(sentBytes, skipped: transferred.GetInt32() == 0);
            _events?.Invoke(new("Upload", "Proton Drive upload finished"));
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public async Task EnsureDirectoryAsync(string directory, CancellationToken ct)
    {
        var full = PathFor(directory, true);
        if (full == Root) return;
        var segments = full[(Root.Length + 1)..].Split('/');
        var parent = Root;
        foreach (var segment in segments)
        {
            var matches = (await ListFolderAsync(parent, ct)).Where(x => x.Name == segment).ToArray();
            if (matches.Length > 1) throw new CloudSafetyException("Duplicate Proton folder names");
            if (matches.Length == 1)
            {
                if (matches[0].Type != "folder") throw new CloudSafetyException("Proton parent is a file");
            }
            else
            {
                _ = await RunAsync(ct, "filesystem", "create-folder", parent, segment, "--json");
                var verified = await ListFolderAsync(parent, ct);
                if (verified.Count(x => x.Name == segment && x.Type == "folder") != 1)
                    throw new CloudSafetyException("Proton directory creation could not be verified");
            }
            parent += "/" + segment;
        }
    }
    public async Task<IReadOnlyList<CloudObjectInfo>> ListAsync(string directory, CancellationToken ct)
    {
        var full = PathFor(directory, true);
        if (!await ExistsAsync(full, true, ct)) return [];
        var relative = CloudPath.Normalize(directory, true);
        return (await ListFolderAsync(full, ct)).Where(x => x.Type == "file")
            .Select(x => new CloudObjectInfo(relative.Length == 0 ? x.Name : relative + "/" + x.Name, x.Modified)).ToArray();
    }
    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        var full = PathFor(path);
        if (!await ExistsAsync(full, false, ct)) throw new CloudSafetyException("Proton object vanished before delete");
        var output = await RunAsync(ct, "filesystem", "trash", full, "--json");
        using var report = JsonDocument.Parse(output);
        if (report.RootElement.ValueKind != JsonValueKind.Array || report.RootElement.GetArrayLength() != 1 ||
            !report.RootElement[0].TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new CloudSafetyException("Proton did not confirm trash operation");
    }
    public void Dispose()
    {
        _binaryLease.Dispose();
        _usage.Dispose();
    }
}
