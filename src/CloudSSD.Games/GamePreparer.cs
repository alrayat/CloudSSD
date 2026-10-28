using CloudSSD.Cache;
using CloudSSD.Core;
using CloudSSD.Mount;
using CloudSSD.Streaming;

namespace CloudSSD.Games;

public enum StepState { Pending, Running, Done, Warning, Failed, Skipped }

public sealed record PrepareStep(string Name, StepState State, string? Detail);

public sealed record PrepareReport(
    ReadinessStatus Status,
    string Headline,
    IReadOnlyList<PrepareStep> Steps,
    PrepareResult? Prefetch,
    CompatibilityReport? Compatibility,
    NetworkProbeResult? Network,
    long CacheAvailableBytes,
    double? StartupCoverage,
    long EssentialBytesCached);

/// <summary>
/// "Optimize &amp; Prepare": inspects the game, checks space and real network speed, prefetches the essential files and
/// the startup working set learned from previous launches, validates the executable, and reports an honest readiness
/// status. When the startup data is unknown (never profiled) it says so instead of showing a made-up percentage.
/// </summary>
public sealed class GamePreparer
{
    private readonly StreamingEngine _engine;
    private readonly ProfileStore _profiles;
    private readonly RcloneConfigService _config;
    private readonly CacheIndex? _index;

    public GamePreparer(StreamingEngine engine, ProfileStore profiles, RcloneConfigService config, CacheIndex? index = null)
    {
        _engine = engine;
        _profiles = profiles;
        _config = config;
        _index = index;
    }

