using CloudSSD.Core;

namespace CloudSSD.Streaming.Simulation;

public enum SimPolicy
{
    /// <summary>Everything already on the local SSD.</summary>
    SsdBaseline,
    /// <summary>rclone VFS full cache with default --buffer-size 16M (downloads 16 MiB ahead of every read position).</summary>
    RcloneDefault,
    /// <summary>rclone VFS full cache with --buffer-size 0: only the bytes actually read are fetched.</summary>
    OnDemand,
    /// <summary>OnDemand plus the CloudSSD prediction engine.</summary>
    Engine
}

public sealed record SimNetwork(double Mbps, double LatencyMs, double Efficiency = 0.9)
{
    public double BytesPerMs => Bandwidth.MbpsToBytesPerSecond(Mbps) * Efficiency / 1000.0;
}

public sealed record SimOptions
{
    public required SimPolicy Policy { get; init; }
    public required SimNetwork Network { get; init; }
    public long BlockSize { get; init; } = ByteSize.MiB;
    public int RcloneBufferBlocks { get; init; } = 16;
    public bool Prepare { get; init; }
    public ProfileStore? Profiles { get; init; }
    public string GameId { get; init; } = "sim";
    public bool RecordProfile { get; init; }
    public StreamingSettings Streaming { get; init; } = new();
    public int MaxConcurrency { get; init; } = 4;
    public double BackgroundShare { get; init; } = 0.6;
    /// <summary>Local SSD throughput used for cache hits and the SSD baseline (SATA-class).</summary>
    public double LocalMBps { get; init; } = 500;
    /// <summary>Fixed per-read overhead of the VFS path on a cache hit.</summary>
    public double HitOverheadMs { get; init; } = 0.15;
    public long? CacheCapacityBytes { get; init; }
    public HashSet<(string, long)>? PreCached { get; init; }
    public double StallThresholdMs { get; init; } = 50;
}

public sealed record SimResult(
    SimPolicy Policy, double Mbps, double LatencyMs, bool Prepared, int ProfileSessions,
    double TotalMs, double ThinkMs, double StallMs, int StallCount, double LongestStallMs,
    int Reads, int HitReads, long NetworkBytes, long DemandBytes, long PrefetchBytes, long UsefulPrefetchBytes, long WastedPrefetchBytes,
    long Requests, double PrepareMs, long PrepareBytes, double? PredictionAccuracy, IReadOnlyDictionary<string, PredictionAccounting.PredictorStats> Predictors)
{
    public double HitRate => Reads == 0 ? 0 : HitReads / (double)Reads;
}

/// <summary>
/// Discrete-event simulation of a game replaying a trace against a cloud link (bandwidth shared equally between active
/// transfers, fixed per-request latency) and a block cache. The Engine policy runs the production PrefetchQueue,
/// SchedulingPolicy, NetworkGovernor, predictors, accounting and ProfileStore against a virtual clock.
/// </summary>
public sealed class TraceSimulator : IForegroundMonitor
{
    private sealed class Transfer
    {
        public required List<(string Path, long Block)> Blocks;
        public int NextBlock;            // index into Blocks currently being transferred
        public double RemainingInBlock;  // bytes
        public double ReadyAt;           // ms when bytes start flowing (after latency)
        public double StartedAt;
        public bool Demand;
        public PrefetchItem? Item;
        public string? StreamFile;       // rclone default read-ahead stream
        public long StreamTarget;        // last block index the stream should reach
    }

    private readonly GameTrace _trace;
    private readonly SimOptions _o;
    private readonly ManualClock _clock;
    private readonly DateTime _epoch = new(2026, 1, 1);
    private readonly HashSet<(string, long)> _cached = new();
    private readonly LinkedList<(string, long)> _lru = new();
    private readonly Dictionary<(string, long), LinkedListNode<(string, long)>> _lruNodes = new();
    private readonly Dictionary<(string, long), Transfer> _inflight = new();
    private readonly List<Transfer> _transfers = new();
    private readonly HashSet<(string, long)> _prefetched = new();
    private readonly HashSet<(string, long)> _readByGame = new();
    private readonly HashSet<(string, long)> _demanded = new();
    private double _now;
    private double _lastStall = -1e9;
    private int _outstanding;
    private long _networkBytes, _demandBytes, _prefetchBytes, _requests;
    private double _tokens, _tokensAt;

