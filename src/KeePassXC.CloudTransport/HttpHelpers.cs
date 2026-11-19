using System.Net;
using System.Text.Json;

namespace KeePassXC.CloudTransport;

internal static class HttpHelpers
{
    internal static HttpClient NewClient(CloudConnectionOptions options, CloudTransportServices services)
    {
        // Injected handler is for tests. Production must never follow redirects with credentials.
        HttpMessageHandler handler = services.HttpHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        return new HttpClient(handler, disposeHandler: services.HttpHandler is null) { Timeout = options.Timeout };
    }
    internal static void Ensure(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var code = response.StatusCode;
        var n = (int)code;
        if (code is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.Locked)
            throw new CloudTransportException("CONCURRENT_WRITE", CloudFailureKind.ConcurrentWrite, operation + ": conditional write rejected (HTTP " + n + ")");
        if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CloudTransportException("NEEDS_AUTH", CloudFailureKind.Authentication, operation + ": access denied (HTTP " + n + ")");
        if (code is HttpStatusCode.TooManyRequests)
            throw new CloudTransportException("RATE_LIMIT", CloudFailureKind.RateLimited, operation + ": rate limited");
        if (code is HttpStatusCode.RequestTimeout || n >= 500)
            throw new CloudTransportException("TEMPORARY", CloudFailureKind.Transient, operation + ": remote service unavailable (HTTP " + n + ")");
        throw new HttpRequestException(operation + ": HTTP " + n, null, code);
    }
    internal static async Task<JsonDocument> ReadJsonLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new CloudSafetyException("Remote JSON response too large");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffered = new MemoryStream();
        var bytes = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(bytes, ct)) != 0)
        {
            if (buffered.Length + count > maxBytes)
                throw new CloudSafetyException("Remote JSON response exceeds safety limit");
            await buffered.WriteAsync(bytes.AsMemory(0, count), ct);
        }
        buffered.Position = 0;
        return await JsonDocument.ParseAsync(buffered, new JsonDocumentOptions { MaxDepth = 32 }, ct);
    }
    internal static StreamContent UploadContent(string source, TransferMeter meter)
        => new(new MeteredReadStream(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read), meter));

    internal static async Task DownloadToNewFileAsync(HttpResponseMessage response, string destination,
        CancellationToken ct, TransferMeter? meter = null)
    {
        meter?.Begin(response.Content.Headers.ContentLength);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = await input.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            meter?.Add(n);
        }
        await output.FlushAsync(ct);
        if (response.Content.Headers.ContentLength is long expected && output.Length != expected)
            throw new IOException("Downloaded payload differs from declared Content-Length");
        meter?.Confirm();
        meter?.Complete();
    }
}
