using System.Collections.Concurrent;
using CloudSSD.Cache;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using CloudSSD.Games;
using CloudSSD.Mount;
using CloudSSD.Streaming;
using CloudSSD.Streaming.GameAware;
using Microsoft.Extensions.Logging;

namespace CloudSSD.App;

/// <summary>
/// The CloudSSD application service: owns settings, rclone, mounts, the prediction engine, the game library and
/// metrics, and wires real telemetry from rclone into the engine. UI and CLI are thin layers over this class.
/// </summary>
public sealed partial class AppHost : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, string> _remoteTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (NamedPipeTelemetryServer Server, GameAwareController Controller)> _gameAware = new();
    private volatile bool _diskPressure;
    private Timer? _diskTimer;
    private Timer? _bridgeTimer;
    private GameProcessWatcher? _gameWatcher;
    private readonly HashSet<string> _watchedSessions = new(StringComparer.Ordinal);
    private (DateTime At, IReadOnlyList<GameProcessWatcher.Watched> Games) _watchList = (DateTime.MinValue, []);
    private int _bridgeCheckRunning;
    private FileStream? _instanceLock;

    private AppHost(AppPaths paths)
    {
        Paths = paths;
        SettingsStore = new JsonSettingsStore(paths.SettingsFile);
        Settings = SettingsStore.Load();
        LogSink = new MemoryLogSink();
        LogProvider = new AppLoggerProvider(paths.LogDirectory, LogSink);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(LogProvider));
        _log = LoggerFactory.CreateLogger<AppHost>();
        RcloneConfig = new RcloneConfigService(paths, LoggerFactory.CreateLogger<RcloneConfigService>());
        Drive = new GoogleDriveConnector(RcloneConfig, LoggerFactory.CreateLogger<GoogleDriveConnector>());
        TeraBox = new TeraBoxService(paths, Settings, RcloneConfig, () => Rclone, CliPath, LoggerFactory.CreateLogger<TeraBoxService>());
        Mounts = new RcloneMountService(paths, RcloneConfig, LoggerFactory.CreateLogger<RcloneMountService>());
        Profiles = new ProfileStore(paths.DatabaseFile);
        Library = new GameLibrary(paths.DatabaseFile);
        Transfers = new TransferJournal(paths.DatabaseFile);
        CompletionActions = new CompletionJournal(paths.DatabaseFile);
        CacheIndex = new CacheIndex();
        Engine = new StreamingEngine(Settings.Streaming, Settings.Network, Profiles,
            new MountReadExecutor(id => Mounts.Get(id) is { State: MountState.Mounted } s ? s.Root : null, id => Mounts.Get(id)?.Attribution),
            FileSize, _ => !_diskPressure, CacheIndex.IsCached, LoggerFactory.CreateLogger<StreamingEngine>());
        Launcher = new GameLauncher(paths.DiagnosticsDirectory);
        Preparer = new GamePreparer(Engine, Profiles, RcloneConfig, CacheIndex);
        Installer = new FullLocalInstaller(RcloneConfig);
        Metrics = new MetricsCollector(this);
        Pins = new PinKeeper(PinnedTargets);

        Mounts.ReadObserved += Engine.OnAccess;
        Mounts.LookupObserved += (mount, path, off, present, pre) => Engine.OnLookup(mount, path, off, present, pre);
        Mounts.StatusChanged += s =>
        {
            _log.LogInformation("Mount {Id}: {State} {Message}", s.MountId, s.State, s.Message);
            if (s.State is MountState.Stopped or MountState.Failed)
            {
                CacheIndex.Unregister(s.MountId);
                // rclone may have evicted or lost data while the mount was down; forget what we believed was cached.
                Engine.Queue.ForgetCached(k => k.Mount == s.MountId);
            }
            StateChanged?.Invoke();
        };
        Engine.ApplicationBlockRead += (session, block) =>
        {
            if (_gameAware.TryGetValue(session.GameId, out var ga)) ga.Controller.OnApplicationRead(block);
        };
    }

    public AppPaths Paths { get; }
    public ISettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public MemoryLogSink LogSink { get; }
    public AppLoggerProvider LogProvider { get; }
    public ILoggerFactory LoggerFactory { get; }
    public RcloneInfo? Rclone { get; private set; }
    public RcloneConfigService RcloneConfig { get; }
    public GoogleDriveConnector Drive { get; }
    public TeraBoxService TeraBox { get; }
    public RcloneMountService Mounts { get; }
    public ProfileStore Profiles { get; }
    public GameLibrary Library { get; }
    public CacheIndex CacheIndex { get; }
    public StreamingEngine Engine { get; }
    public GameLauncher Launcher { get; }
    public GamePreparer Preparer { get; }
    public FullLocalInstaller Installer { get; }
    public MetricsCollector Metrics { get; }
    public PinKeeper Pins { get; }
    public IReadOnlyList<DependencyStatus> Dependencies { get; private set; } = [];
    public IReadOnlyList<RemoteEntry> Remotes { get; private set; } = [];
    public LaunchSession? CurrentLaunch { get; private set; }
    public bool DiskPressure => _diskPressure;
    public string CliPath => Path.Combine(AppPaths.AppDirectory, "cloudssd-cli.exe");

    /// <summary>Cumulative prefetch accounting over finished sessions (plus live sessions in <see cref="StreamingEngine.PrefetchTotals"/>).</summary>
    public long FinishedUsefulBytes, FinishedWastedBytes;
    public SessionSummary? LastSession { get; private set; }

    public event Action? StateChanged;

    public static AppHost Create(AppPaths? paths = null) => new(paths ?? new AppPaths());

    /// <summary>
    /// Prevents two CloudSSD instances from owning the same data/cache directories (concurrent cache ownership
    /// would corrupt rclone's cache). Returns false if another instance holds the lock.
    /// </summary>
    public bool TryAcquireInstanceLock()
    {
        try
        {
            _instanceLock = new FileStream(Path.Combine(Paths.DataRoot, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Waits up to <paramref name="wait"/> for the lock (e.g. while the background agent hands the drives over).</summary>
    public async Task<bool> TryAcquireInstanceLockAsync(TimeSpan wait, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            if (TryAcquireInstanceLock()) return true;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(200, ct);
        }
    }

    /// <param name="adoptMounts">False for read-only tools that must not take over or clean up running drives.</param>
    public async Task InitializeAsync(bool restoreMounts = true, CancellationToken ct = default, bool adoptMounts = true)
    {
        _log.LogInformation("CloudSSD starting (data root {Root})", Paths.DataRoot);
        Trace("locating rclone");
        Rclone = await RcloneLocator.LocateAsync(Settings.RclonePathOverride, Paths.DataRoot, ct);
        Trace(Rclone is null ? "rclone not found" : "rclone found; listing accounts");
        if (Rclone != null)
        {
            Mounts.SetRclone(Rclone);
            _log.LogInformation("Using {Version} at {Path}", Rclone.Version, Rclone.Path);
        }
        else _log.LogError("rclone not found");
        RefreshDependencies();
        await RefreshRemotesAsync(ct);
        Trace($"{Remotes.Count} account(s); adopting running drives");
        _diskTimer = new Timer(_ => CheckDisk(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        _bridgeTimer = new Timer(_ => _ = KeepBridgeAliveAsync(), null, BridgeWatchInterval, BridgeWatchInterval);
        if (Settings.Streaming.ReadProfilingEnabled)
            _gameWatcher = new GameProcessWatcher(WatchedGames, OnGameProcessStarted, OnGameProcessEnded);

        if (Rclone != null && adoptMounts)
        {
            foreach (var s in await Mounts.AdoptExistingAsync(ct))
            {
                CacheIndex.Register(s.Id, s.CacheDir, s.Definition.RemotePath);
                // A TeraBox drive kept mounted after the last exit needs its bridge (normally still running).
                if (TeraBoxService.IsTeraBoxMount(s.Definition))
                {
                    try { await TeraBox.EnsureBridgeAsync(ct); }
                    catch (Exception e) { _log.LogError("TeraBox bridge could not be started: {Error}", e.Message); }
                }
            }
            Trace("checking TeraBox drive set-up");
            await MigrateTeraBoxDrivesAsync(ct);
            Trace("restoring drives");
            if (restoreMounts && Settings.RestoreMountsOnStartup)
            {
                foreach (var def in Settings.Mounts.Where(m => m.AutoMount))
                {
                    if (Mounts.Get(def.Id) != null) continue;
                    Trace($"mounting {def.DisplayName}");
                    try { await MountAsync(def, ct); }
                    catch (Exception e) { _log.LogError("Could not restore mount {Name}: {Error}", def.DisplayName, e.Message); }
                }
            }
        }
        Metrics.Start();
        if (adoptMounts && _instanceLock != null) { StartRecovery(); StartCompletionActions(); }
        Trace($"start-up done; {Mounts.Sessions.Count} drive(s) running");
        StateChanged?.Invoke();
    }

    private void Trace(string step) => Diagnostics.StartupTrace.Mark(Paths.LogDirectory, "host", step);

    /// <summary>
    /// TeraBox drives use the chunked remote when large-file storage is on. Files already stored natively stay readable
    /// through it (chunker passes them through), so switching needs no data migration. Running drives are switched at
    /// their next mount, never under an open game.
    /// </summary>
    private async Task MigrateTeraBoxDrivesAsync(CancellationToken ct)
    {
        if (!TeraBox.HasLogin) return;
        var target = TeraBox.DriveRemoteName;
        var defs = Settings.Mounts.Where(m => TeraBoxService.IsTeraBoxMount(m) && m.RemoteName != target && Mounts.Get(m.Id) is null).ToList();
        var needRemote = target == TeraBoxService.ChunkedRemoteName && !Remotes.Any(r => r.Name == TeraBoxService.ChunkedRemoteName);
        if (defs.Count == 0 && !needRemote) return;
        try
        {
            await TeraBox.EnsureRemoteAsync(ct);
            await RefreshRemotesAsync(ct);
            foreach (var d in defs)
            {
                d.RemoteName = target;
                _log.LogInformation("TeraBox drive {Name} now uses {Remote}", d.DisplayName, target);
            }
            if (defs.Count > 0) SaveSettings();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException) { _log.LogWarning("TeraBox large-file setup failed: {Error}", e.Message); }
    }

    public void RefreshDependencies()
    {
        Dependencies = DependencyChecker.CheckAll(Rclone?.Path, Rclone?.Version, Paths.CacheRootFor(Settings), Settings.Cache.MinFreeSpaceBytes);
    }

    public async Task RefreshRemotesAsync(CancellationToken ct = default)
    {
        if (Rclone is null) return;
        try
        {
            Remotes = await RcloneConfig.ListRemotesAsync(Rclone, ct);
            foreach (var r in Remotes) _remoteTypes[r.Name] = r.Type;
        }
        catch (Exception e) { _log.LogWarning("Listing rclone remotes failed: {Error}", e.Message); }
    }

    public void SaveSettings() => SettingsStore.Save(Settings);

    public bool IsTeraBoxMount(MountDefinition d) => TeraBoxService.IsTeraBoxMount(d);

    public string? RemoteTypeOf(string remoteName) =>
        remoteName.StartsWith(':') ? remoteName.TrimStart(':') : _remoteTypes.GetValueOrDefault(remoteName);

    /// <summary>Budget rclone may use for one mount, after protecting the free-space reserve.</summary>
    public CacheBudgetDecision BudgetFor(string mountId)
    {
        var root = Paths.CacheRootFor(Settings);
        Directory.CreateDirectory(root);
        var current = CacheInspector.Measure(Paths.MountCacheDir(Settings, mountId));
        var mounts = Math.Max(1, Settings.Mounts.Count(m => m.AutoMount || Mounts.Get(m.Id) != null));
        return DiskSafetyGuard.Decide(Settings.Cache.BudgetBytes, Settings.Cache.MinFreeSpaceBytes, current.VolumeFreeBytes, current.PhysicalBytes, mounts);
    }

    public static TimeSpan BridgeWatchInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A mounted TeraBox drive is only as alive as its bridge process. If the bridge stops answering (crash, killed), start
    /// it again; rclone retries its requests meanwhile, so the drive recovers instead of failing until the next logon.
    /// </summary>
    public async Task KeepBridgeAliveAsync()
    {
        if (Interlocked.Exchange(ref _bridgeCheckRunning, 1) == 1) return;
        try
        {
            if (!Settings.Mounts.Any(m => TeraBoxService.IsTeraBoxMount(m) && Mounts.Get(m.Id) is { State: MountState.Mounted or MountState.Degraded or MountState.Starting })) return;
            if (await TeraBox.IsBridgeHealthyAsync(CancellationToken.None)) return;
            _log.LogWarning("The TeraBox bridge is not answering while a TeraBox drive is mounted; starting it again");
            await TeraBox.EnsureBridgeAsync(CancellationToken.None);
        }
        catch (Exception e) { _log.LogError("TeraBox bridge restart failed: {Error}", e.Message); }
        finally { Volatile.Write(ref _bridgeCheckRunning, 0); }
    }

    public async Task<MountSession> MountAsync(MountDefinition def, CancellationToken ct = default)
    {
        if (Rclone is null) throw new InvalidOperationException("rclone was not found. See Diagnostics.");
        if (TeraBoxService.IsTeraBoxMount(def)) await TeraBox.EnsureBridgeAsync(ct);
        var budget = BudgetFor(def.Id);
        foreach (var w in budget.Warnings) _log.LogWarning("{Warning}", w);
        var cacheDir = Paths.MountCacheDir(Settings, def.Id);
        var cache = Settings.Cache;
        if (TeraBoxService.IsTeraBoxMount(def))
        {
            cache = cache.Clone();
            // The parallel reader in rclone 1.75.2 can wait forever after an interrupted range.
            // Sequential reads propagate failures so cache recovery can continue. Parallel is explicit opt-in.
            cache.ReadChunkStreams = Math.Max(0, Settings.TeraBox.ReadStreams);
            if (Rclone.ParsedVersion <= new Version(1, 75, 2)) cache.ReadChunkStreams = 0;
            var minWriteBack = TimeSpan.FromSeconds(Settings.TeraBox.MinWriteBackSeconds);
            if (cache.WriteBack < minWriteBack) cache.WriteBack = minWriteBack;
        }
        var req = new MountRequest(def, cache, Settings.Network, cacheDir, Math.Max(16 * ByteSize.MiB, budget.EffectiveMaxBytes),
            Settings.Streaming.ReadProfilingEnabled, RemoteTypeOf(def.RemoteName));
        var s = await Mounts.MountAsync(req, ct);
        CacheIndex.Register(def.Id, cacheDir, def.RemotePath);
        if (s.DriveLetter.ToString() != def.DriveLetter)
        {
            def.DriveLetter ??= s.DriveLetter.ToString();
        }
        StateChanged?.Invoke();
        return s;
    }

    public async Task UnmountAsync(string mountId)
    {
        await Mounts.UnmountAsync(mountId, TimeSpan.FromSeconds(60));
        CacheIndex.Unregister(mountId);
        if (Settings.Mounts.FirstOrDefault(m => m.Id == mountId) is { } def && TeraBoxService.IsTeraBoxMount(def)
            && !Settings.Mounts.Any(m => TeraBoxService.IsTeraBoxMount(m) && Mounts.Get(m.Id) != null))
            await TeraBox.StopBridgeAsync();
        StateChanged?.Invoke();
    }

    public async Task RepairSteamRecoveryAsync(string mountId, bool resumePaused = false)
    {
        if (CurrentLaunch is { State: LaunchState.Starting or LaunchState.Running })
            throw new InvalidOperationException("Close the running game before repairing its drive.");
        var def = Settings.Mounts.FirstOrDefault(m => m.Id == mountId) ?? throw new InvalidOperationException("Drive no longer configured.");
        await Mounts.UnmountAsync(mountId, TimeSpan.Zero);
        CacheIndex.Unregister(mountId);
        var paused = new PausedSteamWrites(Paths.MountCacheDir(Settings, mountId));
        await Task.Run(() => { if (resumePaused) paused.Restore(); else paused.PauseIncompleteDownloads(); });
        await MountAsync(def);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Why the drive should not be unmounted right now, or null when it is idle: a game running from it, uploads
    /// waiting or in progress, or reads in the last 30 s. (rclone 1.75.2 does not report open handles of other
    /// programs, so an open but idle file in another program is not detected.)
    /// </summary>
    public async Task<string?> BusyReasonAsync(string mountId, CancellationToken ct = default)
    {
        if (CurrentLaunch is { State: LaunchState.Running } l && l.Game.MountId == mountId) return $"{l.Game.Name} is running from this drive";
        var s = Mounts.Get(mountId);
        if (s is null) return null;
        if (await Mounts.StatsAsync(mountId, ct) is { } v && v.UploadsInProgress + v.UploadsQueued > 0)
            return $"{v.UploadsInProgress + v.UploadsQueued} upload(s) still in progress";
        if (s.Request.Telemetry && DateTime.Now - s.LastActivity < IdleWindow) return "files on the drive are being read";
        return null;
    }

    public static TimeSpan IdleWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Waits until the drive is idle (see <see cref="BusyReasonAsync"/>), then unmounts it gracefully.</summary>
    public async Task UnmountWhenIdleAsync(string mountId, IProgress<string>? pending, CancellationToken ct)
    {
        while (await BusyReasonAsync(mountId, ct) is { } reason)
        {
            pending?.Report($"Unmount pending: {reason}");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        if (Mounts.Get(mountId) != null) await UnmountAsync(mountId);
    }

    private long? FileSize(string mountId, string mountPath)
    {
        var s = Mounts.Get(mountId);
        if (s is null) return null;
        try
        {
            var fi = new FileInfo(Path.Combine(s.Root, mountPath.Replace('/', '\\')));
            return fi.Exists ? fi.Length : null;
        }
        catch (IOException) { return null; }
    }

    private IReadOnlyList<(MountSession, string)> PinnedTargets()
    {
        var list = new List<(MountSession, string)>();
        foreach (var p in Settings.Cache.PinnedPaths)
        {
            // Stored as "<mountId>:<path in mount>".
            var i = p.IndexOf(':');
            if (i <= 0) continue;
            if (Mounts.Get(p[..i]) is { } m) list.Add((m, p[(i + 1)..]));
        }
        return list;
    }

    private void CheckDisk()
    {
        try
        {
            var root = Paths.CacheRootFor(Settings);
            Directory.CreateDirectory(root);
            var (free, _) = DiskInfo.FreeSpace(root);
            var pressure = DiskSafetyGuard.Pressure(free, Settings.Cache.MinFreeSpaceBytes);
            var was = _diskPressure;
            _diskPressure = pressure != Cache.DiskPressure.Normal;
            if (_diskPressure && !was) _log.LogWarning("Disk space low ({Free} free, reserve {Reserve}); prefetching paused", ByteSize.Format(free), ByteSize.Format(Settings.Cache.MinFreeSpaceBytes));
            if (!_diskPressure && was) _log.LogInformation("Disk space recovered; prefetching resumed");
        }
        catch (Exception) { }
    }

    // ---------------- games ----------------

    public async Task<GameEntry> AddGameAsync(string mountId, string rootInMount, string? name = null, CancellationToken ct = default)
    {
        var mount = Mounts.Get(mountId) ?? throw new InvalidOperationException("The drive is not mounted.");
        var root = Path.Combine(mount.Root, rootInMount.Replace('/', '\\'));
        if (!await BoundedFileSystem.Shared.RunAsync(() => Directory.Exists(root), TimeSpan.FromSeconds(5), ct)) throw new DirectoryNotFoundException(root);
        var scan = await BoundedFileSystem.Shared.RunAsync(() => GameScanner.Scan(root, ct: ct), TimeSpan.FromSeconds(60), ct);
        var g = new GameEntry
        {
            Name = name ?? Path.GetFileName(root.TrimEnd('\\')),
            MountId = mountId,
            RootPath = rootInMount.Replace('\\', '/').Trim('/'),
            Executable = scan.Executables.FirstOrDefault()?.RelativePath,
        };
        var summary = Profiles.Summary(g.Id, Settings.Streaming.StartupWindow);
        g.Compatibility = CompatibilityAnalyzer.Analyze(scan, g.Executable, Settings.Network.EffectiveMbps, null, summary.Sessions);
        if (g.Executable != null && await BoundedFileSystem.Shared.RunAsync(() => PeInspector.TryRead(Path.Combine(root, g.Executable)), TimeSpan.FromSeconds(15), ct) is { } pe)
        {
            g.Compatibility.Architecture = pe.Machine.ToString();
            var dependencies = await BoundedFileSystem.Shared.RunAsync(() => DependencyResolver.Check(pe, Path.GetDirectoryName(Path.Combine(root, g.Executable))!, root), TimeSpan.FromSeconds(20), ct);
            CompatibilityAnalyzer.AddDependencyIssues(g.Compatibility, dependencies);
        }
        g.Mode = g.Compatibility.RecommendedMode == ExecutionMode.FullLocal ? ExecutionMode.CloudDirect : g.Compatibility.RecommendedMode;
        if (g.Mode == ExecutionMode.HybridOptimized && !DependencyChecker.CanCreateSymlinks()) g.Mode = ExecutionMode.CloudDirect;
        Library.Save(g);
        _log.LogInformation("Added game {Name} ({Files} files, {Size}); compatibility: {Level}", g.Name, scan.FileCount, ByteSize.Format(scan.TotalBytes), g.Compatibility.Level);
        return g;
    }

    // ---------------- import (Mode C) ----------------

    /// <summary>TeraBox reports a 4 GB per-file limit for free accounts (decimal GB used to stay on the safe side).</summary>
    public const long TeraBoxFreeMaxFileSize = 4_000_000_000;

    public async Task<long?> CloudFreeBytesAsync(MountDefinition def, CancellationToken ct = default)
    {
        if (Rclone is null) return null;
        try
        {
            var r = await RcloneConfig.CreateRunner(Rclone).RunAsync(["about", def.RemoteName + ":", "--json"], TimeSpan.FromMinutes(1), ct);
            if (!r.Success) return null;
            return System.Text.Json.Nodes.JsonNode.Parse(r.StdOut)?["free"]?.GetValue<long>();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    public async Task<ImportPlan> PlanImportAsync(string sourceRoot, MountDefinition def, string remoteFolder, CancellationToken ct = default)
    {
        remoteFolder = GameImporter.NormalizeFolder(def, remoteFolder, (Mounts.Get(def.Id)?.DriveLetter)?.ToString());
        if (TeraBoxService.IsTeraBoxMount(def)) await TeraBox.EnsureBridgeAsync(ct);
        var tera = TeraBoxService.IsTeraBoxMount(def);
        // A chunked TeraBox drive stores large files as parts below the plan limit, so only the native remote has a per-file cap.
        long? max = tera && !TeraBoxService.IsChunked(def) && Settings.TeraBox.Plan is null or "Free" ? TeraBoxFreeMaxFileSize : null;
        return await Task.Run(async () => GameImporter.Plan(sourceRoot, remoteFolder, max, await CloudFreeBytesAsync(def, ct), max != null ? "TeraBox Free per-file limit" : null), ct);
    }

    /// <summary>Uploads a local game folder to the drive, verifies it, publishes the manifest and adds it to the library.</summary>
    public async Task<(ImportResult Result, GameEntry? Game)> ImportGameAsync(ImportPlan plan, MountDefinition def, bool fullVerify,
        IProgress<ImportProgress>? progress, CancellationToken ct, CompletionAction afterCompletion = CompletionAction.DoNothing)
    {
        Interlocked.Increment(ref _activeImports);
        using var importStop = CancellationTokenSource.CreateLinkedTokenSource(ct, _recoveryStop.Token);
        ct = importStop.Token;
        try
        {
        if (Rclone is null) throw new InvalidOperationException("rclone was not found.");
        if (TeraBoxService.IsTeraBoxMount(def)) await TeraBox.EnsureBridgeAsync(ct);
        var result = await new GameImporter(RcloneConfig).ImportAsync(Rclone, def, plan, RemoteTypeOf(def.RemoteName) == "drive", fullVerify, progress, ct);
        _log.LogInformation("Import {Game} to {Dest}: verified={Verified} - {Message}", plan.GameName, result.Destination, result.Verified, result.Message);
        if (!result.Verified) return (result, null);
        var mount = Mounts.Get(def.Id) ?? await MountAsync(def, ct);
        var parent = plan.RemoteFolder.Contains('/') ? plan.RemoteFolder[..plan.RemoteFolder.LastIndexOf('/')] : "";
        await Mounts.RefreshDirAsync(mount.Id, parent, ct); // uploaded by a separate rclone process: make the drive see it
        var game = await AddGameAsync(mount.Id, plan.RemoteFolder, plan.GameName, ct);
        var completionId = "import:" + game.Id + ":" + Guid.NewGuid().ToString("N");
        ArmCompletion(completionId, game.Name + " import", afterCompletion, game.Id);
        CompletionActions.MarkComplete(completionId);
        return (result, game);
        }
        finally { Interlocked.Decrement(ref _activeImports); }
    }
    /// <summary>What "Fully cached" would cost on this PC, and whether it fits (game size vs the drive's cache budget and free space).</summary>
    public (long GameBytes, long AlreadyCached, long Budget, long Free, bool Fits, string Explanation) FullCacheCheck(GameEntry g)
    {
        var mount = Mounts.Get(g.MountId) ?? throw new InvalidOperationException("Mount the game's drive first.");
        var root = Path.Combine(mount.Root, g.RootPath.Replace('/', '\\'));
        var size = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Sum(f => new FileInfo(f).Length);
        var usage = CacheInspector.Measure(mount.CacheDir);
        var budget = BudgetFor(mount.Id);
        var needed = size - Math.Min(size, usage.CachedBytes);
        var fits = size <= budget.EffectiveMaxBytes && needed < usage.VolumeFreeBytes - Settings.Cache.MinFreeSpaceBytes;
        var why = fits
            ? $"Needs {ByteSize.Format(size)} on {DiskInfo.VolumeRoot(mount.CacheDir)} (about {ByteSize.Format(needed)} more than now)."
            : size > budget.EffectiveMaxBytes
                ? $"The game is {ByteSize.Format(size)} but this drive's cache may use only {ByteSize.Format(budget.EffectiveMaxBytes)}. Raise the budget in Smart Cache (or move the cache to a larger drive) first."
                : $"Not enough free space: {ByteSize.Format(needed)} more is needed and {ByteSize.Format(Settings.Cache.MinFreeSpaceBytes)} must stay free for Windows.";
        return (size, usage.CachedBytes, budget.EffectiveMaxBytes, usage.VolumeFreeBytes, fits, why);
    }

    /// <summary>
    /// "Fully cached" mode: downloads the whole game into the drive's local cache and verifies every file in rclone's own
    /// cache index. Costs the full game size locally; refused when it does not fit the budget or free space.
    /// </summary>
    private async Task<HydrateResult> FullyCacheCoreAsync(GameEntry g, IProgress<HydrateProgress>? progress, CancellationToken ct)
    {
        var check = FullCacheCheck(g);
        if (!check.Fits) throw new InvalidOperationException(check.Explanation);
        var mount = Mounts.Get(g.MountId)!;
        var root = Path.Combine(mount.Root, g.RootPath.Replace('/', '\\'));
        var parallel = TeraBoxService.IsTeraBoxMount(mount.Definition) ? Math.Max(1, Settings.TeraBox.ReadStreams) : 4;
        _log.LogInformation("Fully caching {Game} ({Size}) with {Parallel} parallel reads", g.Name, ByteSize.Format(check.GameBytes), parallel);
        var r = await CacheHydrator.HydrateAsync(root, mount.CacheDir, mount.Definition.RemotePath, g.RootPath, parallel, progress, ct);
        g.Readiness = r.Complete ? ReadinessStatus.ReadyToLaunch : ReadinessStatus.InsufficientCacheSpace;
        g.ReadinessDetail = r.Complete ? r.Message + $" Uses {ByteSize.Format(r.TotalBytes)} of local disk." : r.Message;
        var pin = $"{mount.Id}:{g.RootPath}";
        if (r.Complete && !Settings.Cache.PinnedPaths.Contains(pin)) Settings.Cache.PinnedPaths.Add(pin); // keep it recent (best effort)
        Library.Save(g);
        SaveSettings();
        _log.LogInformation("Fully cached {Game}: {Message}", g.Name, r.Message);
        return r;
    }

    public async Task<PrepareReport> PrepareAsync(GameEntry g, IProgress<IReadOnlyList<PrepareStep>>? steps, IProgress<PrepareProgress>? progress, CancellationToken ct)
    {
        if (Rclone is null) throw new InvalidOperationException("rclone was not found.");
        var mount = Mounts.Get(g.MountId);
        if (mount is null && Settings.Mounts.FirstOrDefault(m => m.Id == g.MountId) is { } def && g.Mode != ExecutionMode.FullLocal)
            mount = await MountAsync(def, ct);
        var report = await Task.Run(() => Preparer.PrepareAsync(g, mount, Settings, Rclone, steps, progress, ct), ct).WaitAsync(ct);
        Library.Save(g);
        SaveSettings();
        _log.LogInformation("Prepare {Game}: {Status} - {Headline}", g.Name, report.Status, report.Headline);
        return report;
    }

    private int _launchStarting;
    public bool IsLaunching => Volatile.Read(ref _launchStarting) != 0;

    public async Task<LaunchSession> PlayAsync(GameEntry g, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _launchStarting, 1) != 0) throw new InvalidOperationException("A game launch is already being prepared.");
        try { StateChanged?.Invoke(); return await Task.Run(() => PlayCoreAsync(g, ct), ct); }
        finally { Volatile.Write(ref _launchStarting, 0); StateChanged?.Invoke(); }
    }

    private async Task<LaunchSession> PlayCoreAsync(GameEntry g, CancellationToken ct)
    {
        if (CurrentLaunch is { State: LaunchState.Running or LaunchState.Starting }) throw new InvalidOperationException($"{CurrentLaunch.Game.Name} is already running or starting.");
        MountSession? mount = null;
        if (g.Mode != ExecutionMode.FullLocal)
        {
            mount = Mounts.Get(g.MountId);
            if (mount is not { State: MountState.Mounted or MountState.Degraded })
            {
                var def = Settings.Mounts.FirstOrDefault(m => m.Id == g.MountId) ?? throw new InvalidOperationException("The game's cloud drive is not configured.");
                mount = await MountAsync(def, ct);
            }
            if (TeraBoxService.IsTeraBoxMount(mount.Definition)) await TeraBox.EnsureBridgeAsync(ct);
            var readyUntil = DateTime.UtcNow.AddSeconds(30);
            while (mount.State == MountState.Starting && DateTime.UtcNow < readyUntil) await Task.Delay(200, ct);
            if (mount.State is not (MountState.Mounted or MountState.Degraded))
                throw new InvalidOperationException("The cloud drive is still recovering. Downloaded data is kept; launch again when the drive is available.");
        }
        GameSession? session = null;
        if (mount != null && Settings.Streaming.ReadProfilingEnabled)
        {
            session = Engine.BeginSession(g.Id, mount.Id, g.RootPath, g.Mode.ToString());
            if (g.GameAwareEnabled) StartGameAware(g, session);
        }
        LaunchSession launch;
        try
        {
            launch = await Launcher.LaunchAsync(g, mount?.Root, ct);
        }
        catch
        {
            if (session != null) Engine.EndSession(g.Id, save: false);
            StopGameAware(g.Id);
            throw;
        }
        CurrentLaunch = launch;
        g.LastPlayed = DateTime.Now;
        Library.Save(g);
        _log.LogInformation("Launched {Game} in {Mode} mode: {Exe}", g.Name, g.Mode, launch.Executable);
        StateChanged?.Invoke();
        _ = launch.Completion.ContinueWith(t => OnLaunchEnded(g, t.Result), TaskScheduler.Default);
        return launch;
    }

    // ------------------------------------------------------------------ games started outside CloudSSD

    /// <summary>Library games on mounted drives, with their folder as Windows sees it (refreshed every 30 s).</summary>
    private IReadOnlyList<GameProcessWatcher.Watched> WatchedGames()
    {
        if (DateTime.UtcNow - _watchList.At < TimeSpan.FromSeconds(30)) return _watchList.Games;
        var list = new List<GameProcessWatcher.Watched>();
        foreach (var g in Library.All().Where(g => g.Mode != ExecutionMode.FullLocal))
            if (Mounts.Get(g.MountId) is { State: MountState.Mounted } m)
                list.Add(new(g.Id, Path.Combine(m.Root, g.RootPath.Replace('/', '\\'))));
        _watchList = (DateTime.UtcNow, list);
        return list;
    }

    /// <summary>
    /// A library game is running from its drive (started by Steam or another launcher, or still running after the
    /// process CloudSSD started handed over to its launcher): record its reads so its working set is learned.
    /// </summary>
    private void OnGameProcessStarted(string gameId, string exe)
    {
        lock (_watchedSessions)
        {
            if (Engine.HasSession(gameId)) return; // started through Play: that session records it
            if (Library.Get(gameId) is not { } g || Mounts.Get(g.MountId) is not { State: MountState.Mounted } m) return;
            Engine.BeginSession(g.Id, m.Id, g.RootPath, "Watched");
            _watchedSessions.Add(gameId);
            _log.LogInformation("{Game} is running from the drive ({Exe}), started outside CloudSSD; recording its reads", g.Name, exe);
        }
        StateChanged?.Invoke();
    }

    private void OnGameProcessEnded(string gameId)
    {
        SessionSummary? summary;
        lock (_watchedSessions)
        {
            if (!_watchedSessions.Remove(gameId)) return;
            summary = Engine.EndSession(gameId);
        }
        if (Library.Get(gameId) is { } g)
        {
            g.LastPlayed = DateTime.Now;
            g.LastLaunchResult = $"Ran outside CloudSSD until {DateTime.Now:g}" + (summary != null
                ? $"; reads hit {summary.Hits}/{summary.Hits + summary.Misses}, stalls {summary.StallMs / 1000:0.0} s; profile saved"
                : "");
            Library.Save(g);
            _log.LogInformation("{Game} stopped running from the drive: {Result}", g.Name, g.LastLaunchResult);
        }
        if (summary != null) LastSession = summary;
        StateChanged?.Invoke();
    }

    private void OnLaunchEnded(GameEntry g, LaunchDiagnostics d)
    {
        // Store games restart themselves through their launcher: the process CloudSSD started is gone, the game is not.
        if (_gameWatcher != null)
        {
            _gameWatcher.Tick(DateTime.UtcNow);
            if (_gameWatcher.Running.Contains(g.Id))
            {
                lock (_watchedSessions) _watchedSessions.Add(g.Id);
                _log.LogInformation("{Game}: the started process exited but the game is still running from the drive (launcher hand-over); profiling continues", g.Name);
                return;
            }
        }
        StopGameAware(g.Id);
        var summary = Engine.EndSession(g.Id);
        if (summary != null)
        {
            LastSession = summary;
            Interlocked.Add(ref FinishedUsefulBytes, summary.UsefulPrefetchBytes);
            Interlocked.Add(ref FinishedWastedBytes, summary.WastedPrefetchBytes);
        }
        var fresh = Library.Get(g.Id) ?? g;
        // Process lifetime (even with a window) does not prove gameplay: a black screen can render for minutes.
        // Keep explicit verification separate from these automatically recorded session results.
        fresh.LastLaunchResult = d.LikelyFailure
            ? $"Failed {d.Started:g}: {string.Join(" ", d.Notes)}"
            : $"Ran {Bandwidth.FormatDuration(d.Runtime)} on {d.Started:g} ({d.Mode})" + (summary != null
                ? $"; reads hit {summary.Hits}/{summary.Hits + summary.Misses}, stalls {summary.StallMs / 1000:0.0} s"
                : "");
        Library.Save(fresh);
        _log.Log(d.LikelyFailure ? LogLevel.Warning : LogLevel.Information, "{Game} exited: {Result}", g.Name, fresh.LastLaunchResult);
        StateChanged?.Invoke();
    }

    private void StartGameAware(GameEntry g, GameSession session)
    {
        var server = new NamedPipeTelemetryServer(g.Id);
        var controller = new GameAwareController(new RegionAssetMap(), server,
            (p, tag) => Engine.ScheduleExternal(session, p, tag), prefix => Engine.CancelTagged(session, prefix));
        _gameAware[g.Id] = (server, controller);
        _log.LogInformation("Game-Aware telemetry listening on \\\\.\\pipe\\{Pipe}", server.PipeName);
    }

    private void StopGameAware(string gameId)
    {
        if (!_gameAware.TryRemove(gameId, out var ga)) return;
        ga.Controller.Dispose();
        _ = ga.Server.DisposeAsync();
    }

    public async Task ShutdownAsync(bool keepMounts)
    {
        if (!keepMounts)
            foreach (var session in Mounts.Sessions) await Mounts.EnsureCanUnmountAsync(session.Id);
        await StopRecoveryAsync();
        Metrics.Dispose();
        if (keepMounts)
        {
            Mounts.DetachAll();
            _log.LogInformation("Exiting; drives stay mounted and will be re-attached on next start");
        }
        else
        {
            await Mounts.UnmountAllAsync(TimeSpan.FromSeconds(60));
            await TeraBox.StopBridgeAsync();
            _log.LogInformation("Exiting; all drives unmounted");
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await StopRecoveryAsync();
        _diskTimer?.Dispose();
        _bridgeTimer?.Dispose();
        _gameWatcher?.Dispose();
        Pins.Dispose();
        Metrics.Dispose();
        foreach (var id in _gameAware.Keys.ToList()) StopGameAware(id);
        await Engine.DisposeAsync();
        await Mounts.DisposeAsync();
        CacheIndex.Dispose();
        LoggerFactory.Dispose();
        _instanceLock?.Dispose();
    }
}
