using CloudSSD.Games;
using System.Text.Json;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.SimCloud;
using Xunit.Abstractions;

namespace CloudSSD.IntegrationTests;

/// <summary>
/// The desktop app's TeraBox path end to end: AppHost + TeraBoxService (device-code login, encrypted tokens, rclone
/// "terabox" remote), the bridge as a separate "cloudssd-cli terabox-bridge" process, a real T: mount, an app restart with the
/// drive kept mounted (bridge and mount adopted), and a clean shutdown. TeraBox itself is the documented-API mock.
/// </summary>
public sealed class TeraBoxAppFlowTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Connect_mount_restart_adopt_and_shutdown()
    {
        var root = Path.Combine(TestEnv.WorkRoot, "tb-app-" + Guid.NewGuid().ToString("N")[..6]);
        await using var mock = new TeraBoxMockServer(Path.Combine(root, "cloud"), Path.Combine(root, "state")) { QuotaTotal = 1024L * ByteSize.GiB };
        var home = Path.Combine(root, "home");
        var paths = new AppPaths(home);
        new JsonSettingsStore(paths.SettingsFile).Save(new AppSettings
        {
            FirstRunCompleted = true, EncryptRcloneConfig = true,
            Cache = new CacheSettings { CacheRoot = Path.Combine(root, "cache"), BudgetBytes = 2 * ByteSize.GiB, MinFreeSpaceBytes = 2 * ByteSize.GiB, WriteBack = TimeSpan.FromSeconds(1) },
            Network = new NetworkSettings { TpsLimit = 0 },
            TeraBox = new TeraBoxSettings { MinWriteBackSeconds = 1 },
        });
        var keyFile = Path.Combine(root, "terabox-client.json");
        File.WriteAllText(keyFile, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["client_id"] = mock.ClientId, ["client_secret"] = mock.ClientSecret, ["private_secret"] = mock.PrivateSecret, ["auth_base_url"] = mock.BaseUrl
        }));

        MountDefinition def;
        await using (var host = AppHost.Create(paths))
        {
            await host.InitializeAsync();
            Assert.True(File.Exists(host.CliPath), "cloudssd-cli.exe must be next to the app (bridge host)");
            host.TeraBox.ImportClientFile(keyFile);
            var user = await host.TeraBox.FinishLoginAsync(await host.TeraBox.StartLoginAsync(default), default);
            output.WriteLine($"Logged in: {user.Name} ({user.Plan}), bridge port {host.Settings.TeraBox.BridgePort}");
            await host.RcloneConfig.EnsureEncryptedAsync(host.Rclone!, host.CliPath);
            await host.RefreshRemotesAsync();
            Assert.Contains(host.Remotes, r => r.Name == "terabox" && r.Type == "webdav");

            def = new MountDefinition { DisplayName = "TeraBox Cloud SSD", RemoteName = TeraBoxService.RemoteName, DriveLetter = null, AutoMount = true };
            host.Settings.Mounts.Add(def);
            host.SaveSettings();
            var s = await host.MountAsync(def);
            output.WriteLine($"Mounted at {s.Root}");
            await File.WriteAllTextAsync(Path.Combine(s.Root, "hello.txt"), "from T:");
            var cloud = mock.DiskPath("/From: Other Applications/CloudSSD/hello.txt");
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(cloud) && DateTime.UtcNow < until) await Task.Delay(250);
            Assert.Equal("from T:", File.ReadAllText(cloud));
            Assert.Equal(mock.QuotaTotal, new DriveInfo(s.Root).TotalSize);
            var status = await host.TeraBox.StatusAsync();
            output.WriteLine($"Bridge status: plan {status?.Plan}, uploads {status?.Uploads}, quota {status?.QuotaTotal}");
            Assert.NotNull(status);
            Assert.Equal(1, status!.Uploads);

            await host.ShutdownAsync(keepMounts: true); // drive and bridge stay up
        }

        await using (var host = AppHost.Create(paths))
        {
            await host.InitializeAsync();
            var s = host.Mounts.Get(def.Id);
            Assert.NotNull(s);
            output.WriteLine($"After restart: adopted mount {s!.Root}, bridge healthy: {await host.TeraBox.StatusAsync() != null}");
            Assert.Equal("from T:", await File.ReadAllTextAsync(Path.Combine(s.Root, "hello.txt")));

            // The bridge dies under a mounted drive: the watchdog starts it again and writes reach the cloud.
            await host.TeraBox.StopBridgeAsync();
            Assert.False(await host.TeraBox.IsBridgeHealthyAsync(default));
            await host.KeepBridgeAliveAsync();
            Assert.True(await host.TeraBox.IsBridgeHealthyAsync(default));
            await File.WriteAllTextAsync(Path.Combine(s.Root, "after-restart.txt"), "bridge is back");
            var cloud2 = mock.DiskPath("/From: Other Applications/CloudSSD/after-restart.txt");
            var until2 = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(cloud2) && DateTime.UtcNow < until2) await Task.Delay(250);
            Assert.Equal("bridge is back", File.ReadAllText(cloud2));

            await host.ShutdownAsync(keepMounts: false);
            Assert.Null(await host.TeraBox.StatusAsync()); // bridge process stopped with the last TeraBox drive
        }
        await mock.DisposeAsync();
        TestEnv.Cleanup(root);
    }
    [Fact]
    public async Task Import_existing_game_into_TeraBox_verify_resume_and_play_from_T()
    {
        var demo = Path.Combine(TestEnv.WorkRoot, "dataset", "Games", "CloudSSD Demo Game");
        Assert.True(File.Exists(Path.Combine(demo, "CloudSSD.DemoGame.exe")), "demo game must be installed in the dataset (see TEST_REPORT.md)");
        var root = Path.Combine(TestEnv.WorkRoot, "tb-import-" + Guid.NewGuid().ToString("N")[..6]);
        await using var mock = new TeraBoxMockServer(Path.Combine(root, "cloud"), Path.Combine(root, "state")) { QuotaTotal = 1024L * ByteSize.GiB };
        var paths = new AppPaths(Path.Combine(root, "home"));
        new JsonSettingsStore(paths.SettingsFile).Save(new AppSettings
        {
            FirstRunCompleted = true,
            Cache = new CacheSettings { CacheRoot = Path.Combine(root, "cache"), BudgetBytes = 2 * ByteSize.GiB, MinFreeSpaceBytes = 2 * ByteSize.GiB },
            Network = new NetworkSettings { TpsLimit = 0 },
            TeraBox = new TeraBoxSettings { MinWriteBackSeconds = 1 },
        });
        var keyFile = Path.Combine(root, "terabox-client.json");
        File.WriteAllText(keyFile, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["client_id"] = mock.ClientId, ["client_secret"] = mock.ClientSecret, ["private_secret"] = mock.PrivateSecret, ["auth_base_url"] = mock.BaseUrl
        }));
        await using var host = AppHost.Create(paths);
        await host.InitializeAsync();
        try
        {
            host.TeraBox.ImportClientFile(keyFile);
            await host.TeraBox.FinishLoginAsync(await host.TeraBox.StartLoginAsync(default), default);
            await host.RefreshRemotesAsync();
            var def = new MountDefinition { DisplayName = "TeraBox Cloud SSD", RemoteName = TeraBoxService.RemoteName, DriveLetter = "T" };
            host.Settings.Mounts.Add(def);
            await host.MountAsync(def);

            // A plan limit smaller than the game's archives blocks the import instead of splitting files.
            Assert.False(GameImporter.Plan(demo, "Games/x", 1024 * 1024, null, "test limit").CanStart);

            var plan = await host.PlanImportAsync(demo, def, "Games/CloudSSD Demo Game");
            output.WriteLine($"Plan: {plan.Files.Count} files, {ByteSize.Format(plan.TotalBytes)}, blockers [{string.Join("; ", plan.Blockers)}], warnings [{string.Join("; ", plan.Warnings)}]");
            Assert.True(plan.CanStart);
            var stages = new HashSet<ImportStage>();
            var (result, game) = await host.ImportGameAsync(plan, def, fullVerify: true, new Progress<ImportProgress>(p => stages.Add(p.Stage)), CancellationToken.None);
            output.WriteLine($"Import: verified={result.Verified} ({result.VerificationMethod}) in {result.Elapsed.TotalSeconds:0.0} s - {result.Message}");
            Assert.True(result.Verified, result.Message);
            Assert.True(File.Exists(mock.DiskPath("/From: Other Applications/CloudSSD/Games/CloudSSD Demo Game/" + GameImporter.ManifestName)));
            foreach (var file in Directory.EnumerateFiles(demo, "*", SearchOption.AllDirectories))
                Assert.Equal(Bytes.HashFile(file), Bytes.HashFile(mock.DiskPath("/From: Other Applications/CloudSSD/Games/CloudSSD Demo Game/" + Path.GetRelativePath(demo, file).Replace('\\', '/'))));
            Assert.True(File.Exists(Path.Combine(demo, "CloudSSD.DemoGame.exe")), "the local source must be left untouched");
            Assert.NotNull(game);
            Assert.Equal("CloudSSD.DemoGame.exe", game!.Executable);

            // Re-running resumes: everything is already there, so nothing is uploaded again.
            var callsBefore = mock.ApiCalls;
            var again = await host.ImportGameAsync(await host.PlanImportAsync(demo, def, "Games/CloudSSD Demo Game"), def, false, null, CancellationToken.None);
            output.WriteLine($"Re-import (resume) of the same folder: verified={again.Result.Verified} in {again.Result.Elapsed.TotalSeconds:0.0} s, {mock.ApiCalls - callsBefore} API calls");
            Assert.True(again.Result.Verified, again.Result.Message);
            Assert.True(again.Result.Elapsed < result.Elapsed, "a resumed import must skip files that are already complete");

            // Play the imported game from T: through the launcher (cold cache).
            var results = Path.Combine(root, "demo.jsonl");
            game.Arguments = $"--level 0 --autoexit --results \"{results}\"";
            game.Mode = ExecutionMode.CloudDirect;
            host.Library.Save(game);
            var launch = await host.PlayAsync(game);
            var diag = await launch.Completion.WaitAsync(TimeSpan.FromMinutes(5));
            var r = JsonDocument.Parse(File.ReadAllLines(results)[^1]).RootElement;
            output.WriteLine($"Played imported game from T:: exit {diag.ExitCode}, load {r.GetProperty("loadMs").GetDouble():0} ms, root {r.GetProperty("root")}");
            Assert.False(diag.LikelyFailure);
            Assert.StartsWith(host.Mounts.Get(def.Id)!.Root, r.GetProperty("root").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await host.ShutdownAsync(keepMounts: false);
            await host.DisposeAsync();
            await mock.DisposeAsync();
            TestEnv.Cleanup(root);
        }
    }
}