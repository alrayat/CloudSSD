using CloudSSD.Core;

namespace CloudSSD.Streaming;

/// <summary>
/// Download priority. P0 is the game's own read, served directly by rclone; CloudSSD never competes with it and
/// only schedules P1-P3 work.
/// </summary>
public enum Priority { P0Critical = 0, P1Imminent = 1, P2Likely = 2, P3Background = 3 }

/// <summary>A predicted future read of one block, with the evidence-based confidence that produced it.</summary>
public sealed record Prediction(BlockKey Block, Priority Priority, double Confidence, string Predictor);

/// <summary>One observed application read reduced to block granularity, relative to the game root.</summary>
public readonly record struct BlockAccess(BlockKey Block, DateTime Time, bool? Hit);

public interface IPredictor
{
    string Name { get; }
    bool Enabled { get; set; }

    /// <summary>Called for every application block access in the session (in order).</summary>
    IEnumerable<Prediction> OnAccess(BlockAccess access);
}

/// <summary>
/// Learns "block A is usually followed by block B" (first-order transitions between distinct consecutive blocks within
/// a short time window) and predicts successors whose conditional probability is high enough. Deterministic and
/// fully explainable: confidence = count(A→B) / (count(A) + 1).
/// </summary>
public sealed class MarkovPredictor : IPredictor
{
    private readonly TransitionModel _model;
    private readonly double _minConfidence;
    private readonly int _maxSuccessors;
    private BlockKey? _last;

    public MarkovPredictor(TransitionModel model, double minConfidence = 0.4, int maxSuccessors = 4)
    {
        _model = model;
        _minConfidence = minConfidence;
        _maxSuccessors = maxSuccessors;
    }

    public string Name => "markov";
    public bool Enabled { get; set; } = true;

    public IEnumerable<Prediction> OnAccess(BlockAccess access)
    {
        if (_last == access.Block) yield break;
        _last = access.Block;
        foreach (var (succ, p) in _model.Successors(access.Block, _maxSuccessors))
        {
            if (p < _minConfidence) break;
            yield return new Prediction(succ, p >= 0.75 ? Priority.P1Imminent : Priority.P2Likely, p, Name);
        }
    }
}

/// <summary>Transition counts between blocks. Thread-safe. Learning and prediction use the same instance.</summary>
public sealed class TransitionModel
{
    private readonly Dictionary<BlockKey, Dictionary<BlockKey, int>> _edges = new();
    private readonly Dictionary<BlockKey, int> _occurrences = new();
    private readonly object _gate = new();
    private readonly int _window;
    private readonly TimeSpan _maxGap;
    private readonly LinkedList<(BlockKey Block, DateTime Time)> _recent = new();

    public TransitionModel(int window = 3, TimeSpan? maxGap = null)
    {
        _window = window;
        _maxGap = maxGap ?? TimeSpan.FromSeconds(3);
    }

    public int BlockCount { get { lock (_gate) return _occurrences.Count; } }

    /// <summary>Learns from one live access: every distinct block among the last few (within the time gap) gains an edge.</summary>
    public void Observe(BlockKey block, DateTime time)
    {
        lock (_gate)
        {
            if (_recent.Last is { } last && last.Value.Block == block) return;
            while (_recent.First is { } f && time - f.Value.Time > _maxGap) _recent.RemoveFirst();
            _occurrences[block] = _occurrences.GetValueOrDefault(block) + 1;
            int distance = 0;
            for (var n = _recent.Last; n != null && distance < _window; n = n.Previous, distance++)
            {
                if (n.Value.Block == block) continue;
                // Only the immediate predecessor teaches the strongest edge; older ones a weaker one.
                if (distance == 0) AddEdge(n.Value.Block, block, 1);
            }
            _recent.AddLast((block, time));
            if (_recent.Count > _window) _recent.RemoveFirst();
        }
    }

    public void ResetRecent()
    {
        lock (_gate) _recent.Clear();
    }

    public void AddEdge(BlockKey from, BlockKey to, int count)
    {
        lock (_gate)
        {
            if (!_edges.TryGetValue(from, out var d)) _edges[from] = d = new Dictionary<BlockKey, int>();
            d[to] = d.GetValueOrDefault(to) + count;
        }
    }

    public void AddOccurrences(BlockKey block, int count)
    {
        lock (_gate) _occurrences[block] = _occurrences.GetValueOrDefault(block) + count;
    }

    public IReadOnlyList<(BlockKey Block, double Probability)> Successors(BlockKey from, int max)
    {
        lock (_gate)
        {
            if (!_edges.TryGetValue(from, out var d) || d.Count == 0) return [];
            var n = Math.Max(_occurrences.GetValueOrDefault(from), d.Values.Max());
            return d.OrderByDescending(kv => kv.Value).Take(max)
                .Select(kv => (kv.Key, kv.Value / (double)(n + 1))).ToList();
        }
    }

