using CloudSSD.Core;

namespace CloudSSD.Streaming;

/// <summary>A queued prefetch of one block. Identity is (MountId, MountPath, Block index).</summary>
public sealed class PrefetchItem
{
    public required string MountId { get; init; }
    /// <summary>Path relative to the mount root (used for the file read and telemetry attribution).</summary>
    public required string MountPath { get; init; }
    /// <summary>Block key relative to the game root (used for profiles and accounting).</summary>
    public required BlockKey Block { get; init; }
    public required long Offset { get; init; }
    public required int Length { get; init; }
    /// <summary>Who requested it ("game:&lt;id&gt;" or "prepare:&lt;id&gt;"), used for cancellation and accounting.</summary>
    public string Owner { get; init; } = "";
    /// <summary>Overrides the per-priority expiry (e.g. explicit Optimize &amp; Prepare work must not silently expire).</summary>
    public TimeSpan? Ttl { get; init; }
    /// <summary>Speculative items consume the session prefetch budget; explicitly requested ones (Prepare) do not.</summary>
    public bool Speculative { get; init; } = true;
    public Priority Priority { get; internal set; }
    public double Confidence { get; internal set; }
    public string Predictor { get; internal set; } = "";
    public DateTime EnqueuedAt { get; internal set; }
    public long Sequence { get; internal set; }
    internal int Version;
    public bool Cancelled { get; internal set; }

    public (string, string, long) Key => (MountId, MountPath.ToLowerInvariant(), Block.Index);
}

public enum EnqueueResult { Queued, Upgraded, Duplicate, AlreadyCached, InFlight, OverBudget, Rejected }

/// <summary>
/// Thread-safe prefetch queue with four guarantees: strict priority order (P1 before P2 before P3, higher confidence
/// first), deduplication of identical blocks, in-place reprioritisation when a block is predicted again with higher
/// priority, and expiry of stale speculative work. Contains no I/O, so the live scheduler and the deterministic
/// simulator share it.
/// </summary>
public sealed class PrefetchQueue
{
    private readonly PriorityQueue<(PrefetchItem Item, int Version), (int, double, long)> _pq = new();
    private readonly Dictionary<(string, string, long), PrefetchItem> _queued = new();
    private readonly HashSet<(string, string, long)> _inFlight = new();
    // Short-term memory of blocks known to be in rclone's cache (from telemetry or completed prefetches). Entries expire
    // because rclone may evict data at any time; the on-disk range index (CacheIndex) remains the authority.
    private readonly Dictionary<(string, string, long), DateTime> _cached = new();
    private readonly object _gate = new();
    private readonly IClock _clock;
    private long _seq;

    public PrefetchQueue(IClock? clock = null) => _clock = clock ?? SystemClock.Instance;

    /// <summary>Maximum age per priority before a queued item is considered stale and dropped.</summary>
    public Dictionary<Priority, TimeSpan> Ttl { get; } = new()
    {
        [Priority.P1Imminent] = TimeSpan.FromSeconds(30),
        [Priority.P2Likely] = TimeSpan.FromSeconds(120),
        [Priority.P3Background] = TimeSpan.FromMinutes(30),
    };

    /// <summary>How long a "known cached" observation is trusted before the block may be scheduled again.</summary>
    public TimeSpan KnownCachedTtl { get; set; } = TimeSpan.FromMinutes(10);

    private bool KnownCached((string, string, long) key) => _cached.TryGetValue(key, out var t) && _clock.Now - t < KnownCachedTtl;

    /// <summary>Speculative byte budget remaining (null = unlimited).</summary>
    public long? BudgetBytes { get; set; }

    public long Deduplicated { get; private set; }
    public long Expired { get; private set; }
    public long CancelledCount { get; private set; }
    public long Upgraded { get; private set; }

