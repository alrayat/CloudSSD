using CloudSSD.Core;
using CloudSSD.Streaming;
using CloudSSD.Streaming.Simulation;

namespace CloudSSD.Tests;

public class PrefetchQueueTests
{
    private static PrefetchItem Item(string path, long block) => new()
    {
        MountId = "m", MountPath = path, Block = new BlockKey(path, block), Offset = block * ByteSize.MiB, Length = (int)ByteSize.MiB
    };

    [Fact]
    public void Dequeues_by_priority_then_confidence_then_fifo()
    {
        var q = new PrefetchQueue();
        q.Enqueue(Item("a", 1), Priority.P3Background, 0.9, "t");
        q.Enqueue(Item("a", 2), Priority.P2Likely, 0.5, "t");
        q.Enqueue(Item("a", 3), Priority.P2Likely, 0.8, "t");
        q.Enqueue(Item("a", 4), Priority.P1Imminent, 0.4, "t");
        var order = new List<long>();
        while (q.TryDequeue(Priority.P3Background) is { } i) order.Add(i.Block.Index);
        Assert.Equal(new long[] { 4, 3, 2, 1 }, order);
    }

    [Fact]
    public void Deduplicates_and_upgrades_in_place()
    {
        var q = new PrefetchQueue();
        Assert.Equal(EnqueueResult.Queued, q.Enqueue(Item("a", 1), Priority.P3Background, 0.5, "x"));
        Assert.Equal(EnqueueResult.Duplicate, q.Enqueue(Item("A", 1), Priority.P3Background, 0.5, "x"));
        Assert.Equal(EnqueueResult.Upgraded, q.Enqueue(Item("a", 1), Priority.P1Imminent, 0.9, "y"));
        Assert.Equal(1, q.Count);
        Assert.Null(q.TryDequeue(Priority.P0Critical));
        var i = q.TryDequeue(Priority.P1Imminent)!;
        Assert.Equal(Priority.P1Imminent, i.Priority);
        Assert.Equal("y", i.Predictor);
        Assert.Equal(EnqueueResult.InFlight, q.Enqueue(Item("a", 1), Priority.P1Imminent, 1, "z"));
        q.Complete(i, success: true);
        Assert.Equal(EnqueueResult.AlreadyCached, q.Enqueue(Item("a", 1), Priority.P1Imminent, 1, "z"));
        Assert.Null(q.TryDequeue(Priority.P3Background));
    }

    [Fact]
    public void Respects_allowed_priority_cancellation_ttl_and_budget()
    {
        var clock = new ManualClock(DateTime.Now);
        var q = new PrefetchQueue(clock) { BudgetBytes = 2 * ByteSize.MiB };
        q.Enqueue(Item("a", 1), Priority.P2Likely, 0.6, "t");
        q.Enqueue(Item("b", 1), Priority.P2Likely, 0.6, "t");
        q.Enqueue(Item("c", 1), Priority.P3Background, 0.6, "t");
        Assert.Null(q.TryDequeue(Priority.P1Imminent)); // foreground busy → only P1 allowed
        Assert.Equal(1, q.Cancel(i => i.MountPath == "b"));
        Assert.Equal("a", q.TryDequeue(Priority.P3Background)!.MountPath);
        clock.Advance(TimeSpan.FromHours(1)); // P3 TTL is 30 min
        Assert.Null(q.TryDequeue(Priority.P3Background));
        Assert.Equal(1, q.Expired);
        q.Enqueue(Item("d", 1), Priority.P1Imminent, 0.6, "t");
        q.Enqueue(Item("e", 1), Priority.P1Imminent, 0.6, "t");
        Assert.NotNull(q.TryDequeue(Priority.P1Imminent)); // budget 2 MiB - 1 MiB (a) = 1 MiB left
        Assert.Null(q.TryDequeue(Priority.P1Imminent));
        Assert.Equal(EnqueueResult.OverBudget, q.Enqueue(Item("f", 1), Priority.P1Imminent, 0.6, "t"));
    }

    [Fact]
    public void Every_item_that_leaves_unfetched_is_reported()
    {
        var clock = new ManualClock(DateTime.Now);
        var q = new PrefetchQueue(clock) { BudgetBytes = ByteSize.MiB };
        var dropped = new List<(string Path, string Reason)>();
        q.Dropped += (i, r) => dropped.Add((i.MountPath, r));
        q.Enqueue(Item("expire", 1), Priority.P1Imminent, 0.9, "t");
        q.Enqueue(Item("cancel", 1), Priority.P2Likely, 0.9, "t");
        q.Enqueue(Item("hit", 1), Priority.P2Likely, 0.9, "t");
        q.Cancel(i => i.MountPath == "cancel");
        q.MarkCached("m", "hit", 1);
        clock.Advance(TimeSpan.FromMinutes(1)); // P1 TTL is 30 s
        Assert.Null(q.TryDequeue(Priority.P3Background));
        Assert.Contains(("expire", "expired"), dropped);
        Assert.Contains(("cancel", "cancelled"), dropped);
        Assert.Contains(("hit", "already cached"), dropped);
    }