    public IEnumerable<(BlockKey From, BlockKey To, int Count)> Edges()
    {
        lock (_gate)
            return _edges.SelectMany(e => e.Value.Select(t => (e.Key, t.Key, t.Value))).ToList();
    }

    public IEnumerable<(BlockKey Block, int Count)> Occurrences()
    {
        lock (_gate) return _occurrences.Select(kv => (kv.Key, kv.Value)).ToList();
    }
}

/// <summary>
/// Detects sequential streams inside a file and reads ahead. Depth adapts to measured throughput so that, at 70 Mbps,
/// read-ahead covers roughly one second of transfer instead of a fixed large buffer.
/// </summary>
public sealed class SequentialPredictor : IPredictor
{
    private sealed class Stream
    {
        public long LastBlock = -10;
        public int Run;
    }

    private readonly Dictionary<string, Stream> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<int> _depth;
    private readonly Func<string, long?> _fileBlocks;

    /// <param name="depth">Current read-ahead depth in blocks (from the network governor).</param>
    /// <param name="fileBlocks">Number of blocks in a file, if known (avoids predicting past EOF).</param>
    public SequentialPredictor(Func<int> depth, Func<string, long?> fileBlocks)
    {
        _depth = depth;
        _fileBlocks = fileBlocks;
    }

    public string Name => "sequential";
    public bool Enabled { get; set; } = true;
    public int MinRun { get; init; } = 3;

    public IEnumerable<Prediction> OnAccess(BlockAccess access)
    {
        var key = access.Block.Path;
        if (!_streams.TryGetValue(key, out var s)) _streams[key] = s = new Stream();
        var b = access.Block.Index;
        if (b == s.LastBlock) yield break;
        s.Run = b == s.LastBlock + 1 ? s.Run + 1 : 1;
        s.LastBlock = b;
        if (_streams.Count > 4096) _streams.Clear();
        if (s.Run < MinRun) yield break;

        var depth = Math.Max(1, _depth());
        var limit = _fileBlocks(key);
        var confidence = Math.Min(0.95, 0.5 + 0.08 * s.Run);
        for (int i = 1; i <= depth; i++)
        {
            var next = b + i;
            if (limit is { } l && next >= l) yield break;
            yield return new Prediction(access.Block with { Index = next }, i <= 2 ? Priority.P1Imminent : Priority.P2Likely, confidence, Name);
        }
    }
}

/// <summary>Per-block statistics across recorded sessions, used for startup prediction.</summary>
public sealed record BlockHistory(BlockKey Block, int SessionsSeen, double MedianFirstAccessMs);

/// <summary>
/// Predicts the data a game reads during startup from previous launches. Confidence uses Laplace smoothing
/// ((seen + 0.5) / (sessions + 1)) so a single observation is never treated as certainty, and a block must have been
/// read in a majority of recorded launches (confidence strictly above 0.5) - data of a level loaded in only one of two
/// launches is not "startup" data.
/// </summary>
public static class StartupPredictor
{
    public const string Name = "startup";

    public static List<Prediction> Predict(IReadOnlyList<BlockHistory> history, int sessionCount, double minConfidence, TimeSpan window)
    {
        if (sessionCount <= 0) return [];
        var list = new List<(Prediction P, double T)>();
        foreach (var h in history)
        {
            if (h.MedianFirstAccessMs > window.TotalMilliseconds) continue;
            var conf = (h.SessionsSeen + 0.5) / (sessionCount + 1.0);
            if (conf < minConfidence || conf <= 0.5) continue;
            var prio = conf >= 0.7 && h.MedianFirstAccessMs < 15_000 ? Priority.P1Imminent
                : conf >= 0.5 ? Priority.P2Likely
                : Priority.P3Background;
            list.Add((new Prediction(h.Block, prio, conf, Name), h.MedianFirstAccessMs));
        }
        return list.OrderBy(x => x.T).Select(x => x.P).ToList();
    }
}

/// <summary>
/// Releases a learned startup sequence just in time: a predicted block becomes due when the game's progress time
/// (wall time minus time spent waiting on the network) comes within <see cref="HorizonMs"/> of the block's typical
/// first access. Scheduling the whole startup set at launch was measured (simulation) to compete with the game's own
/// reads and waste bandwidth on slow links; releasing it progressively keeps prefetch ahead of the game without
/// flooding the link. Optimize &amp; Prepare still fetches the whole set up front, when nothing else needs bandwidth.
/// </summary>
public sealed class StartupStream
{
    private readonly List<(Prediction P, double FirstMs)> _items;
    private int _next;

    public StartupStream(IEnumerable<(Prediction P, double FirstMs)> items) =>
        _items = items.OrderBy(i => i.FirstMs).ToList();

    public double HorizonMs { get; init; } = 10_000;

    /// <summary>
    /// During a live launch only consistently observed data is worth competing with the game for bandwidth; data seen in
    /// some launches only (e.g. a level loaded in 2 of 3 launches) is left to Optimize &amp; Prepare. Measured in simulation.
    /// </summary>
    public double MinConfidence { get; init; } = 0.7;

