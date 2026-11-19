using System.Net;
using KeePassXC.CloudTransport;

internal static class TransferProgressTests
{
    private sealed class Capture : IProgress<CloudTransferProgress>
    {
        internal readonly List<CloudTransferProgress> Items = [];
        public void Report(CloudTransferProgress value) => Items.Add(value);
        internal void Reset() => Items.Clear();
        internal void Check(CloudProvider provider, CloudTransferDirection direction, int bytes)
        {
            if (Items.Count < 4 || Items[0].Phase != CloudTransferPhase.Preparing ||
                Items[^1].Phase != CloudTransferPhase.Completed ||
                Items[^1].TransferredBytes != bytes || Items[^1].TotalBytes != bytes ||
                !Items.Any(x => x.Phase == CloudTransferPhase.Confirming) ||
                Items.Any(x => x.Provider != provider || x.Direction != direction ||
                    x.TransferId != Items[0].TransferId))
                throw new InvalidOperationException("Incorrect transfer progress sequence or byte counts");
        }
    }
    private sealed class Secrets : ICloudSecretStore
    {
        public (string User, string Password) Read(string key) => key == "s3"
            ? ("TEST_ACCESS_ID", "TEST_SECRET_ONLY") : ("demo", "TEST_PASSWORD_ONLY");
        public void Write(string key, string user, string secret) { }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => action(req, ct);
    }
    private sealed class UnknownContent(byte[] contents) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(contents).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class ProtonRunner(byte[] contents) : ICloudCliRunner
    {
        public Task<string> RunAsync(string executable, TimeSpan timeout, CancellationToken ct, params string[] args)
        {
            if (args[0] != "filesystem") throw new InvalidOperationException("Unexpected test command");
            return args[1] switch
            {
                "list" => Task.FromResult("[{\"name\":\"config.bin\",\"type\":\"file\"}]"),
                "upload" => Task.FromResult("{\"failedItems\":0,\"transferredItems\":1,\"transferredBytes\":" + contents.Length + "}"),
                "download" => Download(args[3]),
                _ => throw new InvalidOperationException("Unexpected test command")
            };
        }
        private Task<string> Download(string folder)
        {
            File.WriteAllBytes(Path.Combine(folder, "config.bin"), contents);
            return Task.FromResult("{\"failedItems\":0,\"transferredItems\":1,\"transferredBytes\":" + contents.Length + "}");
        }
    }