    // Engine parts
    private PrefetchQueue? _queue;
    private NetworkGovernor? _gov;
    private GameSession? _session;

    public TraceSimulator(GameTrace trace, SimOptions options)
    {
        _trace = trace;
        _o = options;
        _clock = new ManualClock(_epoch);
        if (options.PreCached != null) foreach (var k in options.PreCached) AddCached(k);
    }

    public int OutstandingMisses => _outstanding;
    public TimeSpan SinceLastStall => TimeSpan.FromMilliseconds(_now - _lastStall);
    public HashSet<(string, long)> CachedBlocks => _cached;

    private DateTime T(double ms) => _epoch.AddMilliseconds(ms);

    public SimResult Run()
    {
        int profileSessions = 0;
        double prepareMs = 0;
        long prepareBytes = 0;
        if (_o.Policy == SimPolicy.Engine) profileSessions = SetupEngine();
        if (_o.Policy == SimPolicy.Engine && _o.Prepare && _o.Profiles != null)
        {
            var before = _networkBytes;
            prepareMs = RunPrepare();
            prepareBytes = _networkBytes - before;
            _prefetched.Clear(); // prepared blocks are accounted as startup prefetches in the session below
        }

        var gameStart = _now;
        if (_o.Policy == SimPolicy.Engine) StartEngineSession(gameStart);

        double think = 0, stall = 0, longest = 0;
        int stalls = 0, hits = 0;
        var bs = _o.BlockSize;
        foreach (var read in _trace.Reads)
        {
            AdvanceTo(_now + read.ThinkMs);
            think += read.ThinkMs;
            var blocks = BlockMath.BlocksFor(read.Offset, read.Length, bs).Select(b => (read.Path, b)).ToList();
            var issuedAt = _now;
            var missing = blocks.Where(k => !_cached.Contains(k)).ToList();
            double localCost = _o.HitOverheadMs + read.Length / (_o.LocalMBps * 1000.0);
            if (_o.Policy == SimPolicy.SsdBaseline) { missing.Clear(); localCost = 0.08 + read.Length / (_o.LocalMBps * 1000.0); }

            if (_o.Policy == SimPolicy.RcloneDefault) EnsureStream(read.Path, blocks[^1].b);

            if (missing.Count == 0)
            {
                hits++;
                AdvanceTo(_now + localCost);
            }
            else
            {
                var start = _now;
                _outstanding++;
                _lastStall = _now;
                foreach (var k in missing) _demanded.Add(k);
                var toFetch = missing.Where(k => !_inflight.ContainsKey(k)).ToList();
                if (toFetch.Count > 0)
                {
                    if (_o.Policy == SimPolicy.RcloneDefault)
                        StartStream(read.Path, toFetch[0].b, blocks[^1].b + _o.RcloneBufferBlocks, demand: true);
                    else
                        StartTransfer(toFetch, demand: true, item: null);
                }
                while (missing.Any(k => !_cached.Contains(k))) Step(untilMs: double.PositiveInfinity);
                _outstanding--;
                var waited = _now - start;
                stall += waited;
                if (waited >= _o.StallThresholdMs) stalls++;
                longest = Math.Max(longest, waited);
                AdvanceTo(_now + localCost);
            }
            foreach (var k in blocks)
            {
                _readByGame.Add(k);
                Touch(k);
            }
            if (_o.Policy == SimPolicy.Engine)
            {
                if (missing.Count > 0) _session!.Recorder.StallMs += _now - issuedAt - localCost;
                EngineOnRead(read, blocks);
            }
        }

        var total = _now - gameStart;
        _prefetchBytes = _prefetched.LongCount() * bs + prepareBytes;
        _demandBytes = _networkBytes - _prefetchBytes;
        long useful = 0, wasted = 0;
        foreach (var k in _prefetched)
        {
            if (_readByGame.Contains(k)) useful += bs; else wasted += bs;
        }
        double? acc = null;
        IReadOnlyDictionary<string, PredictionAccounting.PredictorStats> stats = new Dictionary<string, PredictionAccounting.PredictorStats>();
        if (_session != null)
        {
            _session.Accounting.Settle(T(_now), sessionEnded: true);
            acc = _session.Accounting.Totals().Accuracy;
            stats = _session.Accounting.Snapshot();
            if (_o.RecordProfile && _o.Profiles != null)
                _o.Profiles.SaveSession(_o.GameId, bs, _session.Recorder, T(_now), _session.Accounting, _o.Policy.ToString());
        }
        return new SimResult(_o.Policy, _o.Network.Mbps, _o.Network.LatencyMs, _o.Prepare, profileSessions, total, think, stall, stalls, longest,
            _trace.Reads.Count, hits, _networkBytes, _demandBytes, _prefetchBytes, useful, wasted, _requests, prepareMs, prepareBytes, acc, stats);
    }