    /// <summary>
    /// Raised (outside the lock) whenever a queued item leaves the queue without being fetched: expired, over budget,
    /// cancelled or superseded by a cache hit. Anyone waiting for an item must handle this or it would wait forever.
    /// </summary>
    public event Action<PrefetchItem, string>? Dropped;

    private void RaiseDropped(List<(PrefetchItem, string)>? dropped)
    {
        if (dropped is null) return;
        foreach (var (i, reason) in dropped)
        {
            try { Dropped?.Invoke(i, reason); } catch (Exception) { }
        }
    }

    public int Count { get { lock (_gate) return _queued.Count; } }
    public int InFlightCount { get { lock (_gate) return _inFlight.Count; } }

    public int CountAtOrAbove(Priority p)
    {
        lock (_gate) return _queued.Values.Count(i => i.Priority <= p);
    }

    public EnqueueResult Enqueue(PrefetchItem item, Priority priority, double confidence, string predictor)
    {
        if (priority == Priority.P0Critical) priority = Priority.P1Imminent; // P0 belongs to the game
        lock (_gate)
        {
            var key = item.Key;
            if (KnownCached(key)) { Deduplicated++; return EnqueueResult.AlreadyCached; }
            if (_inFlight.Contains(key)) { Deduplicated++; return EnqueueResult.InFlight; }
            if (_queued.TryGetValue(key, out var existing))
            {
                if (priority < existing.Priority || (priority == existing.Priority && confidence > existing.Confidence + 0.05))
                {
                    existing.Priority = priority;
                    existing.Confidence = Math.Max(confidence, existing.Confidence);
                    existing.Predictor = predictor;
                    existing.EnqueuedAt = _clock.Now;
                    existing.Version++;
                    _pq.Enqueue((existing, existing.Version), Order(existing));
                    Upgraded++;
                    return EnqueueResult.Upgraded;
                }
                Deduplicated++;
                return EnqueueResult.Duplicate;
            }
            if (item.Speculative && BudgetBytes is { } b && b < item.Length) return EnqueueResult.OverBudget;
            item.Priority = priority;
            item.Confidence = confidence;
            item.Predictor = predictor;
            item.EnqueuedAt = _clock.Now;
            item.Sequence = ++_seq;
            _queued[key] = item;
            _pq.Enqueue((item, item.Version), Order(item));
            return EnqueueResult.Queued;
        }
    }

    private static (int, double, long) Order(PrefetchItem i) => ((int)i.Priority, -i.Confidence, i.Sequence);

    /// <summary>Takes the best item whose priority is allowed. Marks it in flight and charges the budget.</summary>
    public PrefetchItem? TryDequeue(Priority lowestAllowed)
    {
        List<(PrefetchItem, string)>? dropped = null;
        try
        {
            lock (_gate) return DequeueLocked(lowestAllowed, ref dropped);
        }
        finally { RaiseDropped(dropped); }
    }

    private PrefetchItem? DequeueLocked(Priority lowestAllowed, ref List<(PrefetchItem, string)>? dropped)
    {
        {
            while (_pq.TryPeek(out var top, out _))
            {
                var (item, version) = top;
                if (item.Cancelled || version != item.Version || !_queued.ContainsKey(item.Key) || !ReferenceEquals(_queued[item.Key], item))
                {
                    _pq.Dequeue();
                    continue;
                }
                if (_clock.Now - item.EnqueuedAt > (item.Ttl ?? Ttl.GetValueOrDefault(item.Priority, TimeSpan.FromMinutes(5))))
                {
                    _pq.Dequeue();
                    _queued.Remove(item.Key);
                    Expired++;
                    (dropped ??= new()).Add((item, "expired"));
                    continue;
                }
                if (item.Priority > lowestAllowed) return null;
                if (item.Speculative && BudgetBytes is { } b)
                {
                    if (b < item.Length)
                    {
                        _pq.Dequeue();
                        _queued.Remove(item.Key);
                        (dropped ??= new()).Add((item, "over budget"));
                        continue;
                    }
                    BudgetBytes = b - item.Length;
                }
                _pq.Dequeue();
                _queued.Remove(item.Key);
                _inFlight.Add(item.Key);
                return item;
            }
            return null;
        }
    }