    public async Task<PrepareReport> PrepareAsync(GameEntry game, MountSession? mount, AppSettings settings, RcloneInfo rclone,
        IProgress<IReadOnlyList<PrepareStep>>? stepsProgress, IProgress<PrepareProgress>? prefetchProgress, CancellationToken ct)
    {
        var steps = new List<PrepareStep>
        {
            new("Inspect game folder", StepState.Pending, null),
            new("Check cache capacity", StepState.Pending, null),
            new("Measure network speed", StepState.Pending, null),
            new("Load access profile", StepState.Pending, null),
            new("Analyze compatibility", StepState.Pending, null),
            new("Prepare essential files", StepState.Pending, null),
            new("Prefetch startup data", StepState.Pending, null),
            new("Validate", StepState.Pending, null),
        };
        void Set(int i, StepState s, string? d)
        {
            steps[i] = steps[i] with { State = s, Detail = d };
            stepsProgress?.Report(steps.ToList());
        }
        PrepareReport Done(ReadinessStatus st, string headline, PrepareResult? pf = null, CompatibilityReport? cr = null, NetworkProbeResult? net = null,
            long avail = 0, double? cov = null, long essential = 0)
        {
            for (int i = 0; i < steps.Count; i++)
                if (steps[i].State is StepState.Pending or StepState.Running) steps[i] = steps[i] with { State = StepState.Skipped };
            stepsProgress?.Report(steps.ToList());
            game.Readiness = st;
            game.ReadinessDetail = headline;
            return new PrepareReport(st, headline, steps.ToList(), pf, cr, net, avail, cov, essential);
        }

        if (mount is null || mount.State != MountState.Mounted ||
            !await BoundedFileSystem.Shared.RunAsync(() => Directory.Exists(mount.Root), TimeSpan.FromSeconds(5), ct))
        {
            if (game.Mode != ExecutionMode.FullLocal)
                return Done(ReadinessStatus.DriveNotMounted, "The cloud drive for this game is not mounted. Mount it in Cloud Drives first.");
        }

        // 1. Inspect
        Set(0, StepState.Running, null);
        var cloudRoot = mount is null ? null : Path.Combine(mount.Root, game.RootPath.Replace('/', '\\'));
        if (cloudRoot is null || !await BoundedFileSystem.Shared.RunAsync(() => Directory.Exists(cloudRoot), TimeSpan.FromSeconds(5), ct))
            return Done(ReadinessStatus.UnsupportedFilesystemOperation, $"Game folder not found on the drive: {cloudRoot}");
        var scan = await BoundedFileSystem.Shared.RunAsync(() => GameScanner.Scan(cloudRoot, ct: ct), TimeSpan.FromSeconds(60), ct);
        Set(0, StepState.Done, $"{scan.FileCount:N0} files, {ByteSize.Format(scan.TotalBytes)} logical size, scanned in {scan.Elapsed.TotalSeconds:0.0} s{(scan.Engine != null ? $", engine: {scan.Engine}" : "")}");

        // 2. Capacity
        Set(1, StepState.Running, null);
        var usage = CacheInspector.Measure(mount!.CacheDir);
        var decision = DiskSafetyGuard.Decide(settings.Cache.BudgetBytes, settings.Cache.MinFreeSpaceBytes, usage.VolumeFreeBytes, usage.PhysicalBytes,
            Math.Max(1, settings.Mounts.Count(m => m.AutoMount)));
        var available = Math.Max(0, Math.Min(decision.EffectiveMaxBytes - usage.PhysicalBytes, usage.VolumeFreeBytes - settings.Cache.MinFreeSpaceBytes));
        Set(1, decision.Pressure == DiskPressure.Normal ? StepState.Done : StepState.Warning,
            $"Cache budget {ByteSize.Format(decision.EffectiveMaxBytes)}, in use {ByteSize.Format(usage.PhysicalBytes)}, available for this game {ByteSize.Format(available)}; disk free {ByteSize.Format(usage.VolumeFreeBytes)}"
            + (decision.Warnings.Count > 0 ? ". " + string.Join(" ", decision.Warnings) : ""));

        // 3. Network
        Set(2, StepState.Running, null);
        NetworkProbeResult? net = null;
        var probeFile = scan.Files.OrderByDescending(f => f.Size).FirstOrDefault();
        if (probeFile != null && probeFile.Size > 0)
        {
            var count = Math.Min(8 * ByteSize.MiB, probeFile.Size);
            var offset = Math.Max(0, probeFile.Size / 2 - count / 2);
            var rel = string.IsNullOrEmpty(game.RootPath) ? probeFile.RelativePath : game.RootPath.TrimEnd('/') + "/" + probeFile.RelativePath;
            net = await NetworkProbe.MeasureAsync(_config.CreateRunner(rclone), mount.Definition, rel, offset, count, ct);
        }
        if (net is { Success: true, Mbps: { } mbps })
        {
            settings.Network.MeasuredDownloadMbps = Math.Round(mbps, 1);
            settings.Network.MeasuredLatencyMs = net.FirstByteMs;
            settings.Network.MeasuredAt = DateTimeOffset.Now;
            _engine.Governor.DeclaredBytesPerSec = Bandwidth.MbpsToBytesPerSecond(mbps);
            Set(2, StepState.Done, $"{mbps:0.0} Mbps ({Bandwidth.BytesPerSecondToMBps(Bandwidth.MbpsToBytesPerSecond(mbps)):0.00} MB/s) from the provider; first byte after {net.FirstByteMs:0} ms (includes rclone start-up)");
        }
        else if (net is { Success: false })
        {
            Set(2, StepState.Failed, net.Error);
            var err = net.Error ?? "";
            if (err.StartsWith("Cloud authentication required", StringComparison.Ordinal))
                return Done(ReadinessStatus.CloudAuthenticationRequired, err, net: net);
            if (err.StartsWith("Network unavailable", StringComparison.Ordinal))
                return Done(ReadinessStatus.NetworkUnavailable, err, net: net);
        }
        else Set(2, StepState.Skipped, "No file to measure with.");
        var effectiveMbps = settings.Network.EffectiveMbps;

        // 4. Profile
        Set(3, StepState.Running, null);
        var summary = _profiles.Summary(game.Id, settings.Streaming.StartupWindow);
        Set(3, summary.Sessions > 0 ? StepState.Done : StepState.Warning, summary.Sessions > 0
            ? $"{summary.Sessions} recorded launch(es); learned startup set ≈ {ByteSize.Format(summary.StartupBytesEstimate)}"
            : "No launch profile yet — startup data unknown. The first launch will be profiled (exploratory mode).");

        // 5. Compatibility
        Set(4, StepState.Running, null);
        var exeRel = game.Executable ?? scan.Executables.FirstOrDefault()?.RelativePath;
        game.Executable ??= exeRel;
        var report = CompatibilityAnalyzer.Analyze(scan, exeRel, effectiveMbps, summary.Sessions > 0 ? summary.StartupBytesEstimate : null, summary.Sessions, game.VerifiedModes);
        if (exeRel != null && await BoundedFileSystem.Shared.RunAsync(() => PeInspector.TryRead(Path.Combine(cloudRoot, exeRel)), TimeSpan.FromSeconds(15), ct) is { } pe)
        {
            report.Architecture = pe.Machine.ToString();
            var exeDir = Path.GetDirectoryName(Path.Combine(cloudRoot, exeRel))!;
            var dependencies = await BoundedFileSystem.Shared.RunAsync(() => DependencyResolver.Check(pe, exeDir, cloudRoot), TimeSpan.FromSeconds(20), ct);
            CompatibilityAnalyzer.AddDependencyIssues(report, dependencies);
        }
        game.Compatibility = report;
        Set(4, report.Level switch
        {
            CompatibilityLevel.NeedsLocalInstall or CompatibilityLevel.Incompatible => StepState.Failed,
            CompatibilityLevel.Uncertain => StepState.Warning,
            _ => StepState.Done
        }, $"{report.Summary}. Recommended mode: {report.RecommendedMode}. {report.Issues.Count(i => i.Severity != Severity.Info)} warning(s).");
        if (report.Level is CompatibilityLevel.NeedsLocalInstall or CompatibilityLevel.Incompatible && game.Mode != ExecutionMode.FullLocal)
            return Done(report.Level == CompatibilityLevel.Incompatible ? ReadinessStatus.UnsupportedFilesystemOperation : ReadinessStatus.RequiresFullLocalInstallation,
                report.Issues.FirstOrDefault(i => i.Severity == Severity.Error)?.Message ?? report.Summary, cr: report, net: net, avail: available);

        // 6. Essentials
        Set(5, StepState.Running, null);
        long essentialBytes = 0;
        HashSet<string>? localCopies = null;
        if (game.Mode == ExecutionMode.HybridOptimized)
        {
            var plan = HybridBuilder.Plan(scan.Files, pinned: settings.Cache.PinnedPaths);
            var localRoot = game.LocalRoot ?? Path.Combine(settings.LocalGamesRoot ?? Path.Combine(Path.GetDirectoryName(mount.CacheDir)!, "..", "local-games"), $"{Sanitize(game.Name)}-{game.Id}");
            var (fits, free, _) = FullLocalInstaller.CheckSpace(localRoot, plan.CopyBytes, settings.Cache.MinFreeSpaceBytes);
            if (!fits)
                return Done(ReadinessStatus.InsufficientCacheSpace, $"Hybrid mode needs {ByteSize.Format(plan.CopyBytes)} of local space for essential files; only {ByteSize.Format(free)} is free.", cr: report, net: net, avail: available);
            var built = await HybridBuilder.BuildAsync(cloudRoot, localRoot, plan, null, ct);
            game.LocalRoot = built.LocalRoot;
            essentialBytes = plan.CopyBytes;
            localCopies = plan.Copy.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Set(5, built.Errors.Count == 0 ? StepState.Done : StepState.Warning,
                $"Local folder: {built.LocalRoot}. Copied {built.Copied} ({ByteSize.Format(built.CopiedBytes)}), linked {built.Linked}, unchanged {built.Unchanged}." +
                (built.Errors.Count > 0 ? " " + string.Join(" ", built.Errors.Take(3)) : ""));
            if (built.Errors.Any(e => e.Contains("Symbolic link failed")))
                return Done(ReadinessStatus.UnsupportedFilesystemOperation, "Hybrid mode could not create symbolic links. Enable Windows Developer Mode or use Cloud Direct.", cr: report, net: net);
        }
        else if (game.Mode == ExecutionMode.FullLocal)
        {
            var ok = game.LocalRoot != null && game.Executable != null && File.Exists(Path.Combine(game.LocalRoot, game.Executable));
            Set(5, ok ? StepState.Done : StepState.Failed, ok ? $"Local installation found at {game.LocalRoot}" : "No complete local copy yet. Use Install Locally (requires your approval).");
            if (!ok) return Done(ReadinessStatus.RequiresFullLocalInstallation, "Full Local mode selected but the game has not been copied locally yet.", cr: report, net: net);
            return Done(ReadinessStatus.ReadyToLaunch, "Runs from the local SSD copy; no streaming needed.", cr: report, net: net, cov: 1);
        }
        else
        {
            var essentials = EssentialFiles(scan, exeRel).ToList();
            var budget = Math.Min(available, 512 * ByteSize.MiB);
            foreach (var f in essentials)
            {
                if (essentialBytes + f.Size > budget) break;
                ct.ThrowIfCancellationRequested();
                await WarmFileAsync(Path.Combine(cloudRoot, f.RelativePath), mount, game.RootPath, f.RelativePath, ct);
                essentialBytes += f.Size;
            }
            Set(5, StepState.Done, $"Cached {essentials.Count} essential file(s) (executable, DLLs, configuration): {ByteSize.Format(essentialBytes)}");
        }

        // 7. Startup prefetch
        Set(6, StepState.Running, null);
        PrepareResult? pf = null;
        if (summary.Sessions > 0 && settings.Streaming.PredictionEnabled)
        {
            pf = await _engine.PrepareAsync(game.Id, mount.Id, game.RootPath, Math.Max(0, available - essentialBytes), prefetchProgress, ct,
                include: localCopies is null ? null : p => !localCopies.Contains(p));
            Set(6, pf.FailedBlocks == 0 ? StepState.Done : StepState.Warning,
                $"Planned {pf.PlannedBlocks} block(s) ({ByteSize.Format(pf.BytesPlanned)}), fetched {pf.FetchedBlocks} ({ByteSize.Format(pf.BytesFetched)}), " +
                $"already cached {pf.AlreadyCachedBlocks}, failed {pf.FailedBlocks}, trimmed for budget {pf.TrimmedForBudget}, mean confidence {pf.MeanConfidence:P0}, in {pf.Elapsed.TotalSeconds:0} s");
        }
        else if (summary.Sessions == 0 && game.Mode != ExecutionMode.HybridOptimized)
        {
            // Never launched: warm the archives' tables of contents and all small files (see IndexWarmup). Capped so it
            // cannot take over the cache, and reported for what it is: a heuristic, not learned data.
            var essentialsSet = EssentialFiles(scan, exeRel).Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var warmBudget = Math.Max(0, Math.Min(available - essentialBytes, Math.Min(settings.Cache.BudgetBytes / 4, 4 * ByteSize.GiB)));
            var plan = IndexWarmup.PlanFor(scan.Files, warmBudget, essentialsSet);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (warmed, failed) = await IndexWarmup.WarmAsync(plan, cloudRoot, mount, game.RootPath, parallel: 4,
                prefetchProgress is null ? null : new Progress<(long Done, long Total)>(p =>
                {
                    var rate = p.Done / Math.Max(1, sw.Elapsed.TotalSeconds);
                    prefetchProgress.Report(new PrepareProgress((int)(p.Total / ByteSize.MiB), (int)(p.Done / ByteSize.MiB), 0, 0, p.Done, p.Total, sw.Elapsed,
                        rate > 0 ? TimeSpan.FromSeconds((p.Total - p.Done) / rate) : null));
                }), ct);
            Set(6, failed == 0 && plan.Trimmed == 0 ? StepState.Done : StepState.Warning,
                $"No launch profile yet: cached {plan.SmallFiles} small file(s) whole and the start/end (archive index) of {plan.LargeFiles} large file(s): " +
                $"{ByteSize.Format(warmed)} in {sw.Elapsed.TotalSeconds:0} s" + (plan.Trimmed > 0 ? $"; {plan.Trimmed} range(s) left out for budget" : "") +
                (failed > 0 ? $"; {failed} failed" : "") + ". This is a heuristic; the first launch is profiled.");
        }
        else Set(6, StepState.Skipped, summary.Sessions == 0 ? "Nothing learned yet." : "Prediction disabled in Settings.");

        // 8. Validate
        Set(7, StepState.Running, null);
        var exePath = exeRel is null ? null : Path.Combine(game.Mode == ExecutionMode.HybridOptimized ? game.LocalRoot! : cloudRoot, exeRel);
        var exeOk = exePath != null && await BoundedFileSystem.Shared.RunAsync(() => PeInspector.TryRead(exePath) != null, TimeSpan.FromSeconds(15), ct);
        double? coverage = null;
        string coverageNote = "";
        if (pf is { PlannedBlocks: > 0 })
        {
            if (_index != null)
            {
                // Verify against rclone's own range index, which it writes once a file's handles have been closed for its
                // handle-caching period. Never trust in-memory bookkeeping for a readiness claim.
                await Task.Delay(Mount.RcloneMountService.HandleCachingWindow + TimeSpan.FromSeconds(1), ct);
                _index.Refresh(mount.Id);
                var covered = pf.Planned.Count(b => _index.IsCached(mount.Id, game.RootPath.Length == 0 ? b.Path : game.RootPath.TrimEnd('/') + "/" + b.Path,
                    b.Index * _engine.BlockSize, Math.Min(_engine.BlockSize, 1)));
                coverage = covered / (double)pf.PlannedBlocks;
                coverageNote = $" {covered} of {pf.PlannedBlocks} planned blocks verified in rclone's cache index.";
            }
            else coverage = (pf.FetchedBlocks + pf.AlreadyCachedBlocks) / (double)pf.PlannedBlocks;
        }
        Set(7, exeOk ? StepState.Done : StepState.Failed, exeOk ? $"Executable header valid ({report.Architecture}).{coverageNote}" : "The executable could not be read.");
        if (!exeOk) return Done(ReadinessStatus.UnsupportedFilesystemOperation, "The game executable could not be read from the drive.", pf, report, net, available, coverage, essentialBytes);

        if (summary.Sessions == 0)
            return Done(ReadinessStatus.MayRequireAdditionalDownloads,
                $"Essential files are cached, but the startup data is unknown until the first launch is profiled. Expect cloud loading waits on first launch " +
                $"(full game would take {Bandwidth.FormatDuration(Bandwidth.EstimateTransfer(scan.TotalBytes, effectiveMbps))} to download at {effectiveMbps:0} Mbps).",
                pf, report, net, available, null, essentialBytes);
        if (pf is { TrimmedForBudget: > 0 } && available < summary.StartupBytesEstimate / 2)
            return Done(ReadinessStatus.InsufficientCacheSpace,
                $"The learned startup set ({ByteSize.Format(summary.StartupBytesEstimate)}) does not fit the available cache ({ByteSize.Format(available)}). Increase the budget or free disk space.",
                pf, report, net, available, coverage, essentialBytes);
        if (coverage >= 0.95 && pf!.TrimmedForBudget == 0)
            return Done(ReadinessStatus.ReadyToLaunch,
                $"Learned startup data cached ({coverage:P0} of {pf.PlannedBlocks} predicted blocks, verified in rclone's cache index). Data outside the learned set will still stream from the cloud.",
                pf, report, net, available, coverage, essentialBytes);
        return Done(ReadinessStatus.MayRequireAdditionalDownloads,
            $"Startup data partially cached ({coverage:P0}). Some loading will wait on the cloud.", pf, report, net, available, coverage, essentialBytes);
    }