    // ---------------- network model ----------------

    private void StartTransfer(List<(string, long)> blocks, bool demand, PrefetchItem? item)
    {
        var t = new Transfer
        {
            Blocks = blocks, RemainingInBlock = _o.BlockSize, ReadyAt = _now + _o.Network.LatencyMs, StartedAt = _now, Demand = demand, Item = item
        };
        foreach (var k in blocks) _inflight[k] = t;
        _transfers.Add(t);
        _requests++;
    }

    private void StartStream(string file, long fromBlock, long target, bool demand)
    {
        var limit = FileBlocks(file);
        var list = new List<(string, long)>();
        for (var b = fromBlock; b <= Math.Min(target, limit - 1); b++)
        {
            var k = (file, b);
            if (_cached.Contains(k) || _inflight.ContainsKey(k)) { if (list.Count > 0) break; continue; }
            list.Add(k);
        }
        if (list.Count == 0) return;
        var t = new Transfer
        {
            Blocks = list, RemainingInBlock = _o.BlockSize, ReadyAt = _now + _o.Network.LatencyMs, StartedAt = _now, Demand = demand,
            StreamFile = file, StreamTarget = Math.Min(target, limit - 1)
        };
        foreach (var k in list) _inflight[k] = t;
        _transfers.Add(t);
        _requests++;
    }

    /// <summary>rclone keeps downloading buffer-size ahead of the current read position in a file.</summary>
    private void EnsureStream(string file, long readBlock)
    {
        var target = readBlock + _o.RcloneBufferBlocks;
        var stream = _transfers.FirstOrDefault(t => t.StreamFile == file);
        if (stream != null)
        {
            var last = stream.Blocks[^1].Item2;
            if (target > stream.StreamTarget && readBlock >= stream.Blocks[stream.NextBlock].Item2 - 1)
            {
                for (var b = last + 1; b <= Math.Min(target, FileBlocks(file) - 1); b++)
                {
                    var k = (file, b);
                    if (_cached.Contains(k) || _inflight.ContainsKey(k)) break;
                    stream.Blocks.Add(k);
                    _inflight[k] = stream;
                }
                stream.StreamTarget = target;
            }
            return;
        }
        if (_cached.Contains((file, readBlock)))
        {
            var next = readBlock + 1;
            while (next <= target && _cached.Contains((file, next))) next++;
            if (next <= target && next < FileBlocks(file)) StartStream(file, next, target, demand: false);
        }
    }

    private long FileBlocks(string file) => (_trace.Files.TryGetValue(file, out var size) ? size : long.MaxValue / 2) / _o.BlockSize + 1;

    /// <summary>Processes the next network event (or advances to untilMs if nothing happens earlier).</summary>
    private void Step(double untilMs)
    {
        if (_o.Policy == SimPolicy.Engine) DispatchPrefetch();
        var active = _transfers.Where(t => t.ReadyAt <= _now + 1e-9).ToList();
        double rate = active.Count == 0 ? 0 : _o.Network.BytesPerMs / active.Count;
        double next = untilMs;
        foreach (var t in _transfers)
        {
            if (t.ReadyAt > _now + 1e-9) next = Math.Min(next, t.ReadyAt);
            else if (rate > 0) next = Math.Min(next, _now + t.RemainingInBlock / rate);
        }
        if (_o.Policy == SimPolicy.Engine && _queue!.Count > 0 && _tokensWaitUntil > _now) next = Math.Min(next, _tokensWaitUntil);
        if (double.IsPositiveInfinity(next))
        {
            if (_transfers.Count == 0 && untilMs == double.PositiveInfinity)
                throw new InvalidOperationException("Simulation stalled: waiting for data with no transfer in flight.");
            next = untilMs;
        }
        var dt = Math.Max(0, next - _now);
        foreach (var t in active)
        {
            var moved = Math.Min(t.RemainingInBlock, rate * dt);
            t.RemainingInBlock -= moved;
            _networkBytes += (long)moved;
        }
        _now = next;
        _clock.Now = T(_now);
        foreach (var t in active.Where(t => t.RemainingInBlock <= 1e-6).ToList()) CompleteBlock(t);
    }

