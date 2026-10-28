using System.Text.Json;
using CloudSSD.App;
using CloudSSD.Cache;
using CloudSSD.Core;
using CloudSSD.Games;
using CloudSSD.SimCloud;
using Xunit.Abstractions;

namespace CloudSSD.IntegrationTests;

/// <summary>
/// End-to-end: the real AppHost (as used by the desktop UI) mounts a SimCloud drive (70 Mbps, 80 ms), adds the DRM-free
/// CLOUDSSD demo game, launches it from the cloud drive, learns its profile, prepares it, launches it again and finally
/// runs it in Hybrid mode. Load times are measured by the demo game itself.
/// </summary>
public sealed class GameFlowTests(ITestOutputHelper output)
{
    private const string GameFolder = "Games/CloudSSD Demo Game";

    private static string DemoGameRoot => Path.Combine(TestEnv.WorkRoot, "dataset", "Games", "CloudSSD Demo Game");

    private static List<JsonElement> Results(string file) =>
        File.Exists(file) ? File.ReadAllLines(file).Select(l => JsonDocument.Parse(l).RootElement).ToList() : [];

    private async Task<(LaunchDiagnostics Diag, JsonElement Result)> LaunchAsync(AppHost host, GameEntry g, int level, string results)
    {
        g.Arguments = $"--level {level} --autoexit --results \"{results}\"";
        host.Library.Save(g);
        var before = Results(results).Count;
        var launch = await host.PlayAsync(g);
        var diag = await launch.Completion.WaitAsync(TimeSpan.FromMinutes(5));
        await Task.Delay(500); // OnLaunchEnded runs on a continuation
        var all = Results(results);
        Assert.True(all.Count > before, "the demo game should have written a result");
        return (diag, all[^1]);
    }

    [Fact]
    public async Task Demo_game_launches_from_cloud_learns_profile_prepares_and_runs_hybrid()
    {
        Assert.True(File.Exists(Path.Combine(DemoGameRoot, "CloudSSD.DemoGame.exe")), "run the demo game install step first (see TEST_REPORT.md)");
        var home = Path.Combine(TestEnv.WorkRoot, "home-gameflow-" + Guid.NewGuid().ToString("N")[..6]);
        var results = Path.Combine(home, "demo-results.jsonl");
        await using var server = new SimCloudServer(Path.Combine(TestEnv.WorkRoot, "dataset")) { BandwidthMbps = 70, Latency = TimeSpan.FromMilliseconds(80) };
        var paths = new AppPaths(home);
        var settings = new AppSettings
        {
            FirstRunCompleted = true,
            Cache = new CacheSettings { CacheRoot = Path.Combine(home, "cache"), BudgetBytes = 2 * ByteSize.GiB, MinFreeSpaceBytes = 2 * ByteSize.GiB },
            Network = new NetworkSettings { DeclaredDownloadMbps = 70, TpsLimit = 0 },
            LocalGamesRoot = Path.Combine(home, "local-games"),
        };
        settings.Mounts.Add(new MountDefinition { Id = "sim", DisplayName = "SimCloud", RemoteName = ":http", ExtraFlags = { ["http-url"] = server.Url } });
        new JsonSettingsStore(paths.SettingsFile).Save(settings);

        await using var host = AppHost.Create(paths);
        await host.InitializeAsync();
        try { await RunFlowAsync(host, server, results); }
        finally { await host.ShutdownAsync(keepMounts: false); }
    }

