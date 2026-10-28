using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.Streaming;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CloudSSD.Desktop.ViewModels;

public sealed record PredictorRow(string Name, string State, string Tone, string Issued, string Useful, string Wasted, string Accuracy);
public sealed record ReadRow(string Time, string Path, string Range, string Result, string Tone, string Wait);

/// <summary>Game Accelerator: live cache hits/misses, prediction activity and the game's read stream.</summary>
public sealed partial class AcceleratorViewModel : PageViewModel
{
    private readonly ConcurrentQueue<AccessEvent> _recent = new();

    public AcceleratorViewModel(AppHost host) : base(host, "Game Accelerator", "")
    {
        host.Mounts.ReadObserved += e =>
        {
            if (e.Source != ReadSource.Application) return;
            _recent.Enqueue(e);
            while (_recent.Count > 60 && _recent.TryDequeue(out _)) { }
        };
    }

    public ObservableCollection<PredictorRow> Predictors { get; } = new();
    public ObservableCollection<ReadRow> Reads { get; } = new();
    public ObservableCollection<KeyValueRow> Queue { get; } = new();

    [ObservableProperty] private string _session = "No game session";
    [ObservableProperty] private string _hits = Fmt.NA;
    [ObservableProperty] private string _misses = Fmt.NA;
    [ObservableProperty] private string _hitRate = Fmt.NA;
    [ObservableProperty] private string _avgWait = Fmt.NA;
    [ObservableProperty] private string _longestStall = Fmt.NA;
    [ObservableProperty] private string _stalls = Fmt.NA;
    [ObservableProperty] private string _accuracy = Fmt.NA;
    [ObservableProperty] private string _useful = Fmt.NA;
    [ObservableProperty] private string _wasted = Fmt.NA;
    [ObservableProperty] private string _policy = "";
    [ObservableProperty] private double?[] _hitHistory = [];
    [ObservableProperty] private double?[] _waitHistory = [];
    [ObservableProperty] private double?[] _queueHistory = [];
    [ObservableProperty] private bool _predictionEnabled;

    partial void OnPredictionEnabledChanged(bool value)
    {
        if (Host.Settings.Streaming.PredictionEnabled == value) return;
        Host.Settings.Streaming.PredictionEnabled = value;
        Host.SaveSettings();
        if (!value) Host.Engine.Scheduler.Cancel(_ => true);
    }

    public override void OnShown()
    {
        PredictionEnabled = Host.Settings.Streaming.PredictionEnabled;
        OnMetrics(Host.Metrics.Latest);
    }