    public static IEnumerable<FileStat> EssentialFiles(ScanResult scan, string? exeRel)
    {
        var exeDir = exeRel is null ? "" : (Path.GetDirectoryName(exeRel.Replace('/', '\\'))?.Replace('\\', '/') ?? "");
        return scan.Files
            .Where(f =>
            {
                var dir = Path.GetDirectoryName(f.RelativePath.Replace('/', '\\'))?.Replace('\\', '/') ?? "";
                var ext = Path.GetExtension(f.RelativePath);
                return (dir == exeDir && (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)))
                       || (f.Size <= 256 * 1024 && HybridBuilder.EssentialExtensions.Contains(ext) && f.RelativePath.Count(c => c == '/') <= 2);
            })
            .OrderBy(f => f.RelativePath == exeRel ? 0 : 1).ThenBy(f => f.Size);
    }

    private static async Task WarmFileAsync(string path, MountSession mount, string gameRoot, string rel, CancellationToken ct)
    {
        var mountPath = string.IsNullOrEmpty(gameRoot) ? rel : gameRoot.TrimEnd('/') + "/" + rel;
        using var reg = mount.Attribution.Register(mountPath, 0, long.MaxValue / 4);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buf = new byte[1 << 20];
        while (await fs.ReadAsync(buf, ct) > 0) { }
    }

    private static string Sanitize(string s) => new(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).Take(40).ToArray());
}

