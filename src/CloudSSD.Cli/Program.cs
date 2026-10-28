using Microsoft.Extensions.Logging;
using CloudSSD.App;
using CloudSSD.App.Persistence;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using CloudSSD.Games;
using CloudSSD.Mount;
using CloudSSD.SimCloud;

namespace CloudSSD.Cli;

public static class Program
{
    private const string Usage = """
        cloudssd-cli - CloudSSD command line

          cloudssd-cli deps                                  Check WinFsp, rclone, cache volume and other prerequisites
          cloudssd-cli remotes                               List configured cloud accounts (rclone remotes)
          cloudssd-cli mounts                                List configured drives
          cloudssd-cli mount <mount-id> [--letter X]         Mount a configured drive in the foreground (Ctrl+C to unmount)
          cloudssd-cli import <game folder> [--drive T] [--folder Games/Name] [--full-verify]
                                                    Upload an installed game to a cloud drive (resumable), verify, add to library
          cloudssd-cli agent status|start|stop|release       Background agent that keeps "Keep Cloud SSD Permanent" drives mounted
                                                    (stop = unmount all drives gracefully; release = hand drives to the app)
          cloudssd-cli scan <folder> [--mbps 70]             Scan a game folder and print the compatibility report
          cloudssd-cli pe <file.exe>                         Show PE architecture and imported DLLs (+ missing dependencies)
          cloudssd-cli profile <game-id>                     Show the learned access profile of a game
          cloudssd-cli simcloud --root <dir> [--port 8080] [--mbps 70] [--latency-ms 150]
                                                    Serve a folder as a throttled local test "cloud" (127.0.0.1 only)
          cloudssd-cli secret emit-config-pass [--home dir]  (used by rclone --password-command; prints the DPAPI-protected key)
          cloudssd-cli terabox-bridge --port <p> [--home dir] (started by the app) Serve the TeraBox WebDAV bridge on 127.0.0.1
          cloudssd-cli terabox-mock --data <dir> [--port p]   Run the TeraBox Open Platform test mock (no real account involved)
          cloudssd-cli terabox-probe [--pagination] [--reads N] [--out dir] [--throughput [--size-mib 64]]
                                                    Measure the connected TeraBox account (ranges, errors, speed) in a test folder

        Options: --home <dir> overrides the data folder (default %LOCALAPPDATA%\CloudSSD).
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        var home = Opt(args, "--home");
        var paths = new AppPaths(home);
        try
        {
            switch (args[0])
            {
                case "secret" when args.Length > 1 && args[1] == "emit-config-pass":
                {
                    var pass = SecretStore.Load(paths.ConfigPasswordFile);
                    if (pass is null)
                    {
                        Console.Error.WriteLine("No configuration key found for this Windows user.");
                        return 1;
                    }
                    Console.Out.Write(pass);
                    return 0;
                }
                case "repair-paused":
                {
                    // Puts paused Steam writes back into a drive's cache (merging them into newer writes of the same file).
                    // Only while no drive is running: rclone must not have the cache open.
                    if (System.Diagnostics.Process.GetProcessesByName("rclone").Length > 0)
                    {
                        Console.Error.WriteLine("A drive is running (rclone). Stop it first.");
                        return 2;
                    }
                    var settings = new JsonSettingsStore(paths.SettingsFile).Load();
                    foreach (var m in settings.Mounts.Where(m => Opt(args, "--mount") is not { } id || m.Id == id))
                    {
                        var paused = new PausedSteamWrites(paths.MountCacheDir(settings, m.Id));
                        var before = paused.All().Count;
                        if (before == 0) continue;
                        paused.Restore();
                        Console.WriteLine($"{m.DisplayName}: {before} paused Steam write(s) restored; {paused.All().Count} still paused.");
                    }
                    return 0;
                }
                case "deps":
                {
                    var rclone = await RcloneLocator.LocateAsync(null, paths.DataRoot);
                    var settings = new JsonSettingsStore(paths.SettingsFile).Load();
                    foreach (var d in DependencyChecker.CheckAll(rclone?.Path, rclone?.Version, paths.CacheRootFor(settings), settings.Cache.MinFreeSpaceBytes))
                        Console.WriteLine($"[{d.State,-7}] {d.Name}: {d.Detail}{(d.Remedy != null ? $"\n           → {d.Remedy}" : "")}");
                    return 0;
                }
                case "remotes":
                {
                    await using var host = AppHost.Create(paths);
                    // Read-only: without the data-folder lock this must not adopt, restart or clean up running drives.
                    await host.InitializeAsync(restoreMounts: false, adoptMounts: false);
                    foreach (var r in host.Remotes) Console.WriteLine($"{r.Name,-20} {r.Type}");
                    if (host.Remotes.Count == 0) Console.WriteLine("No cloud accounts configured. Connect Google Drive in the CloudSSD app.");
                    return 0;
                }
                case "mounts":
                {
                    var s = new JsonSettingsStore(paths.SettingsFile).Load();
                    foreach (var m in s.Mounts) Console.WriteLine($"{m.Id}  {m.DisplayName,-20} {m.RemoteSpec,-30} letter={m.DriveLetter ?? "auto"} auto={m.AutoMount}");
                    return 0;
                }
                case "mount" when args.Length > 1:
                {
                    await using var host = AppHost.Create(paths);
                    if (!host.TryAcquireInstanceLock())
                    {
                        Console.Error.WriteLine("Another CloudSSD instance (the app or its background agent) is running with this data folder." +
                                                (await AgentControlClient.StatusAsync(paths) != null ? " Stop the agent first: cloudssd-cli agent stop" : ""));
                        return 2;
                    }
                    await host.InitializeAsync(restoreMounts: false);
                    var def = host.Settings.Mounts.FirstOrDefault(m => m.Id == args[1] || m.DisplayName == args[1])
                              ?? throw new ArgumentException($"No configured drive '{args[1]}'. See 'cloudssd-cli mounts'.");
                    if (Opt(args, "--letter") is { } l) def.DriveLetter = l;
                    var s = await host.MountAsync(def);
                    Console.WriteLine($"Mounted {def.RemoteSpec} at {s.DriveLetter}:. Press Ctrl+C to unmount.");
                    var done = new TaskCompletionSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
                    await done.Task;
                    // Unmount only the drive this command mounted; drives that were already running stay as they were.
                    await host.UnmountAsync(def.Id);
                    await host.ShutdownAsync(keepMounts: true);
                    return 0;
                }
                case "agent" when args.Length > 1:
                {
                    switch (args[1])
                    {
                        case "refresh-startup":
                            Console.WriteLine(new StartupRegistration().RepointExistingAgent(StartupRegistration.AgentPath)
                                ? "Existing startup entry updated; its arguments and disabled/enabled choice are preserved."
                                : "No existing agent startup entry to update.");
                            return 0;
                        case "status":
                        {
                            var r = await AgentControlClient.StatusAsync(paths);
                            if (r?.Status is not { } st) { Console.WriteLine("The background agent is not running."); return 1; }
                            Console.WriteLine($"Agent pid {st.Pid}, running since {st.Started:g}");
                            foreach (var m in st.Mounts) Console.WriteLine($"  {m.Letter,-3} {m.Name,-24} {m.State,-9} permanent={m.Permanent} {m.Message}");
                            foreach (var w in st.Waiting) Console.WriteLine($"  waiting: {w}");
                            var reg = new StartupRegistration();
                            Console.WriteLine($"Windows sign-in entry: {reg.State(StartupRegistration.CommandFor(StartupRegistration.AgentPath, paths))}");
                            var settings = new JsonSettingsStore(paths.SettingsFile).Load();
                            var secret = SecretStore.Load(paths.TeraBoxBridgeSecretFile);
                            if (settings.TeraBox.BridgePort > 0 && secret != null)
                            {
                                try
                                {
                                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{TeraBoxService.BridgeUser}:{secret}")));
                                    var status = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{settings.TeraBox.BridgePort}/.cloudssd/status"));
                                    using (status)
                                    {
                                        var o = status.RootElement;
                                        if (o.TryGetProperty("providerThrottled", out var slow) && slow.GetBoolean())
                                            Console.WriteLine("Cloud reads: TOO SLOW / timed out. Uncached games may fail; prepare local data or retry later.");
                                        if (o.TryGetProperty("localSourceBytes", out var local))
                                            Console.WriteLine($"Verified local source: {ByteSize.Format(local.GetInt64())} served.");
                                    }
                                }
                                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
                                { Console.WriteLine("Cloud read status is temporarily unavailable."); }
                            }
                            return 0;
                        }
                        case "start":
                            AgentControlClient.Launch(paths);
                            Console.WriteLine("Agent started.");
                            return 0;
                        case "stop":
                            Console.WriteLine("Stopping the agent and unmounting its drives (pending uploads get up to 60 s)…");
                            if (await AgentControlClient.StatusAsync(paths) is null) { Console.WriteLine("The background agent is not running."); return 0; }
                            return await AgentControlClient.StopAsync(paths, TimeSpan.FromMinutes(3)) ? 0 : 1;
                        case "release":
                            return await AgentControlClient.ReleaseAsync(paths, TimeSpan.FromSeconds(30)) ? 0 : 1;
                    }
                    break;
                }
                case "scan" when args.Length > 1:
                {
                    var scan = GameScanner.Scan(args[1]);
                    var mbps = double.TryParse(Opt(args, "--mbps"), out var v) ? v : 70;
                    var r = CompatibilityAnalyzer.Analyze(scan, null, mbps, null, 0);
                    if (r.Executable != null && PeInspector.TryRead(Path.Combine(args[1], r.Executable)) is { } pe)
                        CompatibilityAnalyzer.AddDependencyIssues(r, DependencyResolver.Check(pe, Path.GetDirectoryName(Path.Combine(args[1], r.Executable))!, args[1]));
                    Console.WriteLine($"{scan.FileCount:N0} files, {ByteSize.Format(scan.TotalBytes)}, engine: {scan.Engine ?? "unknown"}, scanned in {scan.Elapsed.TotalSeconds:0.00} s");
                    Console.WriteLine("Executables:");
                    foreach (var e in scan.Executables) Console.WriteLine($"  [{e.Score,3}] {e.RelativePath} ({e.Reason})");
                    Console.WriteLine($"Markers: {string.Join(", ", scan.Markers)}");
                    Console.WriteLine($"Compatibility: {r.Summary}; recommended mode: {r.RecommendedMode}");
                    foreach (var i in r.Issues) Console.WriteLine($"  {i.Severity,-7} {i.Message}{(i.Suggestion != null ? $"\n          → {i.Suggestion}" : "")}");
                    return 0;
                }
                case "pe" when args.Length > 1:
                {
                    var pe = PeInspector.TryRead(args[1]);
                    if (pe is null) { Console.Error.WriteLine("Not a readable PE file."); return 1; }
                    Console.WriteLine($"{pe.Machine} {pe.Subsystem} .NET={pe.IsDotNet}");
                    foreach (var i in pe.AllImports) Console.WriteLine("  " + i);
                    foreach (var d in DependencyResolver.Check(pe, Path.GetDirectoryName(Path.GetFullPath(args[1]))!, Path.GetDirectoryName(Path.GetFullPath(args[1]))!))
                        Console.WriteLine($"  MISSING {d.Dll}: {d.Problem}{(d.Remedy != null ? " → " + d.Remedy : "")}");
                    return 0;
                }
                case "profile" when args.Length > 1:
                {
                    var store = new CloudSSD.Streaming.ProfileStore(paths.DatabaseFile);
                    var s = store.Summary(args[1], TimeSpan.FromMinutes(2));
                    Console.WriteLine($"Sessions: {s.Sessions}, distinct blocks: {s.DistinctBlocks}, startup estimate: {ByteSize.Format(s.StartupBytesEstimate)}, last: {s.LastSession}");
                    foreach (var p in store.LoadPredictorStats(args[1]))
                        Console.WriteLine($"  {p.Predictor,-12} issued {p.Issued} useful {p.Useful} wasted {p.Wasted} accuracy {(p.Accuracy is { } a ? a.ToString("P0") : "n/a")}{(p.Disabled ? " DISABLED" : "")}");
                    return 0;
                }
                case "terabox-bridge":
                {
                    var port = int.Parse(Opt(args, "--port") ?? throw new ArgumentException("--port is required"));
                    var settings = new JsonSettingsStore(paths.SettingsFile).Load();
                    var secret = SecretStore.Load(paths.TeraBoxBridgeSecretFile) ?? throw new InvalidOperationException("Bridge secret not found; connect TeraBox in the app.");
                    using var lf = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information)
                        .AddProvider(new AppLoggerProvider(paths.LogDirectory, new MemoryLogSink(), Microsoft.Extensions.Logging.LogLevel.Information, "terabox-bridge")));
                    var log = lf.CreateLogger("TeraBoxBridge");
                    var (api, http) = TeraBoxService.CreateTransport(paths, settings, log);
                    using var _http = http;
                    log.LogInformation("TeraBox transport: {Kind}, root {Root}", api.Kind, api.AppRoot);
                    await using var bridge = new CloudSSD.TeraBox.TeraBoxWebDavBridge(api, CloudSSD.App.TeraBoxService.BridgeUser, secret, paths.TeraBoxStagingDir(settings), log, port)
                    {
                        DeleteProtectionFile = Path.Combine(paths.DataRoot, "terabox-protect.txt"),
                        ProtectedDeleteRecoveryRoot = ".cloudssd-recovery",
                        ProtectedDeleteJournalDirectory = Path.Combine(paths.DataRoot, "terabox-recovery-journal"),
                        StagingFreeSpaceReserveBytes = settings.Cache.MinFreeSpaceBytes
                    };
                    var localSourcesFile = Path.Combine(paths.DataRoot, "terabox-local-sources.json");
                    if (File.Exists(localSourcesFile))
                    {
                        try
                        {
                            var mappings = System.Text.Json.JsonSerializer.Deserialize<CloudSSD.TeraBox.LocalSourceMapping[]>(
                                await File.ReadAllTextAsync(localSourcesFile),
                                new System.Text.Json.JsonSerializerOptions(JsonSettingsStore.Options) { PropertyNameCaseInsensitive = true }) ?? [];
                            mappings = mappings.Where(m => m != null && !string.IsNullOrWhiteSpace(m.RemotePrefix) &&
                                Path.IsPathFullyQualified(m.LocalRoot) && m.ChunkSize > 0 &&
                                !settings.Mounts.Any(mount => !string.IsNullOrEmpty(mount.DriveLetter) &&
                                    char.ToUpperInvariant(mount.DriveLetter[0]) == char.ToUpperInvariant(m.LocalRoot[0]))).ToArray();
                            bridge.LocalSources = new CloudSSD.TeraBox.VerifiedLocalSources(mappings,
                                Path.Combine(paths.DataRoot, "terabox-local-verification.json"));
                            log.LogInformation("Verified local sources enabled: {Count}", mappings.Length);
                        }
                        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or ArgumentException)
                        {
                            log.LogWarning("Local source configuration could not be loaded; continuing with cloud reads.");
                        }
                    }
                    var done = new TaskCompletionSource();
                    bridge.ShutdownRequested += () => done.TrySetResult();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
                    log.LogInformation("TeraBox bridge listening on {Url}", bridge.Url);
                    await done.Task;
                    await Task.Delay(200);
                    return 0;
                }
                case "import" when args.Length > 1:
                {
                    // Mode C from the command line: mount the drive, upload the installed game folder (resumable; files
                    // over the TeraBox per-file limit go up as parts on a chunked TeraBox drive), verify, publish the
                    // manifest and add it to the library. The local folder is only read. Re-running resumes.
                    var source = Path.GetFullPath(args[1]);
                    await using var host = AppHost.Create(paths);
                    if (!host.TryAcquireInstanceLock())
                    {
                        Console.Error.WriteLine("Close the CloudSSD window first (and stop the background agent: cloudssd-cli agent stop).");
                        return 2;
                    }
                    await host.InitializeAsync(restoreMounts: false);
                    var def = (Opt(args, "--drive") is { } want
                                  ? host.Settings.Mounts.FirstOrDefault(m => m.Id == want || m.DisplayName == want || m.DriveLetter == want.TrimEnd(':'))
                                  : host.Settings.Mounts.FirstOrDefault(host.IsTeraBoxMount))
                              ?? throw new ArgumentException("No such drive. See 'cloudssd-cli mounts'.");
                    var folder = Opt(args, "--folder") ?? "Games/" + Path.GetFileName(source.TrimEnd('\\'));
                    var mount = await host.MountAsync(def);
                    Console.WriteLine($"{def.DisplayName} mounted at {mount.DriveLetter}: (remote {def.RemoteName}).");
                    var plan = await host.PlanImportAsync(source, def, folder);
                    Console.WriteLine($"Plan: {plan.Files.Count} files, {ByteSize.Format(plan.TotalBytes)} -> {folder}");
                    foreach (var w in plan.Warnings) Console.WriteLine("  warning: " + w);
                    foreach (var b in plan.Blockers) Console.WriteLine("  BLOCKED: " + b);
                    if (!plan.CanStart) return 3;
                    var last = DateTime.MinValue;
                    var progress = new Progress<ImportProgress>(p =>
                    {
                        if (DateTime.UtcNow - last < TimeSpan.FromSeconds(30) && p.Stage == ImportStage.Uploading) return;
                        last = DateTime.UtcNow;
                        var eta = p.SpeedBytesPerSec > 0 ? Bandwidth.FormatDuration(TimeSpan.FromSeconds((p.TotalBytes - p.Bytes) / p.SpeedBytesPerSec)) : "?";
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {p.Stage,-10} {ByteSize.Format(p.Bytes)} / {ByteSize.Format(p.TotalBytes)} " +
                                          $"({p.Bytes / (double)Math.Max(1, p.TotalBytes):P1}) {p.SpeedBytesPerSec / 1e6:0.00} MB/s, ETA {eta} {p.Detail}");
                    });
                    var (result, game) = await host.ImportGameAsync(plan, def, args.Contains("--full-verify"), progress, CancellationToken.None);
                    Console.WriteLine(result.Message);
                    Console.WriteLine(result.Verified ? $"Imported and added to the library as '{game?.Name}'." : "NOT verified; run the same command again to resume.");
                    // Leave the drive mounted for the user and hand it to the background agent.
                    await host.ShutdownAsync(keepMounts: true);
                    await host.DisposeAsync();
                    try { AgentControlClient.Launch(paths); } catch (Exception e) { Console.WriteLine("Agent not started: " + e.Message); }
                    return result.Verified ? 0 : 4;
                }
                case "game" when args.Length > 1:
                {
                    // cloudssd-cli game list | add <folder on a mounted drive> [--name N] | prepare <name or id>
                    // Works while the background agent holds the drives: the agent lets go, the work runs, the agent
                    // takes the drives back. Drives are never unmounted.
                    var released = await AgentControlClient.ReleaseAsync(paths, TimeSpan.FromSeconds(30));
                    await using var host = AppHost.Create(paths);
                    if (!host.TryAcquireInstanceLock())
                    {
                        Console.Error.WriteLine("The CloudSSD window has the drives; use the Game Library there.");
                        return 2;
                    }
                    try
                    {
                        await host.InitializeAsync(restoreMounts: false);
                        switch (args[1])
                        {
                            case "list":
                                foreach (var g in host.Library.All())
                                    Console.WriteLine($"{g.Id}  {g.Name}  [{g.Mode}]  {g.RootPath}  readiness: {g.Readiness}  {g.LastLaunchResult}");
                                return 0;
                            case "add" when args.Length > 2:
                            {
                                var folder = Path.GetFullPath(args[2]).TrimEnd('\\');
                                var mount = host.Mounts.Sessions.FirstOrDefault(m => folder.StartsWith(m.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                                            ?? throw new ArgumentException($"{folder} is not on a mounted CloudSSD drive.");
                                var rel = folder[(mount.Root.TrimEnd('\\').Length + 1)..].Replace('\\', '/');
                                var g = await host.AddGameAsync(mount.Id, rel, Opt(args, "--name"));
                                if (g.Mode == ExecutionMode.HybridOptimized) { g.Mode = ExecutionMode.CloudDirect; host.Library.Save(g); } // keeps nothing outside the cache
                                Console.WriteLine($"Added '{g.Name}' (id {g.Id}), mode {g.Mode}, executable {g.Executable}, compatibility {g.Compatibility?.Level}: {g.Compatibility?.Summary}");
                                return 0;
                            }
                            case "prepare" when args.Length > 2:
                            {
                                var g = host.Library.All().FirstOrDefault(x => x.Id == args[2] || string.Equals(x.Name, args[2], StringComparison.OrdinalIgnoreCase))
                                        ?? throw new ArgumentException("No such game. See 'cloudssd-cli game list'.");
                                var last = DateTime.MinValue;
                                var steps = new Progress<IReadOnlyList<PrepareStep>>(s =>
                                {
                                    foreach (var st in s.Where(x => x.State is not (StepState.Pending or StepState.Running)))
                                        Console.WriteLine($"  [{st.State}] {st.Name}: {st.Detail}");
                                });
                                var progress = new Progress<CloudSSD.Streaming.PrepareProgress>(p =>
                                {
                                    if (DateTime.UtcNow - last < TimeSpan.FromSeconds(30)) return;
                                    last = DateTime.UtcNow;
                                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {ByteSize.Format(p.BytesFetched)} / {ByteSize.Format(p.BytesPlanned)}, ETA {(p.Eta is { } e ? Bandwidth.FormatDuration(e) : "?")}");
                                });
                                var report = await host.PrepareAsync(g, steps, progress, CancellationToken.None);
                                Console.WriteLine($"{report.Status}: {report.Headline}");
                                return 0;
                            }
                            default:
                                Console.Error.WriteLine("Usage: cloudssd-cli game list | add <folder on drive> [--name N] | prepare <name or id>");
                                return 1;
                        }
                    }
                    finally
                    {
                        await host.ShutdownAsync(keepMounts: true);
                        if (released || host.Settings.Mounts.Any(m => m.Permanent))
                            try { AgentControlClient.Launch(paths); } catch (Exception e) { Console.WriteLine("Agent not started: " + e.Message); }
                    }
                }
                case "terabox-probe":
                {
                    // Gate A: measure the connected TeraBox account directly (not through the drive). Uses a dedicated
                    // ".cloudssd-cli-probe-*" folder with synthetic files and deletes only that folder afterwards.
                    var settings = new JsonSettingsStore(paths.SettingsFile).Load();
                    using var lf = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information)
                        .AddProvider(new AppLoggerProvider(paths.LogDirectory, new MemoryLogSink(), Microsoft.Extensions.Logging.LogLevel.Information, "terabox-probe")));
                    var log = lf.CreateLogger("TeraBoxProbe");
                    var (api, http) = TeraBoxService.CreateTransport(paths, settings, log);
                    using var _http = http;
                    Console.WriteLine($"Probing TeraBox through the {api.Kind} connection (root {api.AppRoot}). This uploads ~10 MB of synthetic test data.");
                    var probe = new CloudSSD.TeraBox.TeraBoxProbe(api, Path.Combine(paths.DataRoot, "probe-tmp"), log)
                    {
                        Pagination = args.Contains("--pagination"),
                        RandomReads = int.TryParse(Opt(args, "--reads"), out var rr) ? rr : 24,
                    };
                    var report = args.Contains("--throughput")
                        ? await probe.ThroughputAsync(int.TryParse(Opt(args, "--size-mib"), out var mib) ? mib : 64, [1, 4, 8, 16], [4])
                        : await probe.RunAsync();
                    var outDir = Opt(args, "--out") ?? paths.DiagnosticsDirectory;
                    Directory.CreateDirectory(outDir);
                    var stem = Path.Combine(outDir, $"terabox-probe-{(api.Kind.StartsWith("web") ? "web" : "official")}-{DateTime.Now:yyyyMMdd-HHmmss}");
                    await File.WriteAllTextAsync(stem + ".json", Redactor.Redact(System.Text.Json.JsonSerializer.Serialize(report, CloudSSD.TeraBox.ProbeReport.Json)));
                    await File.WriteAllTextAsync(stem + ".md", Redactor.Redact(report.ToMarkdown()));
                    Console.WriteLine(report.ToMarkdown());
                    Console.WriteLine($"Report: {stem}.md / .json");
                    return report.Failures == 0 ? 0 : 3;
                }
                case "terabox-mock":
                {
                    var data = Opt(args, "--data") ?? throw new ArgumentException("--data is required");
                    await using var mock = new TeraBoxMockServer(Path.Combine(data, "cloud"), Path.Combine(data, "state"));
                    Console.WriteLine($"TeraBox Open Platform mock at {mock.BaseUrl} (client_id {mock.ClientId}). Ctrl+C to stop.");
                    Console.WriteLine($$"""terabox-client.json for testing: {"client_id":"{{mock.ClientId}}","client_secret":"{{mock.ClientSecret}}","private_secret":"{{mock.PrivateSecret}}","auth_base_url":"{{mock.BaseUrl}}"}""");
                    var done = new TaskCompletionSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
                    await done.Task;
                    return 0;
                }
                case "simcloud":
                {
                    var root = Opt(args, "--root") ?? throw new ArgumentException("--root is required");
                    var port = int.TryParse(Opt(args, "--port"), out var p) ? p : 8080;
                    await using var server = new SimCloudServer(root, port)
                    {
                        BandwidthMbps = double.TryParse(Opt(args, "--mbps"), out var mb) ? mb : 0,
                        Latency = TimeSpan.FromMilliseconds(double.TryParse(Opt(args, "--latency-ms"), out var lat) ? lat : 0)
                    };
                    Console.WriteLine($"Serving {server.Root} at {server.Url} ({server.BandwidthMbps:0} Mbps, {server.Latency.TotalMilliseconds:0} ms). Ctrl+C to stop.");
                    var done = new TaskCompletionSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
                    await done.Task;
                    Console.WriteLine($"Requests {server.Requests}, bytes sent {ByteSize.Format(server.BytesSent)}");
                    return 0;
                }
            }
            Console.Error.WriteLine("Unknown command.\n\n" + Usage);
            return 64;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Error: " + Redactor.Redact(e.Message));
            return 1;
        }
    }

    private static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
