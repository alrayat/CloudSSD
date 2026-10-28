using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CloudSSD.App;
using CloudSSD.Cache;
using CloudSSD.Core;
using CloudSSD.Games;
using CloudSSD.SimCloud;
using CloudSSD.Streaming;
using CloudSSD.Streaming.Simulation;

namespace CloudSSD.Benchmarks;

/// <summary>
/// Reproducible CloudSSD benchmarks.
///   sim  [--scale 0.25]                       deterministic trace simulation matrix (no I/O)
///   real --dataset &lt;dir&gt; [--mbps 70] [--latency 80] real launches of the CLOUDSSD demo game over SimCloud + rclone + WinFsp
/// Results are written as Markdown and JSON to benchmarks/results/.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "sim";
        var outDir = Path.Combine(FindRepo(), "benchmarks", "results");
        Directory.CreateDirectory(outDir);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        return mode switch
        {
            "sim" => Simulation.Run(outDir, double.TryParse(Opt(args, "--scale"), out var s) ? s : 0.25),
            "real" => await RealBenchmark.RunAsync(outDir, Opt(args, "--dataset") ?? Path.Combine(FindRepo(), ".work", "it", "dataset"),
                double.TryParse(Opt(args, "--mbps"), out var m) ? m : 70, double.TryParse(Opt(args, "--latency"), out var l) ? l : 80),
            _ => Usage()
        };
    }

    private static int Usage()
    {
        Console.WriteLine("usage: CloudSSD.Benchmarks sim [--scale 0.25] | real [--dataset dir] [--mbps 70] [--latency 80]");
        return 64;
    }

    public static string? Opt(string[] a, string n)
    {
        var i = Array.IndexOf(a, n);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    public static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "CloudSSD.slnx"))) d = d.Parent;
        return d?.FullName ?? Directory.GetCurrentDirectory();
    }
}

internal static class Simulation
{
    public static int Run(string outDir, double scale)
    {
        var rows = new List<(string Scenario, SimResult R)>();
        var sw = Stopwatch.StartNew();
        const int gameSeed = 11;
        foreach (var latency in new[] { 40.0, 150.0 })
        foreach (var mbps in new[] { 10.0, 30.0, 70.0, 100.0, 500.0 })
        {
            var net = new SimNetwork(mbps, latency);
            var db = Path.Combine(Path.GetTempPath(), $"cloudssd-cli-bench-{Guid.NewGuid():N}.db");
            var store = new ProfileStore(db);
            for (int s = 0; s < 3; s++)
                new TraceSimulator(SyntheticGame.Session(gameSeed, s, scale: scale), new SimOptions
                {
                    Policy = SimPolicy.Engine, Network = net, Profiles = store, GameId = "g", RecordProfile = true
                }).Run();
            var test = SyntheticGame.Session(gameSeed, 3, scale: scale);
            void Add(string name, SimOptions o) => rows.Add((name, new TraceSimulator(test, o).Run()));
            Add("A SSD baseline", new SimOptions { Policy = SimPolicy.SsdBaseline, Network = net });
            Add("B rclone default (buffer 16M)", new SimOptions { Policy = SimPolicy.RcloneDefault, Network = net });
            Add("B0 rclone buffer 0, no engine", new SimOptions { Policy = SimPolicy.OnDemand, Network = net });
            Add("C CLOUDSSD cold (no profile)", new SimOptions { Policy = SimPolicy.Engine, Network = net });
            Add("D CLOUDSSD warm profile, no Prepare", new SimOptions { Policy = SimPolicy.Engine, Network = net, Profiles = store, GameId = "g" });
            Add("D+ CLOUDSSD warm profile + Prepare", new SimOptions { Policy = SimPolicy.Engine, Network = net, Profiles = store, GameId = "g", Prepare = true });
            Add("D+ with 256 MiB cache limit", new SimOptions
            {
                Policy = SimPolicy.Engine, Network = net, Profiles = store, GameId = "g", Prepare = true, CacheCapacityBytes = 256 * ByteSize.MiB
            });
            SqliteCleanup(db);
        }
        var trace = SyntheticGame.Session(gameSeed, 3, scale: scale);
        var md = new StringBuilder();
        md.AppendLine($"# Simulation benchmark — {DateTime.Now:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine($"Synthetic game seed {gameSeed}, scale {scale}: test session has {trace.Reads.Count:N0} reads touching {ByteSize.Format(trace.UniqueBytes(ByteSize.MiB))} of unique 1 MiB blocks. " +
                      "Profile trained on 3 earlier sessions (different levels). Discrete-event model: shared bandwidth at 90% efficiency, fixed per-request latency, " +
                      "SSD reads at 500 MB/s, 0.15 ms VFS overhead per hit. THIS IS A SIMULATION, not a measurement of a real game.");
        md.AppendLine();
        md.AppendLine("| Link | Latency | Scenario | Load time | Stall time | Stalls ≥50 ms | Hit rate | Cloud bytes | Prefetch useful / wasted | Prepare time | Accuracy |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (name, r) in rows)
            md.AppendLine($"| {r.Mbps} Mbps | {r.LatencyMs} ms | {name} | {r.TotalMs / 1000:0.0} s | {r.StallMs / 1000:0.0} s | {r.StallCount} | {r.HitRate:P1} | {ByteSize.Format(r.NetworkBytes)} | " +
                          $"{ByteSize.Format(r.UsefulPrefetchBytes)} / {ByteSize.Format(r.WastedPrefetchBytes)} | {(r.Prepared ? $"{r.PrepareMs / 1000:0.0} s" : "—")} | {(r.PredictionAccuracy is { } a ? a.ToString("P0") : "—")} |");
        md.AppendLine();
        md.AppendLine($"Computed in {sw.Elapsed.TotalSeconds:0.0} s.");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.WriteAllText(Path.Combine(outDir, $"sim-{stamp}.md"), md.ToString());
        File.WriteAllText(Path.Combine(outDir, $"sim-{stamp}.json"), JsonSerializer.Serialize(rows.Select(x => new { x.Scenario, x.R.Mbps, x.R.LatencyMs, x.R.TotalMs, x.R.StallMs, x.R.StallCount, x.R.HitRate, x.R.NetworkBytes, x.R.UsefulPrefetchBytes, x.R.WastedPrefetchBytes, x.R.PrepareMs, x.R.PrepareBytes, x.R.PredictionAccuracy }), new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(md);
        return 0;
    }

    private static void SqliteCleanup(string db)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { db, db + "-wal", db + "-shm" }) try { File.Delete(f); } catch (IOException) { }
    }
}

internal static class RealBenchmark
{
    private sealed record Row(string Scenario, double LoadMs, double StartupMs, double MenuMs, double LevelMs, double WorstReadMs, long CloudBytes, long Requests,
        long Hits, long Misses, long StallsOver50, double RcloneCpuS, long RclonePeakMb, double PrepareS, long PrepareBytes, string Notes);