    public override void OnMetrics(EngineMetrics m)
    {
        var e = Host.Engine;
        var s = e.Sessions.FirstOrDefault();
        Session = s is null ? (Host.Settings.Streaming.ReadProfilingEnabled ? "No game session — reads are still measured on mounted drives" : "Read profiling is disabled in Settings")
            : $"{Host.Library.Get(s.GameId)?.Name ?? s.GameId} · {s.Mode} · {Bandwidth.FormatDuration(DateTime.Now - s.Recorder.Start)} · {s.Recorder.BlockCount:N0} blocks read";
        Hits = Fmt.Count(m.CacheHits);
        Misses = Fmt.Count(m.CacheMisses);
        HitRate = Fmt.Pct(m.HitRate);
        AvgWait = Fmt.Ms(m.AverageReadWaitMs);
        LongestStall = Fmt.Ms(m.LongestReadStallMs);
        Stalls = m.CacheHits is null ? Fmt.NA : $"{Interlocked.Read(ref e.StallCount):N0} reads ≥ {e.StallThresholdMs:0} ms";
        Accuracy = Fmt.Pct(m.PrefetchAccuracy);
        Useful = Fmt.Bytes(m.PrefetchUsefulBytes);
        Wasted = Fmt.Bytes(m.PrefetchWastedBytes);
        HitHistory = Host.Metrics.HitRate.Snapshot();
        WaitHistory = Host.Metrics.ReadWait.Snapshot();
        QueueHistory = Host.Metrics.QueueLength.Snapshot();
        var d = e.Scheduler.LastDecision;
        Policy = d is null ? "Scheduler idle"
            : d.MaxConcurrency == 0 ? "Paused: disk space below reserve"
            : $"Max {d.MaxConcurrency} parallel · up to {d.LowestAllowed} · {(d.ThrottleBackground ? $"background limited to {Bandwidth.BytesPerSecondToMbps(e.Governor.BackgroundBytesPerSec):0} Mbps" : "full link")} · " +
              $"measured {Bandwidth.BytesPerSecondToMbps(e.Governor.ThroughputBytesPerSec):0} Mbps, {e.Governor.LatencyMs:0} ms overhead" +
              (e.OutstandingMisses > 0 ? $" · game waiting on {e.OutstandingMisses} read(s)" : "");

        Queue.ReplaceWith([
            new("Queued", e.Queue.Count.ToString("N0")),
            new("In flight", e.Scheduler.Active.ToString()),
            new("Completed", e.Scheduler.CompletedCount.ToString("N0")),
            new("Failed", e.Scheduler.FailedCount.ToString("N0"), e.Scheduler.FailedCount > 0 ? "warn" : "muted"),
            new("Fetched", ByteSize.Format(e.Scheduler.BytesFetched)),
            new("Deduplicated", e.Queue.Deduplicated.ToString("N0")),
            new("Reprioritized", e.Queue.Upgraded.ToString("N0")),
            new("Expired / cancelled", $"{e.Queue.Expired:N0} / {e.Queue.CancelledCount:N0}"),
        ]);

        if (s != null)
        {
            var stats = s.Accounting.Snapshot();
            Predictors.ReplaceWith(s.Predictors.Select(p => p.Name).Append(StartupPredictor.Name).Append("region").Distinct().Select(name =>
            {
                stats.TryGetValue(name, out var st);
                var pred = s.Predictors.FirstOrDefault(p => p.Name == name);
                var enabled = pred?.Enabled ?? true;
                return new PredictorRow(name, st?.AutoDisabled == true ? "Auto-disabled (low accuracy)" : enabled ? "Enabled" : "Disabled",
                    st?.AutoDisabled == true ? "error" : enabled ? "ok" : "muted",
                    (st?.IssuedBlocks ?? 0).ToString("N0"), (st?.UsefulBlocks ?? 0).ToString("N0"), (st?.WastedBlocks ?? 0).ToString("N0"),
                    st?.Accuracy is { } a ? a.ToString("P0") : "—");
            }));
        }
        else Predictors.Clear();

        Reads.ReplaceWith(_recent.Reverse().Take(30).Select(r => new ReadRow(
            r.Timestamp.ToString("HH:mm:ss.fff"), r.Path, $"{ByteSize.Format(r.Offset)} +{ByteSize.Format(r.Length)}",
            r.Error != null ? "error" : r.CacheHit switch { true => "hit", false => "miss", _ => "?" },
            r.Error != null ? "error" : r.CacheHit == true ? "ok" : r.CacheHit == false ? "warn" : "muted",
            Fmt.Ms(r.WaitMs))));
    }
}

/// <summary>Performance Monitor: real network and filesystem statistics over time.</summary>
public sealed partial class PerformanceViewModel : PageViewModel
{
    public PerformanceViewModel(AppHost host) : base(host, "Performance Monitor", "") { }

    public ObservableCollection<KeyValueRow> Totals { get; } = new();
    public ObservableCollection<KeyValueRow> SessionHistory { get; } = new();

    [ObservableProperty] private double?[] _rate = [];
    [ObservableProperty] private double?[] _hit = [];
    [ObservableProperty] private double?[] _wait = [];
    [ObservableProperty] private double?[] _reads = [];
    [ObservableProperty] private double?[] _cache = [];
    [ObservableProperty] private string _rateNow = Fmt.NA;
    [ObservableProperty] private string _hitNow = Fmt.NA;
    [ObservableProperty] private string _waitNow = Fmt.NA;
    [ObservableProperty] private string _readsNow = Fmt.NA;
    [ObservableProperty] private string _cacheNow = Fmt.NA;

