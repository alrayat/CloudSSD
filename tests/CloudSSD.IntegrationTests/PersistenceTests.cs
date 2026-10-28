using System.Diagnostics;
using System.Text.Json;
using CloudSSD.App;
using CloudSSD.App.Persistence;
using CloudSSD.Core;
using CloudSSD.Games;
using CloudSSD.Mount;
using CloudSSD.SimCloud;
using Microsoft.Win32;
using Xunit.Abstractions;

namespace CloudSSD.IntegrationTests;

/// <summary>
/// The persistence tests kill processes, start a separate agent and use `subst` on a drive letter: they must never run
/// alongside other mount tests (a parallel run once let `subst /d` hit a letter another test's mount had taken).
/// </summary>
[CollectionDefinition("Persistence", DisableParallelization = true)]
public sealed class PersistenceCollection;

/// <summary>
/// "Keep Cloud SSD Permanent": the desktop app hands its drives to CloudSSD.Agent.exe when the window closes and takes
/// them back when it opens; the agent survives its own crash (supervisor) and rclone crashes, restores permanent drives
/// at logon, waits for the network and for a busy drive letter, and stops cleanly. Real rclone + WinFsp + agent process;
/// the cloud is SimCloud. A Windows restart itself is not simulated here (see TEST_REPORT.md for the manual test).
/// </summary>
[Collection("Persistence")]
public sealed class PersistenceTests(ITestOutputHelper output)
{
    private static string AgentExe => Path.Combine(AppContext.BaseDirectory, "CloudSSD.Agent.exe");

    private sealed class Env : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required AppPaths Paths { get; init; }
        public required SimCloudServer Server { get; init; }
        public required MountDefinition Def { get; init; }
        public string Drive => Def.DriveLetter + @":\";

