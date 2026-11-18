using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KeePassXC.CloudTransport;

/// <summary>Desktop OAuth + PKCE with independent host-provided credential storage.</summary>
public sealed class GoogleOAuthSession
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private readonly CloudConnectionOptions _options;
    private readonly CloudTransportServices _services;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expires;
    public GoogleOAuthSession(CloudConnectionOptions options, CloudTransportServices services, HttpClient client)
    {
        _options = options;
        _services = services;
        _http = client;
    }
    private string ClientId => _options.GoogleClientId ?? throw new CloudTransportException("OAUTH_CONFIGURATION",
        CloudFailureKind.Configuration, "Google OAuth client ID is required");
    private string Target => _options.GoogleRefreshTokenTarget ?? throw new CloudTransportException("OAUTH_CONFIGURATION",
        CloudFailureKind.Configuration, "Google refresh-token credential target is required");
    private string? ClientSecret()
    {
        if (string.IsNullOrWhiteSpace(_options.GoogleClientSecretTarget)) return _options.GoogleClientSecret;
        var stored = _services.Secrets.Read(_options.GoogleClientSecretTarget);
        if (!string.Equals(stored.User, ClientId, StringComparison.Ordinal))
            throw new CloudTransportException("OAUTH_CONFIGURATION", CloudFailureKind.Configuration, "Google OAuth client secret belongs to a different client");
        return stored.Password;
    }
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private Dictionary<string, string> Fields(string grant)
    {
        var fields = new Dictionary<string, string> { ["client_id"] = ClientId, ["grant_type"] = grant };
        var secret = ClientSecret();
        if (!string.IsNullOrWhiteSpace(secret)) fields["client_secret"] = secret;
        return fields;
    }
    private static CloudTransportException OAuthFailure(HttpStatusCode status, string json)
    {
        string? code = null;
        if (json.Length < 8192)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                code = doc.RootElement.TryGetProperty("error", out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
            }
            catch (JsonException) { }
        }
        return code switch
        {
            "invalid_grant" => new("OAUTH_INVALID_GRANT", CloudFailureKind.Authentication, "Google OAuth refresh token is expired or revoked"),
            "access_denied" => new("OAUTH_ACCESS_DENIED", CloudFailureKind.Authentication, "Google OAuth access denied"),
            "invalid_client" or "unauthorized_client" => new("OAUTH_INVALID_CLIENT", CloudFailureKind.Configuration, "Google OAuth application rejected"),
            "temporarily_unavailable" => new("OAUTH_TEMPORARY", CloudFailureKind.Transient, "Google OAuth temporary failure"),
            _ when status == HttpStatusCode.TooManyRequests => new("RATE_LIMIT", CloudFailureKind.RateLimited, "Google OAuth request rate limited"),
            _ when (int)status >= 500 => new("OAUTH_TEMPORARY", CloudFailureKind.Transient, "Google OAuth service temporarily unavailable"),
            _ => new("OAUTH_REJECTED", CloudFailureKind.Configuration, "Google OAuth rejected request (HTTP " + (int)status + ")")
        };
    }
    private async Task<JsonDocument> ExchangeAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw OAuthFailure(res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        return await HttpHelpers.ReadJsonLimitedAsync(res, 128_000, ct);
    }
    private void StoreAccess(JsonElement json)
    {
        var access = json.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(access)) throw new CloudSafetyException("Google OAuth did not return access token");
        var seconds = json.TryGetProperty("expires_in", out var n) && n.TryGetInt32(out var s) ? s : 3600;
        _accessToken = access;
        _expires = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 86400));
    }
    private async Task<string> VerifyAccountAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await _http.SendAsync(req, ct);
        HttpHelpers.Ensure(res, "Google account verification");
        using var json = await HttpHelpers.ReadJsonLimitedAsync(res, 128_000, ct);
        var email = json.RootElement.GetProperty("user").GetProperty("emailAddress").GetString();
        if (string.IsNullOrWhiteSpace(email)) throw new CloudSafetyException("Google account identity was not returned");
        if (!string.IsNullOrWhiteSpace(_options.GoogleExpectedAccount) &&
            !string.Equals(email, _options.GoogleExpectedAccount, StringComparison.OrdinalIgnoreCase))
            throw new CloudSafetyException("Google account differs from the configured account");
        return email;
    }
    public async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && _expires > DateTimeOffset.UtcNow.AddSeconds(90)) return _accessToken;
            var stored = _services.Secrets.Read(Target);
            if (!string.Equals(stored.User, ClientId, StringComparison.Ordinal) || string.IsNullOrEmpty(stored.Password))
                throw new CloudTransportException("NEEDS_AUTH", CloudFailureKind.Authentication, "Google login is required");
            var fields = Fields("refresh_token");
            fields["refresh_token"] = stored.Password;
            using var doc = await ExchangeAsync(fields, ct);
            StoreAccess(doc.RootElement);
            try { _ = await VerifyAccountAsync(_accessToken!, ct); }
            catch { _accessToken = null; _expires = default; throw; }
            if (doc.RootElement.TryGetProperty("refresh_token", out var refreshed) && refreshed.GetString() is { Length: > 0 } newer)
                _services.Secrets.Write(Target, ClientId, newer);
            return _accessToken!;
        }
        finally { _gate.Release(); }
    }
    /// <summary>Call on host credential edits or explicit reauthentication.</summary>
    public void Invalidate() { _accessToken = null; _expires = default; }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        var ix = target.IndexOf('?');
        if (ix < 0 || ix > 4 || !target.StartsWith("/?", StringComparison.Ordinal))
            throw new CloudSafetyException("Invalid OAuth callback path");
        foreach (var pair in target[(ix + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            if (split.Length != 2) throw new CloudSafetyException("Invalid OAuth query parameter");
            var key = Uri.UnescapeDataString(split[0]);
            if (!q.TryAdd(key, Uri.UnescapeDataString(split[1].Replace('+', ' '))))
                throw new CloudSafetyException("Duplicate OAuth query parameter");
        }
        return q;
    }
    private static async Task ReplyAsync(Stream output, bool succeeded)
    {
        var body = "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\"></head><body><p>" +
            (succeeded ? "Authorization completed. You can close this tab." : "Authorization not completed. Return to the application.") +
            "</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {(succeeded ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; style-src 'none'; script-src 'none'\r\nConnection: close\r\n\r\n";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await output.WriteAsync(Encoding.ASCII.GetBytes(header), timeout.Token);
            await output.WriteAsync(bytes, timeout.Token);
            await output.FlushAsync(timeout.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    public async Task<string> LoginAsync(CancellationToken ct)
    {
        if (_services.Browser is null) throw new CloudTransportException("OAUTH_CONFIGURATION", CloudFailureKind.Configuration, "OAuth browser launcher is required");
        await _gate.WaitAsync(ct);
        try
        {
            var verifier = B64(RandomNumberGenerator.GetBytes(32));
            var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = B64(RandomNumberGenerator.GetBytes(24));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var redirect = $"http://127.0.0.1:{port}/";
                var parameters = new Dictionary<string, string> {
                    ["response_type"] = "code", ["access_type"] = "offline", ["prompt"] = "consent", ["client_id"] = ClientId,
                    ["redirect_uri"] = redirect, ["scope"] = "https://www.googleapis.com/auth/drive.file",
                    ["code_challenge_method"] = "S256", ["code_challenge"] = challenge, ["state"] = state
                };
                _services.Browser.Open("https://accounts.google.com/o/oauth2/v2/auth?" +
                    string.Join("&", parameters.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value))));
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var line = await reader.ReadLineAsync(deadline.Token) ?? throw new CloudSafetyException("OAuth callback disconnected");
                if (line.Length > 8192 || !line.StartsWith("GET /?", StringComparison.Ordinal) || !line.EndsWith(" HTTP/1.1", StringComparison.Ordinal))
                    throw new CloudSafetyException("Unexpected OAuth callback");
                var headersEnded = false;
                for (var i = 0; i < 64; i++)
                {
                    var header = await reader.ReadLineAsync(deadline.Token);
                    if (header is null or "") { headersEnded = true; break; }
                    if (header.Length > 8192) throw new CloudSafetyException("Oversized OAuth callback header");
                }
                if (!headersEnded) throw new CloudSafetyException("Too many OAuth callback headers");
                var target = line.Split(' ')[1];
                var query = ParseQuery(target);
                if (!query.TryGetValue("state", out var returned) || returned != state ||
                    !query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                {
                    await ReplyAsync(stream, false);
                    throw new CloudSafetyException("Google OAuth callback invalid or authorization denied");
                }
                try
                {
                    var fields = Fields("authorization_code");
                    fields["redirect_uri"] = redirect; fields["code"] = code; fields["code_verifier"] = verifier;
                    using var response = await ExchangeAsync(fields, deadline.Token);
                    var newToken = response.RootElement.GetProperty("access_token").GetString();
                    if (string.IsNullOrWhiteSpace(newToken)) throw new CloudSafetyException("Google OAuth token response incomplete");
                    var account = await VerifyAccountAsync(newToken, deadline.Token);
                    var refresh = response.RootElement.TryGetProperty("refresh_token", out var item) ? item.GetString() : null;
                    if (string.IsNullOrWhiteSpace(refresh)) throw new CloudSafetyException("Google OAuth did not return offline refresh token");
                    _services.Secrets.Write(Target, ClientId, refresh);
                    StoreAccess(response.RootElement);
                    await ReplyAsync(stream, true);
                    return account;
                }
                catch { await ReplyAsync(stream, false); throw; }
            }
            finally { listener.Stop(); }
        }
        finally { _gate.Release(); }
    }
}