    public Func<double, string> RateFormat { get; } = v => Fmt.RateShort(v);
    public Func<double, string> MsFormat { get; } = v => $"{v:0} ms";
    public Func<double, string> BytesFormat { get; } = v => ByteSize.Format((long)v);
    public Func<double, string> CountFormat { get; } = v => $"{v:0}/s";

    public override void OnShown()
    {
        OnMetrics(Host.Metrics.Latest);
        var rows = new List<KeyValueRow>();
        foreach (var g in Host.Library.All())
            foreach (var s in Host.Profiles.Sessions(g.Id, 5))
                rows.Add(new KeyValueRow($"{s.Started:g} · {g.Name}",
                    $"{s.Mode}, {Bandwidth.FormatDuration(TimeSpan.FromMilliseconds(s.DurationMs))}, hits {s.Hits:N0} / misses {s.Misses:N0}, stall {s.StallMs / 1000:0.0} s, prefetch useful {ByteSize.Format(s.UsefulBytes)} / wasted {ByteSize.Format(s.WastedBytes)}"));
        SessionHistory.ReplaceWith(rows.OrderByDescending(r => r.Key).Take(20));
    }

    public override void OnMetrics(EngineMetrics m)
    {
        var mc = Host.Metrics;
        Rate = mc.TransferRate.Snapshot();
        Hit = mc.HitRate.Snapshot();
        Wait = mc.ReadWait.Snapshot();
        Reads = mc.ReadsPerSecond.Snapshot();
        Cache = mc.CacheUsed.Snapshot();
        RateNow = Fmt.Rate(m.DownloadBytesPerSec);
        HitNow = mc.HitRate.Latest is { } h ? $"{h:P0} (last second)" : Fmt.NA;
        WaitNow = Fmt.Ms(mc.ReadWait.Latest);
        ReadsNow = mc.ReadsPerSecond.Latest is { } r ? $"{r:0} reads/s" : Fmt.NA;
        CacheNow = Fmt.Bytes(m.CacheBytesOnDisk);
        Totals.ReplaceWith([
            new("Bytes transferred by rclone", Fmt.Bytes(m.TotalBytesTransferred)),
            new("Bytes served to applications", Fmt.Bytes(m.BytesServedToApps)),
            new("Cache hits / misses", m.CacheHits is null ? Fmt.NA : $"{m.CacheHits:N0} / {m.CacheMisses:N0} ({Fmt.Pct(m.HitRate)})"),
            new("Average read wait", Fmt.Ms(m.AverageReadWaitMs)),
            new("Longest read stall", Fmt.Ms(m.LongestReadStallMs)),
            new("Active cloud requests", m.ActiveTransfers?.ToString() ?? Fmt.NA),
            new("Upload speed", "Unavailable — rclone does not report upload and download separately"),
            new("Uploads pending", m.UploadsPending?.ToString() ?? Fmt.NA, m.UploadsPending > 0 ? "warn" : "muted"),
            new("Provider errors / retries", m.ProviderErrors is null ? Fmt.NA : $"{m.ProviderErrors:N0} / {m.ProviderRetries:N0}", m.ProviderErrors > 0 ? "warn" : "muted"),
            new("Prefetch accuracy", Fmt.Pct(m.PrefetchAccuracy)),
            new("Prefetch useful / wasted", $"{Fmt.Bytes(m.PrefetchUsefulBytes)} / {Fmt.Bytes(m.PrefetchWastedBytes)}"),
            new("Cache on disk / logical", $"{Fmt.Bytes(m.CacheBytesOnDisk)} / {Fmt.Bytes(m.CacheLogicalBytes)}"),
            new("SSD free (cache drive)", Fmt.Bytes(m.CacheVolumeFreeBytes)),
            new("Game", m.GameProcessState ?? "—"),
        ]);
    }
}