    public int Remaining => _items.Count - _next;

    public IEnumerable<Prediction> Due(double progressMs)
    {
        while (_next < _items.Count && _items[_next].FirstMs <= progressMs + HorizonMs)
        {
            var p = _items[_next++].P;
            if (p.Confidence >= MinConfidence) yield return p;
        }
    }

    public static StartupStream FromHistory(IReadOnlyList<BlockHistory> history, int sessions, double minConfidence, TimeSpan window, double horizonMs)
    {
        var times = history.ToDictionary(h => h.Block, h => h.MedianFirstAccessMs);
        return new StartupStream(StartupPredictor.Predict(history, sessions, minConfidence, window).Select(p => (p, times[p.Block]))) { HorizonMs = horizonMs };
    }
}

/// <summary>
/// Tracks which prefetched blocks the game later read (useful) and which it never touched (wasted), per predictor.
/// Predictors whose measured accuracy falls below the threshold after enough samples are disabled automatically.
/// </summary>
public sealed class PredictionAccounting
{
    private sealed class Issued
    {
        public required string Predictor;
        public required long Bytes;
        public required DateTime At;
        public bool Used;
    }

    public sealed class PredictorStats
    {
        public long IssuedBlocks;
        public long UsefulBlocks;
        public long IssuedBytes;
        public long UsefulBytes;
        public long WastedBlocks;
        public long WastedBytes;
        public bool AutoDisabled;
        public double? Accuracy => UsefulBlocks + WastedBlocks == 0 ? null : UsefulBlocks / (double)(UsefulBlocks + WastedBlocks);
    }

    private readonly Dictionary<BlockKey, Issued> _issued = new();
    private readonly Dictionary<string, PredictorStats> _stats = new();
    private readonly object _gate = new();
    private readonly TimeSpan _horizon;

    public PredictionAccounting(TimeSpan? horizon = null) => _horizon = horizon ?? TimeSpan.FromMinutes(3);

    public void RecordPrefetch(BlockKey block, string predictor, long bytes, DateTime at)
    {
        lock (_gate)
        {
            if (_issued.ContainsKey(block)) return;
            _issued[block] = new Issued { Predictor = predictor, Bytes = bytes, At = at };
            var s = Stats(predictor);
            s.IssuedBlocks++;
            s.IssuedBytes += bytes;
        }
    }

    /// <summary>Returns the predictor credited, if this application access consumed a prefetched block.</summary>
    public string? RecordApplicationAccess(BlockKey block)
    {
        lock (_gate)
        {
            if (!_issued.TryGetValue(block, out var i) || i.Used) return null;
            i.Used = true;
            var s = Stats(i.Predictor);
            s.UsefulBlocks++;
            s.UsefulBytes += i.Bytes;
            return i.Predictor;
        }
    }

    /// <summary>Marks prefetched blocks unused for longer than the horizon as wasted. Call periodically and at session end.</summary>
    public void Settle(DateTime now, bool sessionEnded = false)
    {
        lock (_gate)
        {
            foreach (var (k, i) in _issued.ToList())
            {
                if (i.Used) { if (now - i.At > _horizon) _issued.Remove(k); continue; }
                if (!sessionEnded && now - i.At <= _horizon) continue;
                var s = Stats(i.Predictor);
                s.WastedBlocks++;
                s.WastedBytes += i.Bytes;
                _issued.Remove(k);
            }
        }
    }

    /// <summary>Disables predictors with poor measured accuracy. Returns the names disabled by this call.</summary>
    public List<string> Evaluate(IEnumerable<IPredictor> predictors, int minSamples, double minAccuracy)
    {
        var disabled = new List<string>();
        lock (_gate)
        {
            foreach (var p in predictors)
            {
                if (!p.Enabled || !_stats.TryGetValue(p.Name, out var s)) continue;
                if (s.UsefulBlocks + s.WastedBlocks >= minSamples && s.Accuracy < minAccuracy)
                {
                    p.Enabled = false;
                    s.AutoDisabled = true;
                    disabled.Add(p.Name);
                }
            }
        }
        return disabled;
    }

    public IReadOnlyDictionary<string, PredictorStats> Snapshot()
    {
        lock (_gate) return _stats.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public (long UsefulBytes, long WastedBytes, double? Accuracy) Totals()
    {
        lock (_gate)
        {
            long u = _stats.Values.Sum(s => s.UsefulBlocks), w = _stats.Values.Sum(s => s.WastedBlocks);
            return (_stats.Values.Sum(s => s.UsefulBytes), _stats.Values.Sum(s => s.WastedBytes), u + w == 0 ? null : u / (double)(u + w));
        }
    }

    private PredictorStats Stats(string name)
    {
        if (!_stats.TryGetValue(name, out var s)) _stats[name] = s = new PredictorStats();
        return s;
    }
}