    public static async Task<int> RunAsync(string outDir, string dataset, double mbps, double latencyMs)
    {
        var gameDir = Path.Combine(dataset, "Games", "CloudSSD Demo Game");
        if (!File.Exists(Path.Combine(gameDir, "CloudSSD.DemoGame.exe")))
        {
            Console.Error.WriteLine($"Demo game not found in {gameDir}. Install it first (see TEST_REPORT.md).");
            return 2;
        }
        var work = Path.Combine(Program.FindRepo(), ".work", "bench-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(work);
        var results = Path.Combine(work, "demo-results.jsonl");
        var rows = new List<Row>();

        // A. Physical SSD baseline: the same executable and data on the local SSD.
        for (int i = 0; i < 2; i++)
        {
            var r = await RunExeAsync(Path.Combine(gameDir, "CloudSSD.DemoGame.exe"), 2, results);
            rows.Add(new Row(i == 0 ? "A SSD baseline (first run)" : "A SSD baseline (second run)", r.Load, r.Startup, r.Menu, r.Level, r.Worst, 0, 0, 0, 0, 0, 0, 0, 0, 0, "local NTFS"));
        }

        await using var server = new SimCloudServer(dataset) { BandwidthMbps = mbps, Latency = TimeSpan.FromMilliseconds(latencyMs) };

        // B. Standard rclone mount: rclone defaults (buffer 16M, chunk 128M), no prediction.
        rows.Add(await CloudScenarioAsync(work, server, results, "B standard rclone mount (defaults), cold", standardRclone: true, prediction: false, train: false, prepare: false, level: 2));
        // C. CloudSSD, cold: no profile, empty cache, engine on.
        rows.Add(await CloudScenarioAsync(work, server, results, "C CloudSSD cold (no profile)", standardRclone: false, prediction: true, train: false, prepare: false, level: 2));
        // D. CloudSSD warm: profile from 2 launches (levels 0,1), cache cleared, Optimize & Prepare, then unseen level 2.
        var (d, warm) = await CloudScenarioWithWarmAsync(work, server, results, level: 2);
        rows.Add(d);
        rows.Add(warm);

        var md = new StringBuilder();
        md.AppendLine($"# Real end-to-end benchmark — {DateTime.Now:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine($"Workload: CLOUDSSD demo game (DRM-free test program) loading level 2: 409 reads, 54.5 MiB, from 4 × 96 MiB archives. " +
                      $"Cloud: SimCloud on 127.0.0.1 throttled to {mbps} Mbps with {latencyMs} ms per-request latency, mounted through rclone + WinFsp (same flags the app uses unless noted). " +
                      "Load times are measured by the demo game itself (wall clock from first to last read). Each cloud scenario starts with an empty cache unless stated.");
        md.AppendLine();
        md.AppendLine("| Scenario | Load | Startup / Menu / Level phases | Worst read | Cloud bytes | HTTP requests | Hits / Misses | Reads ≥50 ms | rclone CPU | rclone peak RAM | Prepare | Notes |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            md.AppendLine($"| {r.Scenario} | {r.LoadMs / 1000:0.00} s | {r.StartupMs:0} / {r.MenuMs:0} / {r.LevelMs:0} ms | {r.WorstReadMs:0} ms | {ByteSize.Format(r.CloudBytes)} | {r.Requests} | " +
                          $"{r.Hits} / {r.Misses} | {r.StallsOver50} | {r.RcloneCpuS:0.0} s | {r.RclonePeakMb} MiB | {(r.PrepareS > 0 ? $"{r.PrepareS:0.0} s, {ByteSize.Format(r.PrepareBytes)}" : "—")} | {r.Notes} |");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.WriteAllText(Path.Combine(outDir, $"real-{mbps}mbps-{stamp}.md"), md.ToString());
        File.WriteAllText(Path.Combine(outDir, $"real-{mbps}mbps-{stamp}.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(md);
        return 0;
    }

    private sealed record ExeResult(double Load, double Startup, double Menu, double Level, double Worst);

    private static async Task<ExeResult> RunExeAsync(string exe, int level, string results)
    {
        var before = File.Exists(results) ? File.ReadAllLines(results).Length : 0;
        using var p = Process.Start(new ProcessStartInfo(exe, $"--level {level} --autoexit --results \"{results}\"") { UseShellExecute = false })!;
        await p.WaitForExitAsync();
        var lines = File.ReadAllLines(results);
        if (lines.Length <= before) throw new InvalidOperationException("demo game wrote no result");
        return Parse(lines[^1]);
    }

    private static ExeResult Parse(string json)
    {
        var e = JsonDocument.Parse(json).RootElement;
        var ph = e.GetProperty("phases");
        return new ExeResult(e.GetProperty("loadMs").GetDouble(), ph.GetProperty("startup").GetDouble(), ph.GetProperty("menu").GetDouble(),
            ph.GetProperty("level").GetDouble(), e.GetProperty("worstReadMs").GetDouble());
    }

    private static async Task<AppHost> NewHostAsync(string work, SimCloudServer server, string name, bool standardRclone, bool prediction)
    {
        var home = Path.Combine(work, name);
        var paths = new AppPaths(home);
        var s = new AppSettings
        {
            FirstRunCompleted = true,
            Cache = new CacheSettings { CacheRoot = Path.Combine(home, "cache"), BudgetBytes = 2 * ByteSize.GiB, MinFreeSpaceBytes = 2 * ByteSize.GiB },
            Network = new NetworkSettings { DeclaredDownloadMbps = server.BandwidthMbps, TpsLimit = 0 },
            Streaming = new StreamingSettings { PredictionEnabled = prediction, ReadProfilingEnabled = true },
            LocalGamesRoot = Path.Combine(home, "local"),
        };
        if (standardRclone)
        {
            // rclone's own defaults for the read path.
            s.Cache.BufferSizeBytes = 16 * ByteSize.MiB;
            s.Cache.ReadChunkSizeBytes = 128 * ByteSize.MiB;
        }
        s.Mounts.Add(new MountDefinition { Id = "sim", DisplayName = "SimCloud", RemoteName = ":http", ExtraFlags = { ["http-url"] = server.Url } });
        new JsonSettingsStore(paths.SettingsFile).Save(s);
        var host = AppHost.Create(paths);
        await host.InitializeAsync();
        return host;
    }

    private static async Task<(ExeResult R, long Bytes, long Requests, long Hits, long Misses, long Stalls)> LaunchAsync(AppHost host, SimCloudServer server, GameEntry g, int level, string results)
    {
        g.Arguments = $"--level {level} --autoexit --results \"{results}\"";
        host.Library.Save(g);
        server.ResetCounters();
        long h0 = host.Engine.Hits, m0 = host.Engine.Misses, s0 = host.Engine.StallCount;
        var before = File.Exists(results) ? File.ReadAllLines(results).Length : 0;
        var launch = await host.PlayAsync(g);
        await launch.Completion;
        await Task.Delay(1500); // let telemetry catch up
        var lines = File.ReadAllLines(results);
        if (lines.Length <= before) throw new InvalidOperationException("demo game wrote no result");
        return (Parse(lines[^1]), server.BytesSent, server.Requests, host.Engine.Hits - h0, host.Engine.Misses - m0, host.Engine.StallCount - s0);
    }

    private static (double CpuS, long PeakMb) RcloneUsage(AppHost host)
    {
        var s = host.Mounts.Get("sim");
        if (s?.ProcessId is not { } pid) return (0, 0);
        try
        {
            using var p = Process.GetProcessById(pid);
            return (p.TotalProcessorTime.TotalSeconds, p.PeakWorkingSet64 / ByteSize.MiB);
        }
        catch (ArgumentException) { return (0, 0); }
    }

    private static async Task<Row> CloudScenarioAsync(string work, SimCloudServer server, string results, string name, bool standardRclone, bool prediction,
        bool train, bool prepare, int level)
    {
        await using var host = await NewHostAsync(work, server, Slug(name), standardRclone, prediction);
        try
        {
            var g = await host.AddGameAsync("sim", "Games/CloudSSD Demo Game");
            g.Mode = ExecutionMode.CloudDirect;
            var x = await LaunchAsync(host, server, g, level, results);
            var (cpu, peak) = RcloneUsage(host);
            return new Row(name, x.R.Load, x.R.Startup, x.R.Menu, x.R.Level, x.R.Worst, x.Bytes, x.Requests, x.Hits, x.Misses, x.Stalls, cpu, peak, 0, 0,
                standardRclone ? "buffer-size 16M, chunk 128M" : "buffer-size 0, chunk 4M" + (prediction ? ", prediction on" : ""));
        }
        finally { await host.ShutdownAsync(false); }
    }

    private static async Task<(Row Prepared, Row Warm)> CloudScenarioWithWarmAsync(string work, SimCloudServer server, string results, int level)
    {
        await using var host = await NewHostAsync(work, server, "d-warm", standardRclone: false, prediction: true);
        try
        {
            var g = await host.AddGameAsync("sim", "Games/CloudSSD Demo Game");
            g.Mode = ExecutionMode.CloudDirect;
            await LaunchAsync(host, server, g, 0, results); // training launches
            await LaunchAsync(host, server, g, 1, results);
            await host.UnmountAsync("sim");
            var clear = CacheMaintenance.ClearMountCache(host.Paths.MountCacheDir(host.Settings, "sim"), host.Paths.CacheRootFor(host.Settings));
            if (!clear.Cleared) throw new InvalidOperationException(clear.Message);
            await host.MountAsync(host.Settings.Mounts[0]);
            server.ResetCounters();
            var sw = Stopwatch.StartNew();
            var report = await host.PrepareAsync(g, null, null, CancellationToken.None);
            var prepS = sw.Elapsed.TotalSeconds;
            var prepBytes = server.BytesSent;
            var x = await LaunchAsync(host, server, g, level, results);
            var (cpu, peak) = RcloneUsage(host);
            var d = new Row("D CloudSSD warm (2 profiled launches, cache cleared, Prepare, unseen level)", x.R.Load, x.R.Startup, x.R.Menu, x.R.Level, x.R.Worst,
                x.Bytes, x.Requests, x.Hits, x.Misses, x.Stalls, cpu, peak, prepS, prepBytes, $"readiness: {ReadinessText.Describe(report.Status)}");
            var y = await LaunchAsync(host, server, g, level, results);
            (cpu, peak) = RcloneUsage(host);
            var warm = new Row("D' CloudSSD fully warm cache (same level again)", y.R.Load, y.R.Startup, y.R.Menu, y.R.Level, y.R.Worst,
                y.Bytes, y.Requests, y.Hits, y.Misses, y.Stalls, cpu, peak, 0, 0, "everything already cached");
            return (d, warm);
        }
        finally { await host.ShutdownAsync(false); }
    }

    private static string Slug(string s) => new(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').Take(30).ToArray());
}
