using System.Collections.Concurrent;
using CloudSSD.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudSSD.Streaming;

/// <summary>A game currently being profiled and accelerated.</summary>
public sealed class GameSession
{
    internal GameSession(string gameId, string mountId, string rootPrefix, string mode, DateTime start, TransitionModel historic)
    {
        GameId = gameId;
        MountId = mountId;
        RootPrefix = rootPrefix.Replace('\\', '/').Trim('/');
        Mode = mode;
        Recorder = new SessionRecorder(start);
        Historic = historic;
    }

    public string GameId { get; }
    public string MountId { get; }
    /// <summary>Game folder relative to the mount root ("" = mount root), forward slashes.</summary>
    public string RootPrefix { get; }
    public string Mode { get; }
    public SessionRecorder Recorder { get; }
    public PredictionAccounting Accounting { get; } = new();
    public TransitionModel Historic { get; }
    public List<IPredictor> Predictors { get; } = new();
    public StartupStream? Startup { get; internal set; }

    /// <summary>Game progress: wall time since launch minus time spent waiting on cloud misses.</summary>
    public double ProgressMs => Math.Max(0, (DateTime.Now - Recorder.Start).TotalMilliseconds - Recorder.StallMs);
    internal readonly ConcurrentDictionary<string, long?> FileBlocks = new(StringComparer.OrdinalIgnoreCase);
    public string OwnerTag => "game:" + GameId;

    public string? ToGamePath(string mountPath)
    {
        var p = mountPath.Replace('\\', '/').TrimStart('/');
        if (RootPrefix.Length == 0) return p;
        return p.StartsWith(RootPrefix + "/", StringComparison.OrdinalIgnoreCase) ? p[(RootPrefix.Length + 1)..] : null;
    }

    public string ToMountPath(string gamePath) => RootPrefix.Length == 0 ? gamePath : RootPrefix + "/" + gamePath;
}

public sealed record SessionSummary(string GameId, TimeSpan Duration, int Blocks, long BytesRead, long Hits, long Misses, double StallMs,
    double LongestStallMs, long UsefulPrefetchBytes, long WastedPrefetchBytes, double? Accuracy, IReadOnlyList<string> DisabledPredictors);

public sealed record PrepareProgress(int TotalBlocks, int DoneBlocks, int SkippedCached, int Failed, long BytesFetched, long BytesPlanned, TimeSpan Elapsed, TimeSpan? Eta);

public sealed record PrepareResult(bool HadProfile, int ProfileSessions, int PlannedBlocks, int FetchedBlocks, int AlreadyCachedBlocks, int FailedBlocks,
    long BytesFetched, long BytesPlanned, int TrimmedForBudget, TimeSpan Elapsed, bool Completed, double MeanConfidence)
{
    /// <summary>The blocks that were planned, relative to the game root (used to verify coverage afterwards).</summary>
    public IReadOnlyList<BlockKey> Planned { get; init; } = [];
}

/// <summary>
/// The CloudSSD prediction engine (Universal Mode). Consumes real read telemetry, learns per-game access profiles,
/// predicts upcoming reads, and schedules prefetches that warm rclone's cache without competing with the game.
/// </summary>
public sealed class StreamingEngine : IForegroundMonitor, IAsyncDisposable
{
    private readonly StreamingSettings _settings;
    private readonly ProfileStore _store;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();
    private readonly ConcurrentDictionary<(string Mount, string Path, long Offset), DateTime> _outstanding = new();
    private readonly ConcurrentDictionary<string, List<(BlockKey Block, long Bytes, DateTime At)>> _prepared = new();
    private readonly Func<string, string, long?> _fileSize;
    private readonly Func<string, string, long, long, bool>? _isCached;
    private readonly Timer _timer;
    private DateTime _lastStall = DateTime.MinValue;