    private double _tokensWaitUntil;

    private void CompleteBlock(Transfer t)
    {
        var k = t.Blocks[t.NextBlock];
        _inflight.Remove(k);
        AddCached(k);
        if (t.Item != null || (t.StreamFile != null && !_demanded.Contains(k))) _prefetched.Add(k);
        t.NextBlock++;
        if (t.NextBlock < t.Blocks.Count)
        {
            t.RemainingInBlock = _o.BlockSize;
            return;
        }
        _transfers.Remove(t);
        if (t.Item != null && _queue != null && _gov != null)
        {
            _queue.Complete(t.Item, true);
            _gov.Record(_o.BlockSize, TimeSpan.FromMilliseconds(_now - t.StartedAt), Math.Max(1, _transfers.Count(x => x.Item != null) + 1));
            _session?.Accounting.RecordPrefetch(t.Item.Block, t.Item.Predictor, _o.BlockSize, T(_now));
            if (_session?.Recorder.HasSeen(t.Item.Block) == true) _session.Accounting.RecordApplicationAccess(t.Item.Block);
        }
    }

    private void AdvanceTo(double target)
    {
        while (_now < target - 1e-9) Step(target);
    }

    private void AddCached((string, long) k)
    {
        if (!_cached.Add(k)) { Touch(k); return; }
        _lruNodes[k] = _lru.AddLast(k);
        if (_o.CacheCapacityBytes is { } cap)
        {
            var maxBlocks = cap / _o.BlockSize;
            while (_cached.Count > maxBlocks && _lru.First is { } first)
            {
                _lru.RemoveFirst();
                _lruNodes.Remove(first.Value);
                _cached.Remove(first.Value);
                _queue?.ForgetCached(x => x.Path == first.Value.Item1.ToLowerInvariant() && x.Block == first.Value.Item2);
            }
        }
    }

    private void Touch((string, long) k)
    {
        if (_lruNodes.TryGetValue(k, out var n))
        {
            _lru.Remove(n);
            _lru.AddLast(n);
        }
    }

    // ---------------- engine ----------------

    private int SetupEngine()
    {
        _queue = new PrefetchQueue(_clock);
        _gov = new NetworkGovernor(_o.Network.Mbps, _o.MaxConcurrency, _o.BlockSize) { BackgroundShare = _o.BackgroundShare };
        var historic = _o.Profiles?.LoadTransitions(_o.GameId) ?? new TransitionModel();
        _session = new GameSession(_o.GameId, "sim", "", _o.Policy.ToString(), T(0), historic);
        _session.Predictors.Add(new MarkovPredictor(historic, _o.Streaming.MinConfidence));
        _session.Predictors.Add(new SequentialPredictor(_gov.ReadAheadBlocks, p => FileBlocks(p)));
        foreach (var k in _cached) _queue.MarkCached("sim", k.Item1, k.Item2);
        return _o.Profiles?.LoadHistory(_o.GameId).Sessions ?? 0;
    }

    private void StartEngineSession(double at)
    {
        _session = new GameSession(_o.GameId, "sim", "", _o.Policy.ToString(), T(at), _session!.Historic);
        _session.Predictors.Add(new MarkovPredictor(_session.Historic, _o.Streaming.MinConfidence));
        _session.Predictors.Add(new SequentialPredictor(_gov!.ReadAheadBlocks, p => FileBlocks(p)));
        if (_preparedBlocks != null)
            foreach (var b in _preparedBlocks) _session.Accounting.RecordPrefetch(b, StartupPredictor.Name, _o.BlockSize, T(at));
        _queue!.BudgetBytes = _o.Streaming.SessionPrefetchBudgetBytes;
        _gameStart = at;
        if (_o.Profiles == null) return;
        var (count, history) = _o.Profiles.LoadHistory(_o.GameId);
        _startup = StartupStream.FromHistory(history, count, _o.Streaming.MinConfidence, _o.Streaming.StartupWindow, _o.Streaming.StartupHorizon.TotalMilliseconds);
        ReleaseStartup();
    }