        public async ValueTask DisposeAsync()
        {
            await AgentControlClient.StopAsync(Paths, TimeSpan.FromSeconds(90));
            await Server.DisposeAsync();
            TestEnv.Cleanup(Root);
        }
    }

    private static async Task<Env> CreateAsync(string name)
    {
        var root = Path.Combine(TestEnv.WorkRoot, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        var content = Path.Combine(root, "cloud");
        Directory.CreateDirectory(Path.Combine(content, "Game"));
        File.WriteAllText(Path.Combine(content, "hello.txt"), "permanent hello");
        var bytes = new byte[8 * ByteSize.MiB];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(content, "Game", "data.pak"), bytes);
        var server = new SimCloudServer(content);
        var paths = new AppPaths(Path.Combine(root, "home"));
        var letter = DriveLetterAllocator.FreeLetters().First(c => c is >= 'K' and <= 'P').ToString();
        var def = new MountDefinition
        {
            Id = "perm" + Guid.NewGuid().ToString("N")[..4], DisplayName = "Permanent test", RemoteName = ":http", DriveLetter = letter,
            Permanent = true, AutoMount = true, ReadOnly = true,
            ExtraFlags = { ["http-url"] = server.Url, ["low-level-retries"] = "2", ["contimeout"] = "3s", ["timeout"] = "10s" },
        };
        new JsonSettingsStore(paths.SettingsFile).Save(new AppSettings
        {
            FirstRunCompleted = true, EncryptRcloneConfig = false,
            RclonePathOverride = (await TestEnv.RcloneAsync()).Path,
            Cache = new CacheSettings { CacheRoot = Path.Combine(root, "cache"), BudgetBytes = ByteSize.GiB, MinFreeSpaceBytes = ByteSize.GiB },
            Network = new NetworkSettings { TpsLimit = 0 },
            Mounts = { def },
        });
        return new Env { Root = root, Paths = paths, Server = server, Def = def };
    }

    private static async Task<AgentStatus?> WaitForAgentAsync(AppPaths paths, Func<AgentStatus, bool> until, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        AgentStatus? last = null;
        while (sw.Elapsed < timeout)
        {
            last = (await AgentControlClient.StatusAsync(paths))?.Status ?? last;
            if (last != null && until(last)) return last;
            await Task.Delay(250);
        }
        return last;
    }

    private static int? RclonePid(AppPaths paths, string mountId)
    {
        if (!File.Exists(paths.MountStateFile)) return null;
        foreach (var e in JsonDocument.Parse(File.ReadAllText(paths.MountStateFile)).RootElement.EnumerateArray())
            if (e.GetProperty("Id").GetString() == mountId) return e.GetProperty("Pid").GetInt32();
        return null;
    }

    /// <summary>Polls the drive letter every 50 ms and records every interval in which it was missing.</summary>
    private sealed class Visibility : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        public readonly List<(DateTime From, TimeSpan Length, string Phase)> Gaps = new();
        public volatile string Phase = "";

        public Visibility(string root)
        {
            _loop = Task.Run(async () =>
            {
                DateTime? missingSince = null;
                var phase = "";
                while (!_cts.IsCancellationRequested)
                {
                    var ok = Directory.Exists(root);
                    if (!ok && missingSince is null) { missingSince = DateTime.Now; phase = Phase; }
                    if (ok && missingSince is { } m) { lock (Gaps) Gaps.Add((m, DateTime.Now - m, phase)); missingSince = null; }
                    await Task.Delay(50);
                }
                if (missingSince is { } still) lock (Gaps) Gaps.Add((still, DateTime.Now - still, phase + " (still missing)"));
            });
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _loop;
        }
    }

    [Fact]
    public async Task Stop_during_agent_initialization_unmounts_the_previously_detached_worker()
    {
        if (!TestEnv.WinFspAvailable) return;
        await using var env = await CreateAsync("perm-stop-init");
        int? native;
        await using (var ui = AppHost.Create(env.Paths))
        {
            Assert.True(ui.TryAcquireInstanceLock());
            await ui.InitializeAsync();
            native = ui.Mounts.Get(env.Def.Id)!.ProcessId;
            await ui.ShutdownAsync(keepMounts: true);
        }
        using var supervisor = AgentControlClient.Launch(env.Paths, AgentExe)!;
        Assert.True(await AgentControlClient.StopAsync(env.Paths, TimeSpan.FromSeconds(45)));
        Assert.True(supervisor.WaitForExit(10000));
        try
        {
            using var worker = System.Diagnostics.Process.GetProcessById(native!.Value);
            Assert.True(worker.HasExited || worker.WaitForExit(5000), "Stop must not leave an orphaned native mount.");
        }
        catch (ArgumentException) { } // already removed from the process table
        Assert.False(Directory.Exists(env.Drive));
    }

    [Fact]
    public async Task Starting_mount_is_persisted_and_adopted_without_a_second_cache_worker()
    {
        if (!TestEnv.WinFspAvailable) return;
        await using var env = await CreateAsync("perm-starting");
        env.Server.Latency = TimeSpan.FromSeconds(2);
        int? firstPid = null;
        await using (var ui = AppHost.Create(env.Paths))
        {
            Assert.True(ui.TryAcquireInstanceLock());
            await ui.InitializeAsync(restoreMounts: false);
            ui.Mounts.StatusChanged += status =>
            {
                if (status.MountId != env.Def.Id || status.State != MountState.Starting || firstPid != null || ui.Mounts.Get(env.Def.Id)?.ProcessId == null) return;
                using var json = JsonDocument.Parse(File.ReadAllText(env.Paths.MountStateFile));
                var entry = Assert.Single(json.RootElement.EnumerateArray());
                Assert.Equal("Starting", entry.GetProperty("State").GetString());
                firstPid = entry.GetProperty("Pid").GetInt32();
                ui.Mounts.DetachAll();
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ui.MountAsync(env.Def));
            Assert.NotNull(firstPid);
            await ui.ShutdownAsync(keepMounts: true);
        }
        env.Server.Latency = TimeSpan.Zero;
        await using var next = AppHost.Create(env.Paths);
        Assert.True(next.TryAcquireInstanceLock());
        await next.InitializeAsync();
        var adopted = next.Mounts.Get(env.Def.Id);
        Assert.NotNull(adopted);
        Assert.True(adopted!.Adopted);
        Assert.Equal(firstPid, adopted.ProcessId);
        var ready = DateTime.UtcNow.AddSeconds(20);
        while (adopted.State != MountState.Mounted && DateTime.UtcNow < ready) await Task.Delay(100);
        Assert.Equal(MountState.Mounted, adopted.State);
        Assert.Equal("permanent hello", await File.ReadAllTextAsync(Path.Combine(env.Drive, "hello.txt")));
        await next.ShutdownAsync(keepMounts: false);
    }

    [Fact]
    public async Task Window_close_hands_drive_to_agent_and_back_without_unmounting()
    {
        if (!TestEnv.WinFspAvailable) return;
        Assert.True(File.Exists(AgentExe), "CloudSSD.Agent.exe must be next to the tests");
        await using var env = await CreateAsync("perm-handoff");
        var file = Path.Combine(env.Drive, "hello.txt");

        // 1. The desktop app starts and restores the permanent drive.
        await using (var ui = AppHost.Create(env.Paths))
        {
            Assert.True(ui.TryAcquireInstanceLock());
            await ui.InitializeAsync();
            Assert.Equal(MountState.Mounted, ui.Mounts.Get(env.Def.Id)?.State);
            Assert.Equal("permanent hello", await File.ReadAllTextAsync(file));
            // 2. Window closes with the toggle on: detach (rclone keeps running), release the lock.
            await ui.ShutdownAsync(keepMounts: true);
        }
        await using var vis = new Visibility(env.Drive);
        vis.Phase = "hand-over to agent";
        using var supervisor = AgentControlClient.Launch(env.Paths, AgentExe)!;
        var st = await WaitForAgentAsync(env.Paths, s => s.Mounts.Any(m => m.Id == env.Def.Id && m.State == "Mounted"), TimeSpan.FromSeconds(30));
        Assert.NotNull(st);
        output.WriteLine($"Agent worker pid {st!.Pid} adopted {string.Join(", ", st.Mounts.Select(m => $"{m.Letter} {m.State}"))}");
        Assert.Equal("permanent hello", await File.ReadAllTextAsync(file));

        // 3. The worker crashes: the supervisor restarts it and the new worker re-attaches; the drive never goes away.
        vis.Phase = "agent worker crash";
        var firstPid = st.Pid;
        Process.GetProcessById(firstPid).Kill();
        st = await WaitForAgentAsync(env.Paths, s => s.Pid != firstPid && s.Mounts.Any(m => m.Id == env.Def.Id && m.State == "Mounted"), TimeSpan.FromSeconds(60));
        Assert.NotNull(st);
        Assert.NotEqual(firstPid, st!.Pid);
        output.WriteLine($"Worker {firstPid} killed; supervisor started {st.Pid}, which re-attached the drive");
        Assert.Equal("permanent hello", await File.ReadAllTextAsync(file));

        // 4. rclone crashes while only the agent is running: the agent restarts the mount on the same letter.
        vis.Phase = "rclone crash (gap expected)";
        var rclonePid = RclonePid(env.Paths, env.Def.Id);
        Assert.NotNull(rclonePid);
        Process.GetProcessById(rclonePid!.Value).Kill();
        var sw = Stopwatch.StartNew();
        await Task.Delay(1000);
        while (sw.Elapsed < TimeSpan.FromSeconds(60) && (RclonePid(env.Paths, env.Def.Id) is not { } p || p == rclonePid || !Directory.Exists(env.Drive)))
            await Task.Delay(250);
        Assert.NotEqual(rclonePid, RclonePid(env.Paths, env.Def.Id));
        output.WriteLine($"rclone {rclonePid} killed; drive back after {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal("permanent hello", await File.ReadAllTextAsync(file));

        // 5. The desktop app opens again: it asks the agent to release the drive and adopts it.
        vis.Phase = "hand-back to app";
        await using (var ui = AppHost.Create(env.Paths))
        {
            Assert.False(ui.TryAcquireInstanceLock());
            Assert.True(await AgentControlClient.ReleaseAsync(env.Paths, TimeSpan.FromSeconds(30)));
            Assert.True(await ui.TryAcquireInstanceLockAsync(TimeSpan.FromSeconds(10)));
            await ui.InitializeAsync();
            var s = ui.Mounts.Get(env.Def.Id);
            Assert.NotNull(s);
            Assert.True(s!.Adopted, "the app must re-attach to the running drive, not mount a new one");
            Assert.Equal("permanent hello", await File.ReadAllTextAsync(file));
            Assert.True(supervisor.WaitForExit(15_000), "the supervisor exits after a release");
            vis.Phase = "final unmount";
            await ui.ShutdownAsync(keepMounts: false);
        }
        await vis.DisposeAsync();
        foreach (var g in vis.Gaps) output.WriteLine($"Drive missing for {g.Length.TotalMilliseconds:0} ms during '{g.Phase}'");
        Assert.DoesNotContain(vis.Gaps, g => g.Phase is "hand-over to agent" or "agent worker crash" or "hand-back to app");
    }

    [Fact]
    public async Task Agent_at_logon_waits_for_letter_and_network_then_mounts_and_stops_cleanly()
    {
        if (!TestEnv.WinFspAvailable) return;
        await using var env = await CreateAsync("perm-logon");
        var letter = env.Def.DriveLetter!;
        var substTarget = Path.Combine(env.Root, "subst-target");
        Directory.CreateDirectory(substTarget);
        Environment.SetEnvironmentVariable("CLOUDSSD_AGENT_MAX_BACKOFF_S", "3");
        try
        {
            // Another drive holds the letter (subst: per-user, no admin) and the cloud is unreachable at logon.
            Assert.Equal(0, Run("subst", $"{letter}: \"{substTarget}\""));
            env.Server.Offline = true;
            using var supervisor = AgentControlClient.Launch(env.Paths, AgentExe, "--logon")!;
            var st = await WaitForAgentAsync(env.Paths, s => s.Waiting.Any(w => w.Contains($"{letter}: is used by another drive")), TimeSpan.FromSeconds(30));
            Assert.NotNull(st);
            output.WriteLine("Waiting: " + string.Join(" | ", st!.Waiting));
            Assert.Empty(st.Mounts); // it did not take another letter
            Assert.True(Directory.Exists(substTarget));

            // The other drive goes away; the cloud is still offline.
            Assert.Equal(substTarget, SubstTarget(letter), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(0, Run("subst", $"{letter}: /d"));
            st = await WaitForAgentAsync(env.Paths, s => s.Waiting.Count > 0 && !s.Waiting.Any(w => w.Contains("is used by another drive")) || s.Mounts.Count > 0,
                TimeSpan.FromSeconds(60));
            output.WriteLine("Offline cloud: mounts=" + string.Join(",", st?.Mounts.Select(m => m.State) ?? []) + " waiting=" + string.Join(" | ", st?.Waiting ?? []));

            // Connectivity returns: the drive appears on its own letter without any user action.
            env.Server.Offline = false;
            st = await WaitForAgentAsync(env.Paths, s => s.Mounts.Any(m => m.Id == env.Def.Id && m.State == "Mounted"), TimeSpan.FromSeconds(90));
            Assert.NotNull(st);
            Assert.Contains(st!.Mounts, m => m.Letter == letter + ":");
            Assert.Equal("permanent hello", await File.ReadAllTextAsync(Path.Combine(env.Drive, "hello.txt")));

            // A second agent for the same data folder exits immediately (no duplicate workers or mounts).
            using (var dup = AgentControlClient.Launch(env.Paths, AgentExe, "--logon")!)
                Assert.True(dup.WaitForExit(15_000));
            Assert.Single((await AgentControlClient.StatusAsync(env.Paths))!.Status!.Mounts);

            // Stop (as the uninstaller does): unmount and exit; cloud files untouched.
            Assert.True(await AgentControlClient.StopAsync(env.Paths, TimeSpan.FromSeconds(90)));
            Assert.True(supervisor.WaitForExit(15_000));
            Assert.False(Directory.Exists(env.Drive));
            Assert.True(File.Exists(Path.Combine(env.Root, "cloud", "hello.txt")));
        }
        finally
        {
            if (SubstTarget(letter) != null) Run("subst", $"{letter}: /d"); // only our own mapping, never a mounted drive
            Environment.SetEnvironmentVariable("CLOUDSSD_AGENT_MAX_BACKOFF_S", null);
        }
    }

    [Fact]
    public async Task Toggle_off_unmounts_only_when_the_drive_is_idle()
    {
        if (!TestEnv.WinFspAvailable) return;
        await using var env = await CreateAsync("perm-off");
        var oldWindow = AppHost.IdleWindow;
        AppHost.IdleWindow = TimeSpan.FromSeconds(4);
        try
        {
            await using var ui = AppHost.Create(env.Paths);
            Assert.True(ui.TryAcquireInstanceLock());
            await ui.InitializeAsync();
            Assert.NotNull(ui.Mounts.Get(env.Def.Id));
            _ = Bytes.HashFile(Path.Combine(env.Drive, "Game", "data.pak"));
            await Task.Delay(500); // telemetry tailer
            var reason = await ui.BusyReasonAsync(env.Def.Id);
            output.WriteLine($"Busy: {reason}");
            Assert.NotNull(reason);

            var reports = new List<string>();
            var sw = Stopwatch.StartNew();
            await ui.UnmountWhenIdleAsync(env.Def.Id, new SyncProgress(reports.Add), CancellationToken.None);
            output.WriteLine($"Unmounted after {sw.Elapsed.TotalSeconds:0.0} s; pending reports: {string.Join(" | ", reports.Distinct())}");
            Assert.Contains(reports, r => r.StartsWith("Unmount pending"));
            Assert.Null(ui.Mounts.Get(env.Def.Id));
            Assert.False(Directory.Exists(env.Drive));
            await ui.ShutdownAsync(keepMounts: false);
        }
        finally { AppHost.IdleWindow = oldWindow; }
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>Target of a subst mapping on this letter, or null if the letter is not a subst drive.</summary>
    private static string? SubstTarget(string letter)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c subst") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        var prefix = $"{letter.ToUpperInvariant()}:\\: => ";
        return output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }

    private static int Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {exe} {args}") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(15_000);
        return p.ExitCode;
    }

    [Fact]
    public async Task Interrupted_cache_job_resumes_after_owner_restart_and_verifies_cached_bytes()
    {
        if (!TestEnv.WinFspAvailable) return;
        await using var env = await CreateAsync("job-recovery");
        var game = new GameEntry { Id = "resume-demo", Name = "Recovery demo", MountId = env.Def.Id, RootPath = "Game" };
        new GameLibrary(env.Paths.DatabaseFile).Save(game);
        new TransferJournal(env.Paths.DatabaseFile).Save(new TransferJob("FullCache:" + game.Id, game.Id, TransferKind.FullCache, null,
            TransferState.Running, 1024, 8 * ByteSize.MiB, "Stopped owner", DateTime.UtcNow.AddMinutes(-1)));
        await using var host = AppHost.Create(env.Paths);
        Assert.True(host.TryAcquireInstanceLock());
        await host.InitializeAsync();
        try
        {
            var until = DateTime.UtcNow.AddSeconds(100);
            while (DateTime.UtcNow < until && host.Transfers.All().Single().State != TransferState.Completed) await Task.Delay(250);
            var job = Assert.Single(host.Transfers.All());
            Assert.Equal(TransferState.Completed, job.State);
            Assert.Equal(8 * ByteSize.MiB, job.Bytes);
            Assert.Equal(ReadinessStatus.ReadyToLaunch, host.Library.Get(game.Id)!.Readiness);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(env.Root, "cloud", "Game", "data.pak")),
                await File.ReadAllBytesAsync(Path.Combine(env.Drive, "Game", "data.pak")));
        }
        finally { await host.ShutdownAsync(false); }
    }

    [Fact]
    public async Task Desktop_watcher_starts_supervision_when_its_parent_crashes()
    {
        if (!TestEnv.WinFspAvailable) return;
        await using var env = await CreateAsync("desktop-crash");
        var psi = new ProcessStartInfo("ping.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-n", "120", "127.0.0.1" }) psi.ArgumentList.Add(arg);
        using var desktop = Process.Start(psi)!;
        using var watcher = AgentControlClient.Launch(env.Paths, AgentExe, "--watch-desktop", desktop.Id.ToString(),
            "--started-ticks", desktop.StartTime.ToUniversalTime().Ticks.ToString())!;
        await Task.Delay(1000);
        Assert.False(watcher.HasExited);
        desktop.Kill();
        var status = await WaitForAgentAsync(env.Paths, s => s.Mounts.Any(m => m.State == "Mounted"), TimeSpan.FromSeconds(45));
        Assert.NotNull(status);
        Assert.Contains(status!.Mounts, m => m.Letter == env.Def.DriveLetter + ":");
        Assert.Equal("permanent hello", await File.ReadAllTextAsync(Path.Combine(env.Drive, "hello.txt")));
        Assert.True(await AgentControlClient.StopAsync(env.Paths, TimeSpan.FromSeconds(45)));
        Assert.True(watcher.WaitForExit(15000));
    }
}