    private async Task RunFlowAsync(AppHost host, SimCloudServer server, string results)
    {
        var mount = host.Mounts.Get("sim");
        Assert.NotNull(mount);
        Assert.Equal(CloudSSD.Core.MountState.Mounted, mount!.State);

        // Add and analyze.
        var game = await host.AddGameAsync("sim", GameFolder);
        output.WriteLine($"Added: exe={game.Executable}, compat={game.Compatibility?.Level}, mode={game.Mode}, recommended={game.Compatibility?.RecommendedMode}");
        Assert.Equal("CloudSSD.DemoGame.exe", game.Executable);
        Assert.Equal(CompatibilityLevel.NoBlockersFound, game.Compatibility!.Level);
        game.Mode = ExecutionMode.CloudDirect;

        // 1. Cold launch straight from the cloud drive (no profile, empty cache).
        server.ResetCounters();
        var (d1, r1) = await LaunchAsync(host, game, 0, results);
        var cold = r1.GetProperty("loadMs").GetDouble();
        output.WriteLine($"Launch 1 (cold, Cloud Direct): load {cold:0} ms, exit {d1.ExitCode}, cloud bytes {ByteSize.Format(server.BytesSent)}, root {r1.GetProperty("root")}");
        Assert.False(d1.LikelyFailure);
        Assert.StartsWith(mount.Root, r1.GetProperty("root").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, host.Profiles.Summary(game.Id, TimeSpan.FromMinutes(2)).Sessions);

        // 2. Second launch, different level, warm cache for shared data.
        server.ResetCounters();
        var (_, r2) = await LaunchAsync(host, game, 1, results);
        output.WriteLine($"Launch 2 (warm shared data, new level): load {r2.GetProperty("loadMs").GetDouble():0} ms, cloud bytes {ByteSize.Format(server.BytesSent)}");

        // 3. Clear the cache completely, then Optimize & Prepare from the learned profile.
        await host.UnmountAsync("sim");
        var clear = CacheMaintenance.ClearMountCache(host.Paths.MountCacheDir(host.Settings, "sim"), host.Paths.CacheRootFor(host.Settings));
        Assert.True(clear.Cleared, clear.Message);
        await host.MountAsync(host.Settings.Mounts[0]);
        server.ResetCounters();
        var report = await host.PrepareAsync(game, null, null, CancellationToken.None);
        foreach (var s in report.Steps) output.WriteLine($"  [{s.State}] {s.Name}: {s.Detail}");
        output.WriteLine($"Prepare: {report.Status} — {report.Headline} (downloaded {ByteSize.Format(server.BytesSent)})");
        Assert.Contains(report.Status, new[] { ReadinessStatus.ReadyToLaunch, ReadinessStatus.MayRequireAdditionalDownloads });
        Assert.NotNull(report.Prefetch);
        Assert.True(report.Prefetch!.FetchedBlocks > 0);

        // 4. Launch after preparation (level 2 was never seen; startup + menu were learned).
        server.ResetCounters();
        var (_, r3) = await LaunchAsync(host, game, 2, results);
        var prepared = r3.GetProperty("loadMs").GetDouble();
        output.WriteLine($"Launch 3 (after Prepare, unseen level): load {prepared:0} ms, cloud bytes during launch {ByteSize.Format(server.BytesSent)}");
        var phases = r3.GetProperty("phases");
        output.WriteLine($"  phases: startup {phases.GetProperty("startup").GetDouble():0} ms, menu {phases.GetProperty("menu").GetDouble():0} ms, level {phases.GetProperty("level").GetDouble():0} ms");
        var coldPhases = r1.GetProperty("phases");
        Assert.True(phases.GetProperty("startup").GetDouble() + phases.GetProperty("menu").GetDouble()
                    < (coldPhases.GetProperty("startup").GetDouble() + coldPhases.GetProperty("menu").GetDouble()) * 0.5,
            "learned startup/menu data should load much faster after Prepare");

        // 5. Hybrid Optimized: essentials copied locally, archives streamed through symbolic links.
        game.Mode = ExecutionMode.HybridOptimized;
        var hybrid = await host.PrepareAsync(game, null, null, CancellationToken.None);
        output.WriteLine($"Hybrid prepare: {hybrid.Status} — local root {game.LocalRoot}");
        Assert.NotNull(game.LocalRoot);
        Assert.True(File.Exists(Path.Combine(game.LocalRoot!, "CloudSSD.DemoGame.exe")));
        Assert.NotNull(new FileInfo(Path.Combine(game.LocalRoot!, "data", "archive00.pak")).LinkTarget);
        var (d4, r4) = await LaunchAsync(host, game, 0, results);
        output.WriteLine($"Launch 4 (Hybrid): load {r4.GetProperty("loadMs").GetDouble():0} ms, root {r4.GetProperty("root")}, exit {d4.ExitCode}");
        Assert.StartsWith(game.LocalRoot!, r4.GetProperty("root").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(d4.LikelyFailure);

        var final = host.Library.Get(game.Id)!;
        output.WriteLine($"Last result: {final.LastLaunchResult}");
        output.WriteLine($"Verified modes: {string.Join(",", final.VerifiedModes)} (requires ≥ 60 s runtime; the demo exits after loading, so none is expected)");
        await host.ShutdownAsync(keepMounts: false);
    }
}
