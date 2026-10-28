namespace CloudSSD.Core;

/// <summary>
/// rclone's log does not say which process issued a read. CloudSSD's own prefetch reads are registered here
/// (path, byte range, time window) so telemetry can separate them from the game's reads. A read is attributed to
/// prefetch only when its range overlaps a registered range AND it started inside that prefetch's time window.
/// </summary>
public sealed class ReadAttributionRegistry
{
    private sealed class Entry
    {
        public required string Path;
        public required long Start;
        public required long End;
        public required DateTime Begin;
        public DateTime? Finish;
    }

    private readonly List<Entry> _entries = new();
    private readonly object _gate = new();
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);

    public IDisposable Register(string path, long offset, long length, IClock? clock = null)
    {
        var now = (clock ?? SystemClock.Instance).Now;
        var e = new Entry { Path = Normalize(path), Start = offset, End = offset + length, Begin = now };
        lock (_gate)
        {
            _entries.Add(e);
            if (_entries.Count > 512) _entries.RemoveAll(x => x.Finish is { } f && now - f > Retention);
        }
        return new Finisher(this, e, clock ?? SystemClock.Instance);
    }

    public bool IsPrefetch(string path, long offset, long length, DateTime readStart)
    {
        var p = Normalize(path);
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                if (!string.Equals(e.Path, p, StringComparison.OrdinalIgnoreCase)) continue;
                if (offset >= e.End || offset + length <= e.Start) continue;
                if (readStart < e.Begin - Slack) continue;
                if (e.Finish is { } f && readStart > f + Slack) continue;
                return true;
            }
        }
        return false;
    }

    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private sealed class Finisher(ReadAttributionRegistry owner, Entry e, IClock clock) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) e.Finish ??= clock.Now;
        }
    }
}
