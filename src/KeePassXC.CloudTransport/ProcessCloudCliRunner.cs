using System.Diagnostics;
using System.Text;

namespace KeePassXC.CloudTransport;

/// <summary>Safe non-shell, output-limited command runner for official Proton CLI JSON operations.</summary>
public sealed class ProcessCloudCliRunner : ICloudCliRunner
{
    private static async Task<string> ReadLimitedAsync(StreamReader input, int maxChars, CancellationToken ct)
    {
        var result = new StringBuilder();
        var block = new char[4096];
        int n;
        while ((n = await input.ReadAsync(block.AsMemory(), ct)) != 0)
        {
            if (result.Length + n > maxChars) throw new CloudSafetyException("Proton CLI output exceeds safety limit");
            result.Append(block, 0, n);
        }
        return result.ToString();
    }
    public async Task<string> RunAsync(string executable, TimeSpan timeout, CancellationToken ct, params string[] args)
    {
        if (!System.IO.Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new CloudTransportException("PROTON_CLI_MISSING", CloudFailureKind.Configuration, "Official Proton CLI executable not found");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg); // No shell expansion or command interpolation.
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new CloudTransportException("CLI_START", CloudFailureKind.Configuration, "Proton CLI could not start");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            // Drain both pipes concurrently; never include raw stderr or user data in exceptions.
            var stdoutTask = ReadLimitedAsync(process.StandardOutput, 12_000_000, deadline.Token);
            var stderrTask = ReadLimitedAsync(process.StandardError, 1_000_000, deadline.Token);
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(deadline.Token));
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                // CLI wording varies by release; only well-known auth conditions are classified.
                var normalized = stderr.ToLowerInvariant();
                if (new[] { "not logged in", "not authenticated", "authentication required", "login required", "session expired" }
                    .Any(normalized.Contains))
                    throw new CloudTransportException("NEEDS_AUTH", CloudFailureKind.Authentication, "Proton Drive session requires authorization");
                throw new CloudTransportException("CLI_FAILED", CloudFailureKind.Unknown,
                    "Proton CLI command failed (exit " + process.ExitCode + "); inspect its session outside the application");
            }
            return await stdoutTask;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new CloudTransportException("CLI_TIMEOUT", CloudFailureKind.Transient, "Proton CLI operation timed out");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }
    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
