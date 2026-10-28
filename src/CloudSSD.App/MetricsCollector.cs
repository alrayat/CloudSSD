using CloudSSD.Cache;
using CloudSSD.Core;
using CloudSSD.Mount;

namespace CloudSSD.App;

/// <summary>
/// Samples live metrics once per second from real sources: rclone's rc API (core/stats, vfs/stats), rclone's cache
/// metadata on disk, and the engine's telemetry counters. Anything without a source is reported as null and shown as
/// "Unavailable" — never estimated. Sampling runs on a timer thread and is cheap enough to leave on while gaming.
/// </summary>
public sealed class MetricsCollector : IDisposable
{
    private readonly AppHost _host;
    private Timer? _timer;
    private readonly Dictionary<string, long> _lastBytes = new();
    private DateTime _lastSample = DateTime.MinValue;
    private long _lastHits, _lastMisses, _lastReads;
    private double _lastWait;
    private int _tick;
    private (long Physical, long Logical, long Free, int Dirty)? _cache;
    private int _busy;

    public MetricsCollector(AppHost host) => _host = host;

    public EngineMetrics Latest { get; private set; } = new();
    public MetricHistory TransferRate { get; } = new(300);
    public MetricHistory HitRate { get; } = new(300);
    public MetricHistory ReadWait { get; } = new(300);
    public MetricHistory QueueLength { get; } = new(300);
    public MetricHistory CacheUsed { get; } = new(300);
    public MetricHistory ReadsPerSecond { get; } = new(300);
    public event Action<EngineMetrics>? Updated;

    public void Start() => _timer ??= new Timer(_ => _ = SampleAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    private async Task SampleAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = DateTime.Now;
            var dt = _lastSample == DateTime.MinValue ? 1.0 : Math.Max(0.2, (now - _lastSample).TotalSeconds);
            _lastSample = now;
            double? rate = null;
            int? active = null, uploads = null;
            long? total = null;
            long errors = 0, retries = 0;
            var mounted = _host.Mounts.Sessions.Where(s => s.State is MountState.Mounted or MountState.Degraded && s.Rc != null).ToList();
            foreach (var s in mounted)
            {
                errors += Interlocked.Read(ref s.ProviderErrors);
                retries += Interlocked.Read(ref s.Retries);
                try
                {
                    var cs = CoreStats.Parse(await s.Rc!.CallAsync("core/stats", null, CancellationToken.None, TimeSpan.FromSeconds(2)));
                    var prev = _lastBytes.GetValueOrDefault(s.Id, cs.Bytes);
                    var delta = Math.Max(0, cs.Bytes - prev);
                    _lastBytes[s.Id] = cs.Bytes;
                    rate = (rate ?? 0) + delta / dt;
                    active = (active ?? 0) + cs.Transferring;
                    total = (total ?? 0) + cs.Bytes;
                    if (cs.Transferring >= 2 && cs.Speed > 0) _host.Engine.Governor.ObserveAggregate(delta / dt);
                    var vs = VfsStats.Parse(await s.Rc.CallAsync("vfs/stats", null, CancellationToken.None, TimeSpan.FromSeconds(2)));
                    uploads = (uploads ?? 0) + vs.UploadsInProgress + vs.UploadsQueued;
                }
                catch (Exception ex) when (ex is HttpRequestException or RcException or TaskCanceledException) { }
            }

            if (_tick++ % 10 == 0 || _cache is null)
            {
                try
                {
                    long phys = 0, logical = 0;
                    int dirty = 0;
                    var root = _host.Paths.CacheRootFor(_host.Settings);
                    Directory.CreateDirectory(root);
                    foreach (var dir in Directory.EnumerateDirectories(root, "m-*"))
                    {
                        var u = CacheInspector.Measure(dir);
                        phys += u.PhysicalBytes;
                        logical += u.LogicalBytes;
                        dirty += u.DirtyFiles;
                    }
                    var (free, _) = DiskInfo.FreeSpace(root);
                    _cache = (phys, logical, free, dirty);
                }
                catch (Exception) { }
            }

            var e = _host.Engine;
            var telemetry = _host.Settings.Streaming.ReadProfilingEnabled && mounted.Count > 0;
            long hits = Interlocked.Read(ref e.Hits), misses = Interlocked.Read(ref e.Misses), reads = Interlocked.Read(ref e.Reads);
            var wait = e.TotalWaitMs;
            long dh = hits - _lastHits, dm = misses - _lastMisses, dr = reads - _lastReads;
            var dw = wait - _lastWait;
            (_lastHits, _lastMisses, _lastReads, _lastWait) = (hits, misses, reads, wait);
            var live = e.PrefetchTotals();
            long useful = live.Useful + Interlocked.Read(ref _host.FinishedUsefulBytes);
            long wasted = live.Wasted + Interlocked.Read(ref _host.FinishedWastedBytes);

            var m = new EngineMetrics
            {
                Timestamp = now,
                DownloadBytesPerSec = mounted.Count > 0 ? rate : null,
                UploadBytesPerSec = null, // rclone's stats do not separate upload from download; see UploadsPending
                ActiveTransfers = mounted.Count > 0 ? active : null,
                TotalBytesTransferred = total,
                CacheBytesOnDisk = _cache?.Physical,
                CacheLogicalBytes = _cache?.Logical,
                CacheVolumeFreeBytes = _cache?.Free,
                BytesServedToApps = telemetry ? Interlocked.Read(ref e.BytesServed) : null,
                CacheHits = telemetry ? hits : null,
                CacheMisses = telemetry ? misses : null,
                AverageReadWaitMs = telemetry && reads > 0 ? wait / reads : null,
                LongestReadStallMs = telemetry && reads > 0 ? e.LongestStallMs : null,
                PrefetchAccuracy = useful + wasted > 0 ? useful / (double)(useful + wasted) : null,
                PrefetchUsefulBytes = useful + wasted > 0 || e.Scheduler.CompletedCount > 0 ? useful : null,
                PrefetchWastedBytes = useful + wasted > 0 || e.Scheduler.CompletedCount > 0 ? wasted : null,
                ProviderErrors = mounted.Count > 0 ? errors : null,
                ProviderRetries = mounted.Count > 0 ? retries : null,
                UploadsPending = uploads,
                PrefetchQueueLength = e.Queue.Count,
                GameProcessState = _host.CurrentLaunch?.StateText ?? "No game running",
            };
            Latest = m;
            TransferRate.Add(m.DownloadBytesPerSec);
            HitRate.Add(telemetry && dh + dm > 0 ? dh / (double)(dh + dm) : null);
            ReadWait.Add(telemetry && dr > 0 ? dw / dr : null);
            QueueLength.Add(m.PrefetchQueueLength);
            CacheUsed.Add(m.CacheBytesOnDisk);
            ReadsPerSecond.Add(telemetry ? dr / dt : null);
            Updated?.Invoke(m);
        }
        catch (Exception) { /* metrics must never take the app down */ }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
