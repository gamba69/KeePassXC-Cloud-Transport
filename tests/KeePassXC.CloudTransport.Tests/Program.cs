using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KeePassXC.CloudTransport;

var failures = 0;
var count = 0;
async Task Run(string name, Func<Task> action)
{
    try { await action(); Console.WriteLine("[PASS] CloudTransportTests / " + name); count++; }
    catch (Exception e) { failures++; Console.Error.WriteLine("[FAIL] CloudTransportTests / " + name + ": " + e); }
}
void Assert(bool value, string why) { if (!value) throw new Exception(why); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
var secrets = new MemorySecrets();
secrets.Write("dav", "demo", "example_password");
secrets.Write("s3", "AKIDEXAMPLE", "TEST_ONLY_SECRET");
await Run("Google / custom OAuth callback pages", GoogleOAuthCallbackPageTests.RunAsync);
await Run("transfers / byte progress, conditional writes, errors", TransferProgressTests.RunAsync);
var temp = Path.Combine(Path.GetTempPath(), "cloudtransport-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    await Run("path / permits safe nested paths", () => { Assert(CloudPath.Normalize("foo/bar.txt") == "foo/bar.txt", "path"); return Task.CompletedTask; });
    foreach (var bad in new[] { "../x", "/root", "x\\y", "foo//bar", "a/./b", "a/..", "a:", "x?y", "x/" })
        await Run("path / rejects " + bad, () => Throws<CloudSafetyException>(() => { CloudPath.Normalize(bad); return Task.CompletedTask; }));
    await Run("path / combines prefix exactly once", () =>
    {
        Assert(CloudPath.Combine("vault", "one/file") == "vault/one/file", "prefix");
        return Task.CompletedTask;
    });
    await Run("credential / missing S3 key rejected", () => Throws<CloudTransportException>(() =>
    {
        using var t = new S3Transport(new CloudConnectionOptions { Provider = CloudProvider.S3, Endpoint = "https://s3.example.test", Bucket = "bucket", CredentialTarget = "missing" }, new CloudTransportServices { Secrets = secrets });
        return Task.CompletedTask;
    }));
    await Run("WebDAV / rejects HTTP endpoint", () => Throws<CloudTransportException>(() =>
    {
        using var t = new WebDavTransport(new CloudConnectionOptions { Provider = CloudProvider.WebDav, Endpoint = "http://localhost", CredentialTarget = "dav" }, new CloudTransportServices { Secrets = secrets });
        return Task.CompletedTask;
    }));
    var davHandler = new StubHandler(req =>
    {
        if (req.Method.Method == "PROPFIND") return new HttpResponseMessage((HttpStatusCode)207)
        { Content = new StringContent("""
          <?xml version="1.0"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>/base/items/file.json</d:href><d:propstat><d:prop><d:getetag>"rev1"</d:getetag><d:getcontentlength>12</d:getcontentlength><d:getlastmodified>Mon, 17 Nov 2025 12:00:00 GMT</d:getlastmodified></d:prop></d:propstat></d:response></d:multistatus>
          """) };
        if (req.Method == HttpMethod.Head) return new HttpResponseMessage(HttpStatusCode.OK) { Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"rev1\"") } };
        if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    });
    using (var dav = new WebDavTransport(new CloudConnectionOptions { Provider = CloudProvider.WebDav, Endpoint = "https://cloud.example.test/base", RemotePrefix = "items", CredentialTarget = "dav" }, new CloudTransportServices { Secrets = secrets, HttpHandler = davHandler }))
    {
        await Run("WebDAV / connection probe", async () => Assert(await dav.CheckConnectionAsync(default), "probe"));
        await Run("WebDAV / missing means false", async () => Assert(!await dav.DownloadAsync("missing", Path.Combine(temp, "missing"), default), "missing"));
        await Run("WebDAV / strong ETag read", async () => Assert(await dav.ReadRevisionAsync("file.json", default) == "\"rev1\"", "etag"));
        await Run("WebDAV / object metadata", async () => Assert((await dav.GetInfoAsync("file.json", default))?.Revision == "\"rev1\"", "metadata"));
        await Run("WebDAV / conditional update", async () =>
        {
            var file = Path.Combine(temp, "source.bin"); await File.WriteAllTextAsync(file, "test");
            await dav.UploadConditionalAsync(file, "file.json", "\"rev1\"", default);
            Assert(davHandler.Calls.Any(x => x.Method == "PUT" && x.IfMatch == "\"rev1\""), "if-match");
        });
        await Run("WebDAV / list regular object", async () =>
        {
            var files = await dav.ListAsync("", default);
            Assert(files.Count == 1 && files[0].Path == "file.json" && files[0].Revision == "\"rev1\"", "object listing");
        });
    }
    var s3Handler = new StubHandler(req =>
    {
        if (req.Method == HttpMethod.Head)
            return new HttpResponseMessage(HttpStatusCode.OK) { Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag1\"") } };
        if (req.Method == HttpMethod.Get && req.RequestUri?.Query.Contains("list-type=2", StringComparison.Ordinal) == true)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
              <?xml version="1.0" encoding="UTF-8"?><ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><IsTruncated>false</IsTruncated><Contents><Key>settings/config.json</Key><LastModified>2025-11-17T12:00:00Z</LastModified><Size>4</Size><ETag>"etag1"</ETag></Contents></ListBucketResult>
              """) };
        return new HttpResponseMessage(HttpStatusCode.OK);
    });
    using (var s3 = new S3Transport(new CloudConnectionOptions { Provider = CloudProvider.S3, Endpoint = "https://s3.example.test/", Bucket = "bucket", S3Region = "us-east-1", CredentialTarget = "s3", RemotePrefix = "settings" }, new CloudTransportServices { Secrets = secrets, HttpHandler = s3Handler }))
    {
        await Run("S3 / list and prefix strip", async () =>
        {
            var files = await s3.ListAsync("", default);
            Assert(files.Count == 1 && files[0].Path == "config.json", "listing");
        });
        await Run("S3 / signed auth header", () =>
        {
            // Check the parsed and wire-visible Authorization values, not merely that
            // the client attempted to add an unvalidated raw header.
            Assert(s3Handler.Calls.Any(x =>
                x.Authorization is { } authorization &&
                authorization.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", StringComparison.Ordinal) &&
                x.RawAuthorization == authorization &&
                authorization.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", StringComparison.Ordinal) &&
                System.Text.RegularExpressions.Regex.IsMatch(authorization, @"Signature=[0-9a-f]{64}(?:$|,)") &&
                !authorization.Contains("TEST_ONLY_SECRET", StringComparison.Ordinal)), "SigV4 Authorization is missing, malformed, or exposes a secret");
            return Task.CompletedTask;
        });
        await Run("S3 / strong ETag", async () => Assert(await s3.ReadRevisionAsync("config.json", default) == "\"etag1\"", "etag"));
        await Run("S3 / object metadata", async () => Assert((await s3.GetInfoAsync("config.json", default))?.Revision == "\"etag1\"", "metadata"));
        await Run("S3 / conditional PUT", async () =>
        {
            var src = Path.Combine(temp, "s3source"); await File.WriteAllTextAsync(src, "bytes");
            await s3.UploadConditionalAsync(src, "config.json", "\"etag1\"", default);
            Assert(s3Handler.Calls.Any(x => x.Method == "PUT" && x.IfMatch == "\"etag1\""), "conditional header");
        });
    }
    await Run("S3 / rejects invalid bucket", () => Throws<CloudTransportException>(() =>
    {
        using var t = new S3Transport(new CloudConnectionOptions { Provider = CloudProvider.S3, Endpoint = "https://s3.example.test", Bucket = "x", CredentialTarget = "s3" }, new CloudTransportServices { Secrets = secrets });
        return Task.CompletedTask;
    }));
    await Run("R2 / region auto enforced", () => Throws<CloudTransportException>(() =>
    {
        using var t = new S3Transport(new CloudConnectionOptions { Provider = CloudProvider.CloudflareR2, Endpoint = "https://abc.r2.cloudflarestorage.com", Bucket = "my-bucket", S3Region = "us-east-1", CredentialTarget = "s3" }, new CloudTransportServices { Secrets = secrets });
        return Task.CompletedTask;
    }));
    var googleCalls = 0;
    secrets.Write("google-refresh", "demo-client", "test-refresh");
    var google = new StubHandler(req =>
    {
        googleCalls++;
        var u = req.RequestUri!.AbsoluteUri;
        if (u.Contains("oauth2.googleapis.com/token", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"test_access\",\"expires_in\":3600}") };
        if (u.Contains("/about?", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"user\":{\"emailAddress\":\"demo@example.test\"}}") };
        if (u.Contains("/files?", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"files\":[]}") };
        throw new Exception("Unexpected mocked Google URL: " + u);
    });
    using (var drive = new GoogleDriveTransport(new CloudConnectionOptions { Provider = CloudProvider.GoogleDrive, GoogleClientId = "demo-client", GoogleRefreshTokenTarget = "google-refresh", GoogleExpectedAccount = "demo@example.test" }, new CloudTransportServices { Secrets = secrets, HttpHandler = google }))
    {
        await Run("Google / refresh + account validation + read-only probe", async () => Assert(await drive.CheckConnectionAsync(default), "OAuth state"));
        await Run("Google / no repeated refresh on healthy session", async () =>
        {
            var before = googleCalls;
            Assert(await drive.CheckConnectionAsync(default), "repeat probe");
            Assert(googleCalls - before == 1, "cached token should only list files");
        });
        await Run("Google / not-found download", async () => Assert(!await drive.DownloadAsync("none.json", Path.Combine(temp, "none"), default), "missing"));
        await Run("Google / not-found metadata", async () => Assert(await drive.GetInfoAsync("none.json", default) is null, "metadata"));
    }
    var protonFile = Path.Combine(temp, "official-cli-placeholder.exe");
    await File.WriteAllTextAsync(protonFile, "dummy test executable");
    var cli = new StubCli();
    using (var proton = new ProtonDriveTransport(new CloudConnectionOptions { Provider = CloudProvider.ProtonDrive, ProtonCliExecutable = protonFile, RemotePrefix = "cardprint" },
        new CloudTransportServices { Secrets = secrets, Cli = cli, ProtonLogin = new StubLogin() }))
    {
        await Run("Proton / session probe via official read-only command", async () => Assert(await proton.CheckConnectionAsync(default), "cli list"));
        await Run("Proton / login followed by session probe", async () => Assert(await proton.LoginAsync(default), "login"));
        await Run("Proton / listing within prefix", async () => Assert((await proton.ListAsync("", default)).Count == 0, "proton list"));
        await Run("Proton / missing metadata", async () => Assert(await proton.GetInfoAsync("missing.json", default) is null, "metadata"));
        await Run("Proton / refuses path escape", () => Throws<CloudSafetyException>(() => proton.DownloadAsync("../other", Path.Combine(temp, "escape"), default)));
        await Run("Proton / lacks CAS capability", () =>
        {
            ICloudTransport transport = proton;
            Assert(!transport.Capabilities.AtomicServerCompareAndSwap && transport is not IConditionalCloudTransport, "proton CAS");
            return Task.CompletedTask;
        });
    }
    await Run("errors / HTTP auth classification", () =>
    {
        Assert(CloudErrorClassifier.Classify(new HttpRequestException("denied", null, HttpStatusCode.Forbidden)) == CloudFailureKind.Authentication, "auth classify");
        return Task.CompletedTask;
    });
}
finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
Console.WriteLine($"[SUMMARY] CloudTransportTests: {count} PASS, {failures} FAIL");
return failures == 0 && count >= 20 ? 0 : 1;

sealed class MemorySecrets : ICloudSecretStore
{
    private readonly Dictionary<string, (string User, string Password)> _items = new(StringComparer.Ordinal);
    public (string User, string Password) Read(string target) => _items.GetValueOrDefault(target, ("", ""));
    public void Write(string target, string user, string secret) => _items[target] = (user, secret);
}
sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
{
    public List<(string Method, string? IfMatch, string? Authorization, string? RawAuthorization)> Calls { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var raw = request.Headers.TryGetValues("Authorization", out var values)
            ? values.SingleOrDefault() : null;
        Calls.Add((request.Method.Method, request.Headers.IfMatch.FirstOrDefault()?.ToString(),
            request.Headers.Authorization?.ToString(), raw));
        return Task.FromResult(action(request));
    }
}
sealed class StubCli : ICloudCliRunner
{
    public Task<string> RunAsync(string executable, TimeSpan timeout, CancellationToken ct, params string[] args)
    {
        if (args.Length >= 2 && args[0] == "filesystem" && args[1] == "list") return Task.FromResult("[]");
        throw new InvalidOperationException("Unexpected fake CLI action");
    }
}
sealed class StubLogin : IProtonLoginLauncher
{
    public Task<bool> StartLoginAsync(string executable, CancellationToken ct) => Task.FromResult(true);
}