    [Fact]
    public void Explicit_requests_do_not_expire_or_consume_the_speculative_budget()
    {
        var clock = new ManualClock(DateTime.Now);
        var q = new PrefetchQueue(clock) { BudgetBytes = 0 };
        var item = new PrefetchItem
        {
            MountId = "m", MountPath = "prep", Block = new BlockKey("prep", 1), Offset = 0, Length = (int)ByteSize.MiB,
            Speculative = false, Ttl = TimeSpan.FromHours(6)
        };
        Assert.Equal(EnqueueResult.Queued, q.Enqueue(item, Priority.P1Imminent, 0.9, "startup"));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Same(item, q.TryDequeue(Priority.P1Imminent));
        Assert.Equal(0, q.BudgetBytes);
    }

    [Fact]
    public void Known_cached_blocks_are_never_fetched()
    {
        var q = new PrefetchQueue();
        q.Enqueue(Item("a", 7), Priority.P2Likely, 0.6, "t");
        q.MarkCached("m", "A", 7);
        Assert.Equal(0, q.Count);
        Assert.Equal(EnqueueResult.AlreadyCached, q.Enqueue(Item("a", 7), Priority.P1Imminent, 0.9, "t"));
    }
}

public class PolicyAndGovernorTests
{
    private sealed class Fg(int misses, TimeSpan since) : IForegroundMonitor
    {
        public int OutstandingMisses => misses;
        public TimeSpan SinceLastStall => since;
    }

    [Fact]
    public void Game_reads_take_precedence()
    {
        var g = new NetworkGovernor(70, 4);
        var busy = SchedulingPolicy.Decide(g, new Fg(2, TimeSpan.Zero), false);
        Assert.Equal(1, busy.MaxConcurrency);
        Assert.Equal(Priority.P1Imminent, busy.LowestAllowed);
        var justStalled = SchedulingPolicy.Decide(g, new Fg(0, TimeSpan.FromMilliseconds(200)), false);
        Assert.Equal(Priority.P2Likely, justStalled.LowestAllowed);
        var idle = SchedulingPolicy.Decide(g, new Fg(0, TimeSpan.FromMinutes(1)), false);
        Assert.Equal(Priority.P3Background, idle.LowestAllowed);
        Assert.False(idle.ThrottleBackground);
        Assert.Equal(0, SchedulingPolicy.Decide(g, new Fg(0, TimeSpan.FromMinutes(1)), true).MaxConcurrency);
    }

    [Fact]
    public void Concurrency_follows_bandwidth_delay_product()
    {
        var g = new NetworkGovernor(70, 8);
        for (int i = 0; i < 10; i++) g.Record(ByteSize.MiB, TimeSpan.FromMilliseconds(120 + 1000.0 * ByteSize.MiB / 8_750_000 * 0.9), 1);
        Assert.InRange(g.ThroughputBytesPerSec, 6_000_000, 10_000_000);
        Assert.InRange(g.RecommendedConcurrency(), 2, 3);
        Assert.InRange(g.ReadAheadBlocks(), 5, 10);
        var slow = new NetworkGovernor(10, 8);
        Assert.Equal(2, slow.ReadAheadBlocks());
    }
}

public class PredictorTests
{
    [Fact]
    public void Markov_learns_successors_with_calibrated_confidence()
    {
        var m = new TransitionModel();
        var t = DateTime.Now;
        for (int i = 0; i < 4; i++)
        {
            m.ResetRecent();
            m.Observe(new BlockKey("a", 1), t);
            m.Observe(new BlockKey("b", 9), t.AddMilliseconds(5));
        }
        m.ResetRecent();
        m.Observe(new BlockKey("a", 1), t);
        m.Observe(new BlockKey("c", 3), t.AddMilliseconds(5));
        var succ = m.Successors(new BlockKey("a", 1), 4);
        Assert.Equal(new BlockKey("b", 9), succ[0].Block);
        Assert.InRange(succ[0].Probability, 0.6, 0.7); // 4 / (5 + 1)
        var p = new MarkovPredictor(m, 0.4);
        var preds = p.OnAccess(new BlockAccess(new BlockKey("a", 1), t, false)).ToList();
        Assert.Single(preds);
        Assert.Equal(Priority.P2Likely, preds[0].Priority);
    }