    private StartupStream? _startup;
    private double _gameStart;

    private void ReleaseStartup()
    {
        if (_startup is null || _session is null) return;
        var progress = Math.Max(0, _now - _gameStart - _session.Recorder.StallMs);
        foreach (var p in _startup.Due(progress)) Enqueue(p);
    }

    private List<BlockKey>? _preparedBlocks;

    private double RunPrepare()
    {
        var start = _now;
        var (count, history) = _o.Profiles!.LoadHistory(_o.GameId);
        var plan = StartupPredictor.Predict(history, count, _o.Streaming.MinConfidence, _o.Streaming.StartupWindow);
        _preparedBlocks = new List<BlockKey>();
        foreach (var p in plan)
            if (!_cached.Contains((p.Block.Path, p.Block.Index)))
            {
                Enqueue(p with { Priority = p.Priority == Priority.P3Background ? Priority.P2Likely : p.Priority }, explicitRequest: true);
                _preparedBlocks.Add(p.Block);
            }
        _lastStall = -1e9;
        while (_queue!.Count > 0 || _transfers.Count > 0) Step(double.PositiveInfinity);
        return _now - start;
    }

    private void Enqueue(Prediction p, bool explicitRequest = false)
    {
        if (_cached.Contains((p.Block.Path, p.Block.Index))) return;
        var item = new PrefetchItem
        {
            MountId = "sim", MountPath = p.Block.Path, Block = p.Block, Offset = p.Block.Index * _o.BlockSize, Length = (int)_o.BlockSize,
            Owner = _session?.OwnerTag ?? "",
            Speculative = !explicitRequest,
            Ttl = explicitRequest ? TimeSpan.FromHours(6) : null
        };
        _queue!.Enqueue(item, p.Priority, p.Confidence, p.Predictor);
    }

    private void DispatchPrefetch()
    {
        var decision = SchedulingPolicy.Decide(_gov!, this, false);
        var active = _transfers.Count(t => t.Item != null);
        // Token bucket for P2/P3.
        var rate = _gov!.BackgroundBytesPerSec / 1000.0;
        _tokens = Math.Min(rate * 1000, _tokens + (_now - _tokensAt) * rate);
        _tokensAt = _now;
        while (active < decision.MaxConcurrency)
        {
            var item = _queue!.TryDequeue(decision.LowestAllowed);
            if (item is null) return;
            var k = (item.Block.Path, item.Block.Index);
            if (_cached.Contains(k) || _inflight.ContainsKey(k))
            {
                _queue.Complete(item, true);
                continue;
            }
            if (item.Priority >= Priority.P2Likely && decision.ThrottleBackground)
            {
                if (_tokens < _o.BlockSize)
                {
                    // Put it back and wake up when enough tokens exist.
                    _queue.Complete(item, false);
                    _queue.Enqueue(item, item.Priority, item.Confidence, item.Predictor);
                    _tokensWaitUntil = _now + (_o.BlockSize - _tokens) / Math.Max(1e-9, rate);
                    return;
                }
                _tokens -= _o.BlockSize;
            }
            StartTransfer([k], demand: false, item);
            active++;
        }
    }

    private void EngineOnRead(TraceRead read, List<(string Path, long b)> blocks)
    {
        var s = _session!;
        var at = T(_now);
        foreach (var (path, idx) in blocks)
        {
            var key = new BlockKey(path, idx);
            s.Recorder.Record(key, at);
            s.Accounting.RecordApplicationAccess(key);
            s.Historic.Observe(key, at);
            _queue!.MarkCached("sim", path, idx);
            if (!_o.Streaming.PredictionEnabled) continue;
            var access = new BlockAccess(key, at, null);
            foreach (var p in s.Predictors)
            {
                if (!p.Enabled) continue;
                foreach (var pred in p.OnAccess(access))
                    if (pred.Confidence >= _o.Streaming.MinConfidence) Enqueue(pred);
            }
        }
        ReleaseStartup();
        if ((long)_now / 1000 != (long)(_now - read.ThinkMs - 1) / 1000)
        {
            s.Accounting.Settle(at);
            s.Accounting.Evaluate(s.Predictors, _o.Streaming.PredictorEvaluationSamples, _o.Streaming.MinPredictorAccuracy);
        }
    }
}
