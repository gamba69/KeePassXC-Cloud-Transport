namespace KeePassXC.CloudTransport;

/// <summary>In-process exclusion for active CLI sessions versus managed CLI binary replacement.</summary>
public static class ProtonCliLease
{
    private static readonly object Gate = new();
    private static int _users;
    private static bool _updating;
    public static IDisposable BeginUse()
    {
        lock (Gate)
        {
            while (_updating) Monitor.Wait(Gate);
            _users++;
            return new Handle(false);
        }
    }
    /// <summary>Returns null if a CLI is in use; caller must not replace until an update lease is acquired.</summary>
    public static IDisposable? TryBeginUpdate()
    {
        lock (Gate)
        {
            if (_updating || _users > 0) return null;
            _updating = true;
            return new Handle(true);
        }
    }
    private sealed class Handle(bool update) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (update) _updating = false;
                else _users--;
                Monitor.PulseAll(Gate);
            }
        }
    }
}