    [Fact]
    public void Transitions_ignore_gaps_longer_than_window()
    {
        var m = new TransitionModel(maxGap: TimeSpan.FromSeconds(1));
        var t = DateTime.Now;
        m.Observe(new BlockKey("a", 1), t);
        m.Observe(new BlockKey("b", 1), t.AddSeconds(5));
        Assert.Empty(m.Successors(new BlockKey("a", 1), 3));
    }

    [Fact]
    public void Sequential_detects_streams_and_stops_at_eof()
    {
        var p = new SequentialPredictor(() => 4, _ => 10);
        var t = DateTime.Now;
        Assert.Empty(p.OnAccess(new BlockAccess(new BlockKey("f", 5), t, null)));
        Assert.Empty(p.OnAccess(new BlockAccess(new BlockKey("f", 6), t, null)));
        var preds = p.OnAccess(new BlockAccess(new BlockKey("f", 7), t, null)).ToList();
        Assert.Equal(new long[] { 8, 9 }, preds.Select(x => x.Block.Index)); // file has 10 blocks
        Assert.Empty(p.OnAccess(new BlockAccess(new BlockKey("f", 2), t, null))); // random jump resets
    }

    [Fact]
    public void Startup_prediction_uses_smoothed_confidence_and_ordering()
    {
        var hist = new List<BlockHistory>
        {
            new(new BlockKey("x", 1), 5, 900),
            new(new BlockKey("x", 2), 5, 100),
            new(new BlockKey("x", 3), 1, 50),      // seen once in 5 sessions → low confidence
            new(new BlockKey("x", 4), 5, 500_000), // outside startup window
        };
        var p = StartupPredictor.Predict(hist, 5, 0.4, TimeSpan.FromSeconds(120));
        Assert.Equal(new long[] { 2, 1 }, p.Select(x => x.Block.Index));
        Assert.All(p, x => Assert.Equal(Priority.P1Imminent, x.Priority));
        var single = StartupPredictor.Predict([new BlockHistory(new BlockKey("x", 1), 1, 10)], 1, 0.4, TimeSpan.FromMinutes(2));
        Assert.InRange(single[0].Confidence, 0.7, 0.8); // one observation is not certainty
    }

    [Fact]
    public void Accounting_tracks_useful_and_wasted_and_disables_bad_predictors()
    {
        var a = new PredictionAccounting(TimeSpan.FromSeconds(10));
        var t = DateTime.Now;
        for (int i = 0; i < 10; i++) a.RecordPrefetch(new BlockKey("good", i), "good", 100, t);
        for (int i = 0; i < 10; i++) a.RecordPrefetch(new BlockKey("bad", i), "bad", 100, t);
        for (int i = 0; i < 9; i++) Assert.Equal("good", a.RecordApplicationAccess(new BlockKey("good", i)));
        Assert.Null(a.RecordApplicationAccess(new BlockKey("good", 0)));
        a.RecordApplicationAccess(new BlockKey("bad", 0));
        a.Settle(t.AddSeconds(30));
        var s = a.Snapshot();
        Assert.Equal(0.9, s["good"].Accuracy!.Value, 3);
        Assert.Equal(0.1, s["bad"].Accuracy!.Value, 3);
        Assert.Equal(900, s["bad"].WastedBytes);
        var preds = new IPredictor[] { new SequentialPredictor(() => 1, _ => null), new FakePredictor("good"), new FakePredictor("bad") };
        var disabled = a.Evaluate(preds, minSamples: 10, minAccuracy: 0.25);
        Assert.Equal(["bad"], disabled);
        Assert.False(preds[2].Enabled);
        Assert.True(preds[1].Enabled);
    }

    private sealed class FakePredictor(string name) : IPredictor
    {
        public string Name => name;
        public bool Enabled { get; set; } = true;
        public IEnumerable<Prediction> OnAccess(BlockAccess access) => [];
    }
}

