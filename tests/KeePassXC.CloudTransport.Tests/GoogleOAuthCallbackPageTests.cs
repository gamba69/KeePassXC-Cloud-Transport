using System.Net;
using System.Net.Http;
using KeePassXC.CloudTransport;

internal static class GoogleOAuthCallbackPageTests
{
    internal static async Task RunAsync()
    {
        await ExerciseAsync(custom: true, success: true);
        await ExerciseAsync(custom: true, success: false);
        await ExerciseAsync(custom: false, success: true);
        var browser = new CallbackBrowser(false);
        using var http = new HttpClient(new OAuthHandler());
        var services = new CloudTransportServices
        {
            Secrets = new TestSecrets(), Browser = browser,
            GoogleOAuthCallbackPages = new GoogleOAuthCallbackPages(
                new string('x', 131073), "<html>Failure</html>")
        };
        var session = new GoogleOAuthSession(Options(), services, http);
        try { await session.LoginAsync(CancellationToken.None); throw new Exception("Oversized HTML accepted"); }
        catch (CloudSafetyException) { }
        if (browser.Request is not null) throw new Exception("Browser opened before callback-page validation");
    }

    private static CloudConnectionOptions Options() => new()
    {
        Provider = CloudProvider.GoogleDrive,
        GoogleClientId = "TEST-CARDPRINT-DESKTOP",
        GoogleRefreshTokenTarget = "TEST-GOOGLE-REFRESH"
    };

    private static async Task ExerciseAsync(bool custom, bool success)
    {
        var browser = new CallbackBrowser(!success);
        var secrets = new TestSecrets();
        using var http = new HttpClient(new OAuthHandler());
        var services = new CloudTransportServices
        {
            Secrets = secrets, Browser = browser,
            GoogleOAuthCallbackPages = custom ? new GoogleOAuthCallbackPages(
                "<!doctype html><html><head><meta charset=\"utf-8\"></head><body><style>body{color:#fff}</style><main>✓ AUTH_OK</main></body></html>",
                "<!doctype html><html><body><style>body{color:#f55}</style><main>AUTH_FAIL</main></body></html>") : null
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var session = new GoogleOAuthSession(Options(), services, http);
        if (success)
        {
            var email = await session.LoginAsync(timeout.Token);
            if (email != "demo@example.test") throw new Exception("Incorrect authenticated account");
            if (secrets.LastWrite is null) throw new Exception("Refresh token was not saved");
        }
        else
        {
            try { await session.LoginAsync(timeout.Token); throw new Exception("Invalid OAuth state accepted"); }
            catch (CloudSafetyException) { }
            if (secrets.LastWrite is not null) throw new Exception("Denied authorization saved credentials");
        }
        var result = await (browser.Request ?? throw new Exception("No callback request"));
        if (result.Status != (success ? HttpStatusCode.OK : HttpStatusCode.BadRequest))
            throw new Exception("Wrong HTTP callback status");
        var expected = custom ? success ? "AUTH_OK" : "AUTH_FAIL"
            : success ? "Authorization completed" : "Authorization not completed";
        if (!result.Html.Contains(expected, StringComparison.Ordinal))
            throw new Exception("Callback HTML was not returned");
        if (result.Html.Contains("code=", StringComparison.Ordinal) ||
            result.Html.Contains("TEST-REFRESH-TOKEN", StringComparison.Ordinal))
            throw new Exception("Callback page exposed OAuth credentials");
        if (!result.Csp.Contains("script-src 'none'", StringComparison.Ordinal))
            throw new Exception("OAuth callback scripts are not blocked");
        if (custom && !result.Csp.Contains("style-src 'unsafe-inline'", StringComparison.Ordinal))
            throw new Exception("Custom OAuth callback inline styles are blocked");
        if (!custom && !result.Csp.Contains("style-src 'none'", StringComparison.Ordinal))
            throw new Exception("Default OAuth CSP changed");
    }

    private sealed class CallbackBrowser(bool deny) : ICloudBrowserLauncher
    {
        public Task<CallbackResult>? Request { get; private set; }
        public void Open(string absoluteUrl) => Request = Task.Run(async () =>
        {
            var uri = new Uri(absoluteUrl);
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2))
                .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]),
                    StringComparer.Ordinal);
            var callback = query["redirect_uri"] + "?state=" +
                Uri.EscapeDataString(deny ? "bad-state" : query["state"]) +
                (deny ? "&error=access_denied" : "&code=TEST-AUTH-CODE");
            using var local = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var response = await local.GetAsync(callback);
            var html = await response.Content.ReadAsStringAsync();
            var csp = response.Headers.TryGetValues("Content-Security-Policy", out var values)
                ? string.Join(";", values) : "";
            return new CallbackResult(response.StatusCode, html, csp);
        });
    }

    private sealed record CallbackResult(HttpStatusCode Status, string Html, string Csp);

    private sealed class TestSecrets : ICloudSecretStore
    {
        public string? LastWrite { get; private set; }
        public (string User, string Password) Read(string target) => ("", "");
        public void Write(string target, string user, string secret) => LastWrite = secret;
    }

    private sealed class OAuthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? "";
            var body = url.Contains("oauth2.googleapis.com/token", StringComparison.Ordinal)
                ? "{\"access_token\":\"TEST-ACCESS-TOKEN\",\"refresh_token\":\"TEST-REFRESH-TOKEN\",\"expires_in\":3600}"
                : url.Contains("/drive/v3/about?", StringComparison.Ordinal)
                    ? "{\"user\":{\"emailAddress\":\"demo@example.test\"}}"
                    : throw new Exception("Unexpected mocked OAuth endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body) });
        }
    }
}