/// <summary>Logon registration in a private HKCU key, so the user's real Run list is never touched.</summary>
public sealed class StartupRegistrationTests
{
    [Fact]
    public void RegistrationPreservesDisabledStartupChoice()
    {
        var key = @"Software\CloudSSD.Tests\startup-" + Guid.NewGuid().ToString("N");
        var reg = new StartupRegistration(key + @"\Run", key + @"\Approved");
        try
        {
            reg.Register("\"C:\\Apps\\CloudSSD\\CloudSSD.Agent.exe\" --logon");
            using (var approved = Registry.CurrentUser.CreateSubKey(key + @"\Approved"))
                approved.SetValue(StartupRegistration.ValueName, new byte[] { 3, 0, 0, 0 }, RegistryValueKind.Binary);
            reg.Register("\"C:\\Apps\\CloudSSD\\CloudSSD.Agent.exe\" --logon");
            Assert.True(reg.DisabledByUser());
            Assert.True(reg.HasApprovalMark());
            reg.Unregister();
            Assert.Null(reg.RegisteredCommand());
            Assert.False(reg.HasApprovalMark());
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
    }
    [Fact]
    public void Upgrade_repoints_only_existing_startup_and_preserves_profile_and_disabled_choice()
    {
        var key = @"Software\CloudSSD.Tests\upgrade-" + Guid.NewGuid().ToString("N");
        var reg = new StartupRegistration(key + @"\Run", key + @"\Approved");
        try
        {
            Assert.False(reg.RepointExistingAgent(@"C:\Updated App\CloudSSD.Agent.exe"));
            Assert.Null(reg.RegisteredCommand());
            reg.Register("\"C:\\Current App\\CloudSSD.Agent.exe\" --logon --home \"D:\\My profile\"");
            using (var approved = Registry.CurrentUser.CreateSubKey(key + @"\Approved"))
                approved.SetValue(StartupRegistration.ValueName, new byte[] { 3, 0, 0, 0 }, RegistryValueKind.Binary);
            Assert.True(reg.RepointExistingAgent(@"C:\Updated App\CloudSSD.Agent.exe"));
            Assert.Equal("\"C:\\Updated App\\CloudSSD.Agent.exe\" --logon --home \"D:\\My profile\"", reg.RegisteredCommand());
            Assert.True(reg.DisabledByUser());
            reg.Register("\"C:\\Other\\something.exe\" --logon");
            Assert.False(reg.RepointExistingAgent(@"C:\Updated App\CloudSSD.Agent.exe"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Register_state_disabled_and_unregister()
    {
        var key = @"Software\CloudSSD-Test\" + Guid.NewGuid().ToString("N")[..8];
        var reg = new StartupRegistration(key + @"\Run", key + @"\Approved");
        try
        {
            var paths = new AppPaths(Path.Combine(TestEnv.WorkRoot, "reg-home"));
            var cmd = StartupRegistration.CommandFor(@"C:\Apps\CloudSSD\CloudSSD.Agent.exe", paths);
            Assert.StartsWith("\"C:\\Apps\\CloudSSD\\CloudSSD.Agent.exe\" --logon", cmd);
            Assert.Contains("--home", cmd); // non-default data folder is passed explicitly
            Assert.Equal(StartupState.NotRegistered, reg.State(cmd));
            reg.Register(cmd);
            Assert.Equal(StartupState.Registered, reg.State(cmd));
            Assert.True(reg.HasApprovalMark(), "registering records the entry as enabled in Startup apps");
            using (var k = Registry.CurrentUser.OpenSubKey(key + @"\Approved"))
                Assert.Equal(2, ((byte[])k!.GetValue(StartupRegistration.ValueName)!)[0]);
            Assert.Equal(StartupState.PointsElsewhere, reg.State(cmd.Replace("Apps", "Other")));
            using (var k = Registry.CurrentUser.CreateSubKey(key + @"\Approved"))
                k.SetValue(StartupRegistration.ValueName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Assert.Equal(StartupState.DisabledInWindows, reg.State(cmd));
            reg.EnsureApproved(); // must not override the user's "disabled"
            reg.Register(cmd);
            Assert.Equal(StartupState.DisabledInWindows, reg.State(cmd));
            reg.Unregister();
            Assert.False(reg.HasApprovalMark());
            Assert.Equal(StartupState.NotRegistered, reg.State(cmd));
            reg.Unregister(); // idempotent
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
    }
}
