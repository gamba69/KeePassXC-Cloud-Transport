using System.Net;

namespace KeePassXC.CloudTransport;

/// <summary>Supported transports; Cloudflare R2 uses the same S3 API with region 'auto'.</summary>
public enum CloudProvider { GoogleDrive, ProtonDrive, S3, CloudflareR2, WebDav }

/// <summary>Explicit, non-secret connection options. Do not persist credentials in this model.</summary>
public sealed record CloudConnectionOptions
{
    public required CloudProvider Provider { get; init; }
    /// <summary>Optional provider root prefix applied exactly once to each relative object path.</summary>
    public string RemotePrefix { get; init; } = "";
    /// <summary>HTTPS WebDAV endpoint or S3/R2 account endpoint (S3 origin only).</summary>
    public string? Endpoint { get; init; }
    /// <summary>S3 bucket name.</summary>
    public string? Bucket { get; init; }
    public string S3Region { get; init; } = "auto";
    /// <summary>Opaque name of secret in the host-owned credential store.</summary>
    public string? CredentialTarget { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>Google Desktop OAuth public-client identifier.</summary>
    public string? GoogleClientId { get; init; }
    /// <summary>Optional embedded Desktop OAuth client secret; not a user credential.</summary>
    public string? GoogleClientSecret { get; init; }
    /// <summary>Optional credential-store target for a client secret (overrides GoogleClientSecret).</summary>
    public string? GoogleClientSecretTarget { get; init; }
    /// <summary>Credential-store target for refresh token. Separate per application/account.</summary>
    public string? GoogleRefreshTokenTarget { get; init; }
    public string? GoogleExpectedAccount { get; init; }
    /// <summary>Absolute path to official Proton CLI executable (never included in the package).</summary>
    public string? ProtonCliExecutable { get; init; }
    /// <summary>Existing folder name cache lifespan, 0..60 seconds; object metadata is never cached.</summary>
    public int GoogleFolderCacheSeconds { get; init; } = 10;
}

public interface ICloudSecretStore
{
    (string User, string Password) Read(string target);
    void Write(string target, string user, string secret);
}

public interface ICloudBrowserLauncher { void Open(string absoluteUrl); }

/// <summary>Host-owned CLI runner; must enforce deadline, cancellation and safe command invocation.</summary>
public interface ICloudCliRunner
{
    Task<string> RunAsync(string executable, TimeSpan timeout, CancellationToken ct, params string[] args);
}

/// <summary>Host starts the official interactive Proton login; a successful launch is not proof of authentication.</summary>
public interface IProtonLoginLauncher
{
    Task<bool> StartLoginAsync(string executable, CancellationToken ct);
}

/// <summary>Optional complete, self-contained HTML pages displayed in the loopback Google OAuth tab.
/// Supplied by the host at login time. Never include credentials or untrusted content.
/// Remote scripts, stylesheets and images are blocked by the OAuth callback CSP.</summary>
public sealed record GoogleOAuthCallbackPages(string SuccessHtml, string FailureHtml);

public sealed record CloudTransportServices
{
    public required ICloudSecretStore Secrets { get; init; }
    public ICloudBrowserLauncher? Browser { get; init; }
    public ICloudCliRunner? Cli { get; init; }
    public IProtonLoginLauncher? ProtonLogin { get; init; }
    /// <summary>Optional host-branded Google OAuth result pages. Both are required when set.
    /// Inlined CSS and data: images/fonts are supported; JavaScript and network assets are blocked.
    /// Maximum UTF-8 encoded size is 128 KiB per page. Null retains the built-in plain page.</summary>
    public GoogleOAuthCallbackPages? GoogleOAuthCallbackPages { get; init; }
    /// <summary>Optional events with safe, redacted messages. Never provide secrets.</summary>
    public Action<CloudTransportEvent>? Events { get; init; }
    /// <summary>For deterministic tests; production should leave null.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }
}

public sealed record CloudTransportEvent(string Stage, string Message, bool IsError = false);
public sealed record CloudObjectInfo(string Path, DateTimeOffset ModifiedUtc, long? Size = null, string? Revision = null);
public enum CloudWriteMode { Replace, CreateNew }
public sealed record CloudCapabilities(bool ConditionalReplace, bool ConditionalCreate, bool AtomicServerCompareAndSwap);

public interface ICloudTransport : IDisposable
{
    CloudProvider Provider { get; }
    CloudCapabilities Capabilities { get; }
    /// <summary>Read-only probe; false is reserved for a definitively unauthenticated session.</summary>
    Task<bool> CheckConnectionAsync(CancellationToken ct);
    /// <summary>False ONLY on definitive not-found. Timeout, auth failure and unknown results throw.</summary>
    Task<bool> DownloadAsync(string path, string destination, CancellationToken ct);
    Task UploadAsync(string source, string path, CloudWriteMode mode, CancellationToken ct);
    Task<IReadOnlyList<CloudObjectInfo>> ListAsync(string directory, CancellationToken ct);
    /// <summary>Returns null only for a definitively absent regular file.</summary>
    Task<CloudObjectInfo?> GetInfoAsync(string path, CancellationToken ct);
    Task EnsureDirectoryAsync(string directory, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}

/// <summary>Revision tokens are opaque. Conditional operations MUST be enforced by server atomically.</summary>
public interface IConditionalCloudTransport
{
    Task<string?> ReadRevisionAsync(string path, CancellationToken ct);
    Task UploadConditionalAsync(string source, string path, string? observedRevision, CancellationToken ct);
}

public interface ICloudAuthentication
{
    /// <summary>Interactive authorization, followed by a real read-only session probe.</summary>
    Task<bool> LoginAsync(CancellationToken ct);
}

public enum CloudFailureKind { Authentication, Configuration, RateLimited, Transient, ConcurrentWrite, Safety, Unknown }
public sealed class CloudTransportException(string code, CloudFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public CloudFailureKind Kind { get; } = kind;
}
public sealed class CloudSafetyException(string message) : Exception(message);

public static class CloudErrorClassifier
{
    public static CloudFailureKind Classify(Exception exception) => exception switch
    {
        CloudTransportException typed => typed.Kind,
        CloudSafetyException => CloudFailureKind.Safety,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => CloudFailureKind.Authentication,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => CloudFailureKind.RateLimited,
        HttpRequestException { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.Locked } => CloudFailureKind.ConcurrentWrite,
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => CloudFailureKind.Transient,
        HttpRequestException { StatusCode: null } or TimeoutException or TaskCanceledException => CloudFailureKind.Transient,
        _ => CloudFailureKind.Unknown
    };
}