    public StreamingEngine(StreamingSettings settings, NetworkSettings network, ProfileStore store, IPrefetchExecutor executor,
        Func<string, string, long?> fileSize, Func<long, bool>? mayUseDisk = null, Func<string, string, long, long, bool>? isCached = null,
        ILogger<StreamingEngine>? log = null)
    {
        _settings = settings;
        _store = store;
        _fileSize = fileSize;
        _isCached = isCached;
        _log = (ILogger?)log ?? NullLogger.Instance;
        Governor = new NetworkGovernor(network.EffectiveMbps, network.MaxPrefetchConcurrency, settings.BlockSizeBytes) { BackgroundShare = network.BackgroundShare };
        Queue = new PrefetchQueue();
        Scheduler = new PrefetchScheduler(Queue, executor, Governor, this, mayUseDisk);
        Scheduler.Completed += OnPrefetchCompleted;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public NetworkGovernor Governor { get; }
    public PrefetchQueue Queue { get; }
    public PrefetchScheduler Scheduler { get; }
    public long BlockSize => _settings.BlockSizeBytes;
    public bool PredictionEnabled { get => _settings.PredictionEnabled; set => _settings.PredictionEnabled = value; }

    // Application-read metrics (prefetch reads are excluded).
    public long Hits, Misses, Reads, BytesServed, UnknownHitState;
    public double TotalWaitMs, LongestStallMs;
    public long StallCount;
    /// <summary>A read slower than this counts as a loading stall.</summary>
    public double StallThresholdMs { get; set; } = 50;

    public IReadOnlyCollection<GameSession> Sessions => _sessions.Values.ToList();

    /// <summary>Raised for every application block read inside a game session (used by Game-Aware Mode to learn regions).</summary>
    public event Action<GameSession, BlockKey>? ApplicationBlockRead;

    /// <summary>Schedules a prediction made outside the built-in predictors (e.g. Game-Aware), tagged for later cancellation.</summary>
    public EnqueueResult ScheduleExternal(GameSession s, Prediction p, string tag) => Schedule(s, p, s.OwnerTag + "|" + tag);

    /// <summary>Cancels queued/running prefetches of a session whose tag starts with <paramref name="tagPrefix"/>.</summary>
    public int CancelTagged(GameSession s, string tagPrefix) => Scheduler.Cancel(i => i.Owner.StartsWith(s.OwnerTag + "|" + tagPrefix, StringComparison.Ordinal));

    public GameSession? GetSession(string gameId) => _sessions.GetValueOrDefault(gameId);

    // ---- IForegroundMonitor ----
    public int OutstandingMisses => _outstanding.Count;
    public TimeSpan SinceLastStall => DateTime.Now - _lastStall;

    /// <summary>rclone reported a cache lookup for an application read (before data arrives).</summary>
    public void OnLookup(string mountId, string path, long offset, bool present, bool isPrefetch)
    {
        if (present || isPrefetch) return;
        _outstanding[(mountId, path, offset)] = DateTime.Now;
        _lastStall = DateTime.Now;
    }

    public void OnAccess(AccessEvent e)
    {
        _outstanding.TryRemove((e.MountId, e.Path, e.Offset), out _);
        var blocks = BlockMath.BlocksFor(e.Offset, Math.Max(1, e.Length), BlockSize).ToList();
        if (e.Source == ReadSource.Prefetch)
        {
            foreach (var b in blocks) Queue.MarkCached(e.MountId, e.Path, b);
            return;
        }
        if (e.Error != null) return;

        Interlocked.Increment(ref Reads);
        Interlocked.Add(ref BytesServed, e.Length);
        var wait = e.WaitMs ?? 0;
        AddDouble(ref TotalWaitMs, wait);
        if (e.CacheHit == true) Interlocked.Increment(ref Hits);
        else if (e.CacheHit == false)
        {
            Interlocked.Increment(ref Misses);
            _lastStall = DateTime.Now;
        }
        else Interlocked.Increment(ref UnknownHitState);
        if (wait >= StallThresholdMs) Interlocked.Increment(ref StallCount);
        if (wait > LongestStallMs) LongestStallMs = wait;

        foreach (var s in _sessions.Values)
        {
            if (s.MountId != e.MountId) continue;
            var gamePath = s.ToGamePath(e.Path);
            if (gamePath is null) continue;
            var rec = s.Recorder;
            Interlocked.Add(ref rec.BytesRead, e.Length);
            if (e.CacheHit == true) Interlocked.Increment(ref rec.Hits);
            else if (e.CacheHit == false) Interlocked.Increment(ref rec.Misses);
            AddDouble(ref rec.StallMs, e.CacheHit == false ? wait : 0);
            if (wait > rec.LongestStallMs) rec.LongestStallMs = wait;

            foreach (var idx in blocks)
            {
                var key = new BlockKey(gamePath, idx);
                rec.Record(key, e.Timestamp);
                s.Accounting.RecordApplicationAccess(key);
                s.Historic.Observe(key, e.Timestamp);
                Queue.MarkCached(e.MountId, e.Path, idx);
                try { ApplicationBlockRead?.Invoke(s, key); } catch (Exception) { }
                if (!_settings.PredictionEnabled) continue;
                var access = new BlockAccess(key, e.Timestamp, e.CacheHit);
                foreach (var p in s.Predictors)
                {
                    if (!p.Enabled) continue;
                    foreach (var pred in p.OnAccess(access))
                        if (pred.Confidence >= _settings.MinConfidence) Schedule(s, pred);
                }
            }
            ReleaseStartup(s);
        }
        foreach (var b in blocks) Queue.MarkCached(e.MountId, e.Path, b);
    }

    private EnqueueResult Schedule(GameSession s, Prediction pred, string? owner = null, bool explicitRequest = false)
    {
        var mountPath = s.ToMountPath(pred.Block.Path);
        var offset = pred.Block.Index * BlockSize;
        if (_isCached?.Invoke(s.MountId, mountPath, offset, BlockSize) == true)
        {
            Queue.MarkCached(s.MountId, mountPath, pred.Block.Index);
            return EnqueueResult.AlreadyCached;
        }
        var item = new PrefetchItem
        {
            MountId = s.MountId, MountPath = mountPath, Block = pred.Block, Offset = offset, Length = (int)BlockSize,
            Owner = owner ?? s.OwnerTag,
            Speculative = !explicitRequest,
            Ttl = explicitRequest ? TimeSpan.FromHours(6) : null
        };
        return Scheduler.Enqueue(item, pred.Priority, pred.Confidence, pred.Predictor);
    }

    private void OnPrefetchCompleted(PrefetchItem item, PrefetchResult r)
    {
        if (r.Error != null || r.Bytes <= 0) return;
        if (item.Owner.StartsWith("prepare:", StringComparison.Ordinal))
        {
            var gameId = item.Owner[8..];
            var list = _prepared.GetOrAdd(gameId, _ => new());
            lock (list) list.Add((item.Block, r.Bytes, DateTime.Now));
            return;
        }
        foreach (var s in _sessions.Values)
        {
            if (item.Owner != s.OwnerTag && !item.Owner.StartsWith(s.OwnerTag + "|", StringComparison.Ordinal)) continue;
            s.Accounting.RecordPrefetch(item.Block, item.Predictor, r.Bytes, DateTime.Now);
            // The game may already have read it while the prefetch was in flight: prediction was right, just late.
            if (s.Recorder.HasSeen(item.Block)) s.Accounting.RecordApplicationAccess(item.Block);
        }
    }

    public bool HasSession(string gameId) => _sessions.ContainsKey(gameId);

    /// <summary>Starts profiling a game. With a stored profile, startup data is scheduled immediately (P1/P2).</summary>
    public GameSession BeginSession(string gameId, string mountId, string gameRootInMount, string mode)
    {
        var historic = _store.LoadTransitions(gameId);
        var s = new GameSession(gameId, mountId, gameRootInMount, mode, DateTime.Now, historic);
        var disabled = _store.LoadPredictorStats(gameId)
            .Where(p => p.Disabled || (p.Useful + p.Wasted >= 2L * _settings.PredictorEvaluationSamples && p.Accuracy < _settings.MinPredictorAccuracy))
            .Select(p => p.Predictor).ToHashSet();
        s.Predictors.Add(new MarkovPredictor(historic, _settings.MinConfidence) { Enabled = !disabled.Contains("markov") });
        s.Predictors.Add(new SequentialPredictor(Governor.ReadAheadBlocks, path => FileBlocks(s, path)) { Enabled = !disabled.Contains("sequential") });
        _sessions[gameId] = s;

        if (_prepared.TryRemove(gameId, out var prepared))
            lock (prepared) foreach (var (b, bytes, at) in prepared) s.Accounting.RecordPrefetch(b, StartupPredictor.Name, bytes, at);

        Queue.BudgetBytes = _settings.SessionPrefetchBudgetBytes;
        if (_settings.PredictionEnabled && !disabled.Contains(StartupPredictor.Name))
        {
            var (count, history) = _store.LoadHistory(gameId);
            s.Startup = StartupStream.FromHistory(history, count, _settings.MinConfidence, _settings.StartupWindow, _settings.StartupHorizon.TotalMilliseconds);
        }
        var scheduled = ReleaseStartup(s);
        _log.LogInformation("Streaming session started for {Game} (mode {Mode}); learned startup set {Total} blocks, {Count} released just in time; disabled predictors: {Disabled}",
            gameId, mode, scheduled + (s.Startup?.Remaining ?? 0), scheduled, disabled.Count == 0 ? "none" : string.Join(",", disabled));
        return s;
    }

    /// <summary>Schedules learned startup blocks that are due within the horizon of the game's current progress.</summary>
    private int ReleaseStartup(GameSession s)
    {
        if (s.Startup is null || !_settings.PredictionEnabled) return 0;
        int n = 0;
        foreach (var p in s.Startup.Due(s.ProgressMs))
            if (Schedule(s, p) is EnqueueResult.Queued or EnqueueResult.Upgraded) n++;
        return n;
    }

    public SessionSummary? EndSession(string gameId, bool save = true)
    {
        if (!_sessions.TryRemove(gameId, out var s)) return null;
        var now = DateTime.Now;
        Scheduler.Cancel(i => i.Owner == s.OwnerTag || i.Owner.StartsWith(s.OwnerTag + "|", StringComparison.Ordinal));
        s.Accounting.Settle(now, sessionEnded: true);
        var disabled = s.Predictors.Where(p => !p.Enabled).Select(p => p.Name).ToList();
        if (save && s.Recorder.BlockCount > 0)
            _store.SaveSession(gameId, BlockSize, s.Recorder, now, s.Accounting, s.Mode, disabled);
        var t = s.Accounting.Totals();
        var r = s.Recorder;
        _log.LogInformation("Session {Game} ended: {Blocks} blocks, hits {Hits}, misses {Misses}, stall {Stall:0} ms, prefetch useful {U} wasted {W}",
            gameId, r.BlockCount, r.Hits, r.Misses, r.StallMs, ByteSize.Format(t.UsefulBytes), ByteSize.Format(t.WastedBytes));
        return new SessionSummary(gameId, now - r.Start, r.BlockCount, r.BytesRead, r.Hits, r.Misses, r.StallMs, r.LongestStallMs,
            t.UsefulBytes, t.WastedBytes, t.Accuracy, disabled);
    }

    /// <summary>
    /// Optimize &amp; Prepare: prefetches the startup working set predicted from previous launches, within the byte budget.
    /// Returns HadProfile=false when the game was never profiled - the caller must then report uncertainty.
    /// </summary>
    public async Task<PrepareResult> PrepareAsync(string gameId, string mountId, string gameRootInMount, long maxBytes,
        IProgress<PrepareProgress>? progress, CancellationToken ct, TimeSpan? timeout = null, Func<string, bool>? include = null)
    {
        var (sessions, history) = _store.LoadHistory(gameId);
        var predictions = StartupPredictor.Predict(history, sessions, _settings.MinConfidence, _settings.StartupWindow);
        if (include != null) predictions = predictions.Where(p => include(p.Block.Path)).ToList();
        if (predictions.Count == 0)
            return new PrepareResult(sessions > 0, sessions, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, true, 0);

        var tmp = new GameSession(gameId, mountId, gameRootInMount, "prepare", DateTime.Now, new TransitionModel());
        var owner = "prepare:" + gameId;
        var maxBlocks = (int)Math.Max(0, maxBytes / BlockSize);
        var trimmed = Math.Max(0, predictions.Count - maxBlocks);
        var plan = predictions.Take(maxBlocks).ToList();
        var mean = plan.Count == 0 ? 0 : plan.Average(p => p.Confidence);
        int done = 0, failed = 0, skipped = 0;
        long bytes = 0;
        var started = DateTime.Now;
        var pending = new HashSet<(string, long)>();

        void OnDone(PrefetchItem item, PrefetchResult r)
        {
            if (item.Owner != owner) return;
            lock (pending)
            {
                if (!pending.Remove((item.MountPath.ToLowerInvariant(), item.Block.Index))) return;
                if (r.Error is null) { done++; bytes += r.Bytes; }
                else if (r.Error == "dropped: already cached") skipped++;
                else failed++;
            }
        }

        Scheduler.Completed += OnDone;
        try
        {
            var savedBudget = Queue.BudgetBytes;
            Queue.BudgetBytes = null;
            foreach (var p in plan)
            {
                // During preparation nothing else is running, so everything goes in at P1/P2 in first-access order.
                var res = Schedule(tmp, p with { Priority = p.Priority == Priority.P3Background ? Priority.P2Likely : p.Priority }, owner, explicitRequest: true);
                if (res is EnqueueResult.Queued or EnqueueResult.Upgraded)
                    lock (pending) pending.Add((tmp.ToMountPath(p.Block.Path).ToLowerInvariant(), p.Block.Index));
                else skipped++;
            }
            Queue.BudgetBytes = savedBudget;
            var total = plan.Count;
            var deadline = DateTime.Now + (timeout ?? TimeSpan.FromHours(2));
            DateTime? orphanedSince = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int left;
                lock (pending) left = pending.Count;
                // Safety net: if nothing of ours is queued or running any more, remaining entries can never complete.
                if (left > 0 && Queue.CountOwned(owner) == 0 && Scheduler.RunningOwned(owner) == 0)
                {
                    orphanedSince ??= DateTime.Now;
                    if (DateTime.Now - orphanedSince > TimeSpan.FromSeconds(3))
                    {
                        lock (pending) { failed += pending.Count; pending.Clear(); }
                        _log.LogWarning("Prepare {Game}: {Count} planned block(s) were lost by the scheduler and counted as failed", gameId, left);
                        left = 0;
                    }
                }
                else orphanedSince = null;
                var elapsed = DateTime.Now - started;
                var rate = bytes / Math.Max(0.5, elapsed.TotalSeconds);
                TimeSpan? eta = rate > 0 ? TimeSpan.FromSeconds(left * BlockSize / rate) : null;
                progress?.Report(new PrepareProgress(total, done, skipped, failed, bytes, plan.Count * BlockSize, elapsed, eta));
                if (left == 0) break;
                if (DateTime.Now > deadline)
                    return new PrepareResult(true, sessions, total, done, skipped, failed, bytes, plan.Count * BlockSize, trimmed, elapsed, false, mean) { Planned = plan.Select(p => p.Block).ToList() };
                await Task.Delay(250, ct);
            }
            return new PrepareResult(true, sessions, total, done, skipped, failed, bytes, plan.Count * BlockSize, trimmed, DateTime.Now - started, true, mean) { Planned = plan.Select(p => p.Block).ToList() };
        }
        catch (OperationCanceledException)
        {
            Scheduler.Cancel(i => i.Owner == owner);
            throw;
        }
        finally
        {
            Scheduler.Completed -= OnDone;
        }
    }