    internal static async Task RunAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "cloud-progress-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var source = Path.Combine(path, "src.bin");
            var destination = Path.Combine(path, "received.bin");
            var contents = Enumerable.Range(0, 77777).Select(i => (byte)(i % 251)).ToArray();
            await File.WriteAllBytesAsync(source, contents);
            var progress = new Capture();
            var handler = new Handler(async (req, ct) =>
            {
                if (req.Method == HttpMethod.Put)
                {
                    var uploaded = await req.Content!.ReadAsByteArrayAsync(ct);
                    if (!uploaded.SequenceEqual(contents)) throw new Exception("Incorrect uploaded payload");
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }
                if (req.Method == HttpMethod.Get)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contents) };
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            var services = new CloudTransportServices
            {
                Secrets = new Secrets(), HttpHandler = handler, TransferProgress = progress
            };
            using (var dav = new WebDavTransport(new CloudConnectionOptions
                { Provider = CloudProvider.WebDav, Endpoint = "https://dav.example.invalid/", CredentialTarget = "dav" }, services))
            {
                await dav.UploadAsync(source, "config.bin", CloudWriteMode.Replace, CancellationToken.None);
                progress.Check(CloudProvider.WebDav, CloudTransferDirection.Upload, contents.Length);
                progress.Reset();
                if (!await dav.DownloadAsync("config.bin", destination, CancellationToken.None)) throw new Exception("Not found");
                if (!File.ReadAllBytes(destination).SequenceEqual(contents)) throw new Exception("Downloaded payload mismatch");
                progress.Check(CloudProvider.WebDav, CloudTransferDirection.Download, contents.Length);
                File.Delete(destination);
                progress.Reset();
                await ((IConditionalCloudTransport)dav).UploadConditionalAsync(source, "config.bin", null, CancellationToken.None);
                progress.Check(CloudProvider.WebDav, CloudTransferDirection.Upload, contents.Length);
            }
            progress.Reset();
            using (var s3 = new S3Transport(new CloudConnectionOptions
                { Provider = CloudProvider.S3, Endpoint = "https://s3.example.invalid", Bucket = "sample-bucket", S3Region = "us-east-1", CredentialTarget = "s3" }, services))
            {
                await s3.UploadAsync(source, "config.bin", CloudWriteMode.Replace, CancellationToken.None);
                progress.Check(CloudProvider.S3, CloudTransferDirection.Upload, contents.Length);
                progress.Reset();
                if (!await s3.DownloadAsync("config.bin", destination, CancellationToken.None)) throw new Exception("Not found");
                progress.Check(CloudProvider.S3, CloudTransferDirection.Download, contents.Length);
                progress.Reset();
                await ((IConditionalCloudTransport)s3).UploadConditionalAsync(source, "config.bin", null, CancellationToken.None);
                progress.Check(CloudProvider.S3, CloudTransferDirection.Upload, contents.Length);
            }
            // Unknown Content-Length: live bytes remain real, percentage is unavailable until completion.
            progress.Reset();
            var unknown = new CloudTransportServices
            {
                Secrets = new Secrets(), TransferProgress = progress,
                HttpHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new UnknownContent(contents) }))
            };
            using (var dav = new WebDavTransport(new CloudConnectionOptions
                { Provider = CloudProvider.WebDav, Endpoint = "https://dav.example.invalid/", CredentialTarget = "dav" }, unknown))
            {
                File.Delete(destination);
                await dav.DownloadAsync("config.bin", destination, CancellationToken.None);
                if (progress.Items[0].TotalBytes is not null ||
                    progress.Items[^1].TotalBytes != contents.Length)
                    throw new Exception("Unknown-length progress was fabricated");
            }
            // CLI does not deliver intermediate bytes, even when it knows the local file size.
            progress.Reset();
            var fakeCli = Path.Combine(path, "test-proton-cli.exe");
            File.WriteAllText(fakeCli, "synthetic CLI");
            using (var proton = new ProtonDriveTransport(new CloudConnectionOptions
                { Provider = CloudProvider.ProtonDrive, ProtonCliExecutable = fakeCli },
                new CloudTransportServices { Secrets = new Secrets(), Cli = new ProtonRunner(contents), TransferProgress = progress }))
            {
                await proton.UploadAsync(source, "config.bin", CloudWriteMode.Replace, CancellationToken.None);
                if (progress.Items[^1].TransferredBytes != contents.Length ||
                    progress.Items[^1].Phase != CloudTransferPhase.Completed ||
                    progress.Items.Any(p => p.HasLiveByteProgress ||
                        p.Phase == CloudTransferPhase.Transferring && p.TransferredBytes != 0))
                    throw new Exception("Proton emitted unconfirmed live bytes");
                progress.Reset();
                File.Delete(destination);
                await proton.DownloadAsync("config.bin", destination, CancellationToken.None);
                if (progress.Items[^1].TransferredBytes != contents.Length ||
                    progress.Items[^1].HasLiveByteProgress ||
                    !File.ReadAllBytes(destination).SequenceEqual(contents))
                    throw new Exception("Proton did not report confirmed download bytes");
            }
            // A wrong server length must never produce a false Completed signal.
            progress.Reset();
            var wrongLength = new CloudTransportServices
            {
                Secrets = new Secrets(), TransferProgress = progress,
                HttpHandler = new Handler((_, _) =>
                {
                    var content = new ByteArrayContent(contents);
                    content.Headers.ContentLength = contents.Length + 1;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                })
            };
            using (var dav = new WebDavTransport(new CloudConnectionOptions
                { Provider = CloudProvider.WebDav, Endpoint = "https://dav.example.invalid/", CredentialTarget = "dav" }, wrongLength))
            {
                File.Delete(destination);
                try { await dav.DownloadAsync("config.bin", destination, CancellationToken.None); throw new Exception("Expected size mismatch"); }
                catch (IOException) { }
                if (progress.Items.Any(x => x.Phase == CloudTransferPhase.Completed))
                    throw new Exception("Incomplete download reported success");
            }
            // Cancellation is not success and must never emit Completed.
            progress.Reset();
            using (var canceled = new CancellationTokenSource())
            using (var dav = new WebDavTransport(new CloudConnectionOptions
                { Provider = CloudProvider.WebDav, Endpoint = "https://dav.example.invalid/", CredentialTarget = "dav" }, services))
            {
                canceled.Cancel();
                try
                {
                    await dav.UploadAsync(source, "config.bin", CloudWriteMode.Replace, canceled.Token);
                    throw new Exception("Expected canceled transfer");
                }
                catch (OperationCanceledException) { }
                if (progress.Items.Any(x => x.Phase == CloudTransferPhase.Completed))
                    throw new Exception("Canceled transfer reported success");
            }
            // A transport failure must never publish a misleading Completed notification.
            progress.Reset();
            var broken = new CloudTransportServices
            {
                Secrets = new Secrets(), TransferProgress = progress,
                HttpHandler = new Handler((_, _) => throw new IOException("TEST_NETWORK_FAILURE"))
            };
            using (var dav = new WebDavTransport(new CloudConnectionOptions
                { Provider = CloudProvider.WebDav, Endpoint = "https://dav.example.invalid/", CredentialTarget = "dav" }, broken))
            {
                try { await dav.UploadAsync(source, "config.bin", CloudWriteMode.Replace, CancellationToken.None); throw new Exception("Expected failure"); }
                catch (IOException) { }
                if (progress.Items.Any(x => x.Phase == CloudTransferPhase.Completed))
                    throw new Exception("Failed transfer reported completion");
            }
        }
        finally { Directory.Delete(path, recursive: true); }
    }
}