/// <summary>
/// Keeps pinned files hot. rclone evicts by least-recent access, so touching a pinned file's first page periodically
/// keeps it at the young end of the LRU. This is best effort: a budget smaller than the pinned set still evicts.
/// </summary>
public sealed class PinKeeper : IDisposable
{
    private readonly Func<IReadOnlyList<(MountSession Mount, string Path)>> _pins;
    private readonly Timer _timer;

    public PinKeeper(Func<IReadOnlyList<(MountSession, string)>> pins, TimeSpan? interval = null)
    {
        _pins = pins;
        var i = interval ?? TimeSpan.FromMinutes(10);
        _timer = new Timer(_ => Touch(), null, i, i);
    }

    public int LastTouched { get; private set; }

    public void Touch()
    {
        int n = 0;
        foreach (var (m, p) in _pins())
        {
            if (m.State != MountState.Mounted) continue;
            try
            {
                using var reg = m.Attribution.Register(p, 0, 4096);
                using var fs = new FileStream(Path.Combine(m.Root, p.Replace('/', '\\')), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
                fs.ReadExactly(new byte[Math.Min(4096, (int)Math.Max(1, fs.Length))]);
                n++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException) { }
        }
        LastTouched = n;
    }

    public void Dispose() => _timer.Dispose();
}