    private long? FileBlocks(GameSession s, string gamePath) =>
        s.FileBlocks.GetOrAdd(gamePath, p =>
        {
            var size = _fileSize(s.MountId, s.ToMountPath(p));
            return size is { } sz ? (sz + BlockSize - 1) / BlockSize : null;
        });

    private void Tick()
    {
        var now = DateTime.Now;
        foreach (var (k, t) in _outstanding)
            if (now - t > TimeSpan.FromSeconds(30)) _outstanding.TryRemove(k, out _);
        foreach (var s in _sessions.Values)
        {
            ReleaseStartup(s);
            s.Accounting.Settle(now);
            foreach (var name in s.Accounting.Evaluate(s.Predictors, _settings.PredictorEvaluationSamples, _settings.MinPredictorAccuracy))
            {
                _log.LogWarning("Predictor {Name} disabled for {Game}: measured accuracy below {Min:P0}", name, s.GameId, _settings.MinPredictorAccuracy);
                Scheduler.Cancel(i => i.Owner == s.OwnerTag && i.Predictor == name);
            }
        }
    }

    public (long Useful, long Wasted, double? Accuracy) PrefetchTotals()
    {
        long u = 0, w = 0, ub = 0, wb = 0;
        foreach (var s in _sessions.Values)
            foreach (var st in s.Accounting.Snapshot().Values)
            {
                u += st.UsefulBlocks; w += st.WastedBlocks; ub += st.UsefulBytes; wb += st.WastedBytes;
            }
        return (ub, wb, u + w == 0 ? null : u / (double)(u + w));
    }

    private static void AddDouble(ref double target, double value)
    {
        double initial, computed;
        do
        {
            initial = target;
            computed = initial + value;
        } while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
    }

    public async ValueTask DisposeAsync()
    {
        await _timer.DisposeAsync();
        foreach (var id in _sessions.Keys.ToList()) EndSession(id);
        await Scheduler.DisposeAsync();
    }
}