public class ProfileStoreTests
{
    [Fact]
    public void Sessions_persist_history_transitions_and_predictor_stats()
    {
        var db = Path.Combine(Directory.CreateTempSubdirectory("cloudssd-cli-prof").FullName, "p.db");
        var store = new ProfileStore(db);
        var t0 = DateTime.Now;
        for (int s = 0; s < 3; s++)
        {
            var rec = new SessionRecorder(t0);
            rec.Record(new BlockKey("game.exe", 0), t0.AddMilliseconds(10 + s));
            rec.Record(new BlockKey("data.pak", 5), t0.AddMilliseconds(200));
            if (s == 0) rec.Record(new BlockKey("rare.pak", 1), t0.AddMilliseconds(300));
            var acc = new PredictionAccounting();
            acc.RecordPrefetch(new BlockKey("data.pak", 5), "markov", 100, t0);
            acc.RecordApplicationAccess(new BlockKey("data.pak", 5));
            store.SaveSession("g1", ByteSize.MiB, rec, t0.AddSeconds(1), acc, "test");
        }
        var (count, hist) = store.LoadHistory("g1");
        Assert.Equal(3, count);
        Assert.Equal(3, hist.Single(h => h.Block.Path == "game.exe").SessionsSeen);
        Assert.Equal(11, hist.Single(h => h.Block.Path == "game.exe").MedianFirstAccessMs, 1);
        Assert.Equal(1, hist.Single(h => h.Block.Path == "rare.pak").SessionsSeen);
        var model = store.LoadTransitions("g1");
        Assert.Equal(new BlockKey("data.pak", 5), model.Successors(new BlockKey("game.exe", 0), 2)[0].Block);
        var stats = store.LoadPredictorStats("g1").Single();
        Assert.Equal(3, stats.Useful);
        var summary = store.Summary("g1", TimeSpan.FromMinutes(2));
        Assert.Equal(3, summary.Sessions);
        Assert.Equal(2 * ByteSize.MiB, summary.StartupBytesEstimate);
        store.DeleteProfile("g1");
        Assert.Equal(0, store.LoadHistory("g1").Sessions);
    }
}

public class SimulatorTests
{
    [Fact]
    public void Synthetic_traces_are_deterministic_and_sessions_differ()
    {
        var a = SyntheticGame.Session(1, 0, scale: 0.2);
        var b = SyntheticGame.Session(1, 0, scale: 0.2);
        var c = SyntheticGame.Session(1, 1, scale: 0.2);
        Assert.Equal(a.Reads, b.Reads);
        Assert.NotEqual(a.Reads, c.Reads);
        Assert.True(a.Reads.Count > 1000);
    }

    [Fact]
    public void Ssd_baseline_has_no_stalls_and_cold_cloud_does()
    {
        var trace = SyntheticGame.Session(3, 0, scale: 0.15);
        var net = new SimNetwork(70, 150);
        var ssd = new TraceSimulator(trace, new SimOptions { Policy = SimPolicy.SsdBaseline, Network = net }).Run();
        var cold = new TraceSimulator(trace, new SimOptions { Policy = SimPolicy.OnDemand, Network = net }).Run();
        Assert.Equal(0, ssd.StallMs);
        Assert.Equal(0, ssd.NetworkBytes);
        Assert.True(cold.StallMs > 1000);
        Assert.True(cold.TotalMs > ssd.TotalMs);
        Assert.Equal(trace.Reads.Count, cold.Reads);
    }

    [Fact]
    public void Warm_profile_with_prepare_reduces_stalls_versus_on_demand()
    {
        var db = Path.Combine(Directory.CreateTempSubdirectory("cloudssd-cli-sim").FullName, "p.db");
        var store = new ProfileStore(db);
        var net = new SimNetwork(70, 150);
        for (int s = 0; s < 3; s++)
            new TraceSimulator(SyntheticGame.Session(5, s, scale: 0.15), new SimOptions
            {
                Policy = SimPolicy.Engine, Network = net, Profiles = store, GameId = "g", RecordProfile = true
            }).Run();
        var test = SyntheticGame.Session(5, 3, scale: 0.15);
        var baseline = new TraceSimulator(test, new SimOptions { Policy = SimPolicy.OnDemand, Network = net }).Run();
        var warm = new TraceSimulator(test, new SimOptions { Policy = SimPolicy.Engine, Network = net, Profiles = store, GameId = "g", Prepare = true }).Run();
        var msg = $"warm stall {warm.StallMs:0} ms hit {warm.HitRate:P1} prep {warm.PrepareMs:0} ms/{warm.PrepareBytes} B acc {warm.PredictionAccuracy:P0} | baseline stall {baseline.StallMs:0} ms hit {baseline.HitRate:P1}";
        Console.WriteLine(msg);
        Assert.True(warm.StallMs < baseline.StallMs * 0.6, msg);
        Assert.True(warm.HitRate > baseline.HitRate);
        Assert.True(warm.PrepareBytes > 0);
        Assert.NotNull(warm.PredictionAccuracy);
    }
}
