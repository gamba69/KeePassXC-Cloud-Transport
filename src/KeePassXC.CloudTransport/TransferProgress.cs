using System.Diagnostics;

namespace KeePassXC.CloudTransport;

/// <summary>One transfer, including retries. Progress is advisory; only Completed confirms success.</summary>
public enum CloudTransferDirection { Upload, Download }
public enum CloudTransferPhase { Preparing, Transferring, Confirming, Completed, Skipped }

/// <summary>Byte counts cover file payload, not HTTP multipart framing or Proton encryption overhead.
/// For Proton CLI, HasLiveByteProgress is false: byte counts are available only at completion.</summary>
public sealed record CloudTransferProgress(
    Guid TransferId,
    CloudProvider Provider,
    CloudTransferDirection Direction,
    CloudTransferPhase Phase,
    long TransferredBytes,
    long? TotalBytes,
    bool HasLiveByteProgress);

/// <summary>One reporter per invocation. No file path, OAuth data or credentials ever leave this class.</summary>
internal sealed class TransferMeter
{
    private readonly IProgress<CloudTransferProgress>? _sink;
    private readonly Guid _id = Guid.NewGuid();
    private readonly CloudProvider _provider;
    private readonly CloudTransferDirection _direction;
    private readonly bool _live;
    private long _bytes;
    private long? _total;
    private long _lastTick;

    internal TransferMeter(CloudProvider provider, CloudTransferDirection direction,
        IProgress<CloudTransferProgress>? sink, bool live = true)
    {
        _provider = provider;
        _direction = direction;
        _sink = sink;
        _live = live;
    }

    internal void Begin(long? total)
    {
        _total = total is >= 0 ? total : null;
        Emit(CloudTransferPhase.Preparing);
        Emit(CloudTransferPhase.Transferring);
    }

    internal void Add(long count)
    {
        if (count <= 0) return;
        _bytes += count;
        if (Stopwatch.GetElapsedTime(_lastTick).TotalMilliseconds >= 100)
        {
            _lastTick = Stopwatch.GetTimestamp();
            Emit(CloudTransferPhase.Transferring);
        }
    }

    internal void Confirm() => Emit(CloudTransferPhase.Confirming);

    internal void Complete()
    {
        // The final actual payload size is authoritative if the server omitted Content-Length.
        _total ??= _bytes;
        Emit(CloudTransferPhase.Completed);
    }

    internal void CompleteFromCli(long transferredBytes, bool skipped = false)
    {
        _bytes = Math.Max(0, transferredBytes);
        if (!skipped) Confirm();
        Emit(skipped ? CloudTransferPhase.Skipped : CloudTransferPhase.Completed);
    }

    private void Emit(CloudTransferPhase phase)
    {
        if (_sink is null) return;
        try
        {
            _sink.Report(new CloudTransferProgress(_id, _provider, _direction, phase,
                _bytes, _total, _live));
        }
        catch (Exception)
        {
            // A diagnostic/UI callback must not cancel a successful upload or download.
        }
    }
}

/// <summary>Counts payload bytes read by HttpClient without buffering entire uploads.</summary>
internal sealed class MeteredReadStream : Stream
{
    private readonly Stream _inner;
    private readonly TransferMeter _meter;
    internal MeteredReadStream(Stream inner, TransferMeter meter)
    {
        _inner = inner;
        _meter = meter;
        _meter.Begin(inner.CanSeek ? inner.Length - inner.Position : null);
    }
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        _meter.Add(n);
        return n;
    }
    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        _meter.Add(n);
        return n;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
        _meter.Add(n);
        return n;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        _meter.Add(n);
        return n;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