    public void Complete(PrefetchItem item, bool success)
    {
        lock (_gate)
        {
            _inFlight.Remove(item.Key);
            if (success) _cached[item.Key] = _clock.Now;
        }
    }

    /// <summary>Records that a block is known to be in the local cache (e.g. from telemetry), so it is never fetched again.</summary>
    public void MarkCached(string mountId, string mountPath, long blockIndex)
    {
        PrefetchItem? superseded = null;
        lock (_gate)
        {
            var key = (mountId, mountPath.ToLowerInvariant(), blockIndex);
            _cached[key] = _clock.Now;
            if (_cached.Count > 200_000)
                foreach (var old in _cached.Where(kv => _clock.Now - kv.Value >= KnownCachedTtl).Select(kv => kv.Key).ToList()) _cached.Remove(old);
            if (_queued.Remove(key, out var q)) { q.Cancelled = true; Deduplicated++; superseded = q; }
        }
        if (superseded != null) RaiseDropped([(superseded, "already cached")]);
    }

    public void ForgetCached(Func<(string Mount, string Path, long Block), bool> predicate)
    {
        lock (_gate)
            foreach (var k in _cached.Keys.Where(k => predicate(k)).ToList()) _cached.Remove(k);
    }

    public bool IsKnownCached(string mountId, string mountPath, long blockIndex)
    {
        lock (_gate) return KnownCached((mountId, mountPath.ToLowerInvariant(), blockIndex));
    }

    public int Cancel(Func<PrefetchItem, bool> predicate)
    {
        List<PrefetchItem> victims;
        lock (_gate)
        {
            victims = _queued.Values.Where(predicate).ToList();
            foreach (var v in victims)
            {
                v.Cancelled = true;
                _queued.Remove(v.Key);
            }
            CancelledCount += victims.Count;
        }
        RaiseDropped(victims.Select(v => (v, "cancelled")).ToList());
        return victims.Count;
    }

    /// <summary>Items of an owner that are still queued or in flight.</summary>
    public int CountOwned(string owner)
    {
        lock (_gate) return _queued.Values.Count(i => i.Owner == owner);
    }
}

/// <summary>
/// Measures effective throughput and latency of prefetch transfers and derives concurrency and read-ahead depth.
/// Concurrency follows the bandwidth-delay product: enough parallel block requests to keep the link busy despite
/// per-request latency, and no more.
/// </summary>
public sealed class NetworkGovernor
{
    private readonly object _gate = new();
    private double _throughput; // bytes/s EWMA
    private double _latencyMs;  // EWMA of per-request overhead
    private int _samples;

    public NetworkGovernor(double declaredMbps, int maxConcurrency = 4, long blockSize = ByteSize.MiB)
    {
        DeclaredBytesPerSec = Bandwidth.MbpsToBytesPerSecond(declaredMbps);
        MaxConcurrency = Math.Max(1, maxConcurrency);
        BlockSize = blockSize;
        _throughput = DeclaredBytesPerSec * 0.85;
        _latencyMs = 100;
    }

    public double DeclaredBytesPerSec { get; set; }
    public int MaxConcurrency { get; set; }
    public long BlockSize { get; }
    public double BackgroundShare { get; set; } = 0.6;
    public bool HasMeasurements { get { lock (_gate) return _samples >= 3; } }

    public double ThroughputBytesPerSec { get { lock (_gate) return _throughput; } }
    public double LatencyMs { get { lock (_gate) return _latencyMs; } }

