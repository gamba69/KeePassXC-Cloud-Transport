namespace KeePassXC.CloudTransport;

/// <summary>Untrusted remote object names are validated before a provider sees them.</summary>
public static class CloudPath
{
    public static string Normalize(string path, bool allowEmpty = false)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (path.Length == 0 && allowEmpty) return "";
        if (path.Length == 0 || path.StartsWith('/') || path.EndsWith('/') || path.Length > 1024 ||
            path.Contains('\\') || path.Contains(':') || path.Contains('?') || path.Contains('#') ||
            path.Any(char.IsControl))
            throw new CloudSafetyException("Invalid remote relative path");
        var parts = path.Split('/');
        if (parts.Any(x => x.Length == 0 || x is "." or ".." || x.Length > 255))
            throw new CloudSafetyException("Unsafe remote path component");
        return path;
    }

    public static string Combine(string prefix, string path, bool allowEmpty = false)
    {
        prefix = Normalize(prefix, true);
        path = Normalize(path, allowEmpty);
        if (path.Length == 0) return prefix;
        return prefix.Length == 0 ? path : prefix + "/" + path;
    }

    public static string Encode(string path) => string.Join("/", Normalize(path).Split('/').Select(Uri.EscapeDataString));
}