    /// <summary>
    /// Feeds a completed network fetch. A single request's elapsed time is latency + bytes/rate, so a lone stream on a
    /// high-latency link under-reports the link rate. The link-rate estimate therefore only moves down on evidence from
    /// parallel transfers (or rclone's aggregate speed via <see cref="ObserveAggregate"/>); latency is what remains.
    /// Samples that look like cache hits (&lt; 2 ms) are ignored.
    /// </summary>
    public void Record(long bytes, TimeSpan elapsed, int concurrentTransfers)
    {
        if (bytes <= 0 || elapsed.TotalMilliseconds < 2) return;
        lock (_gate)
        {
            var c = Math.Max(1, concurrentTransfers);
            var aggregate = bytes / Math.Max(0.001, elapsed.TotalSeconds) * c;
            if (aggregate > _throughput) _throughput = 0.7 * _throughput + 0.3 * aggregate;
            else if (c >= 2) _throughput = 0.9 * _throughput + 0.1 * aggregate;
            var transferMs = bytes / Math.Max(1, _throughput / c) * 1000;
            var overhead = Math.Max(0, elapsed.TotalMilliseconds - transferMs);
            const double a = 0.2;
            _latencyMs = _samples == 0 ? overhead : (1 - a) * _latencyMs + a * overhead;
            _samples++;
        }
    }

    /// <summary>Aggregate download speed measured by rclone (core/stats) while transfers were active.</summary>
    public void ObserveAggregate(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return;
        lock (_gate) _throughput = 0.8 * _throughput + 0.2 * bytesPerSecond;
    }

    /// <summary>Parallel requests needed to fill the pipe: ceil(rate × latency / block) + 1, capped.</summary>
    public int RecommendedConcurrency()
    {
        lock (_gate)
        {
            var bdp = _throughput * (_latencyMs / 1000.0);
            return Math.Clamp((int)Math.Ceiling(bdp / BlockSize) + 1, 1, MaxConcurrency);
        }
    }

    /// <summary>Sequential read-ahead depth covering about one second of transfer (2..16 blocks).</summary>
    public int ReadAheadBlocks()
    {
        lock (_gate) return Math.Clamp((int)(_throughput / BlockSize), 2, 16);
    }

    public double BackgroundBytesPerSec
    {
        get { lock (_gate) return Math.Max(64 * 1024, _throughput * BackgroundShare); }
    }
}

/// <summary>Live view of the game's own outstanding reads that are waiting on the network.</summary>
public interface IForegroundMonitor
{
    int OutstandingMisses { get; }
    /// <summary>Time since the game last had to wait on the network.</summary>
    TimeSpan SinceLastStall { get; }
}

public sealed record SchedulingDecision(int MaxConcurrency, Priority LowestAllowed, bool ThrottleBackground);

/// <summary>
/// Keeps speculative traffic out of the way of the game's own reads (P0). While the game waits on a network read,
/// only P1 work continues, with a single stream; P2/P3 resume after a quiet period and are rate-limited to the
/// background share of measured bandwidth.
/// </summary>
public static class SchedulingPolicy
{
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(750);
    public static readonly TimeSpan ActivityWindow = TimeSpan.FromSeconds(10);

    public static SchedulingDecision Decide(NetworkGovernor governor, IForegroundMonitor foreground, bool diskPressure)
    {
        if (diskPressure) return new SchedulingDecision(0, Priority.P1Imminent, true);
        var c = governor.RecommendedConcurrency();
        if (foreground.OutstandingMisses > 0)
            return new SchedulingDecision(1, Priority.P1Imminent, true);
        if (foreground.SinceLastStall < QuietPeriod)
            return new SchedulingDecision(Math.Max(1, c / 2), Priority.P2Likely, true);
        // Background rate limiting matters only while the game is actively loading; when it has not waited on the
        // network for a while (or is not running, e.g. during Optimize & Prepare) the full link may be used.
        return new SchedulingDecision(c, Priority.P3Background, foreground.SinceLastStall < ActivityWindow);
    }
}
