using System.Diagnostics;
using System.Text.Json;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CloudSSD.Mount;

public sealed record MountRequest(
    MountDefinition Definition,
    CacheSettings Cache,
    NetworkSettings Network,
    string CacheDir,
    long EffectiveCacheMaxBytes,
    bool Telemetry,
    string? RemoteType,
    IReadOnlyDictionary<string, string?>? ExtraEnvironment = null);

/// <summary>A running (or recovering) rclone mount.</summary>
public sealed class MountSession
{
    internal MountSession(MountRequest request, char letter)
    {
        Request = request;
        DriveLetter = letter;
    }

    public MountRequest Request { get; internal set; }
    public MountDefinition Definition => Request.Definition;
    public string Id => Request.Definition.Id;
    public char DriveLetter { get; }
    public string Root => $"{DriveLetter}:\\";
    public MountState State { get; internal set; } = MountState.Starting;
    public string? Message { get; internal set; }
    public DateTimeOffset Since { get; internal set; } = DateTimeOffset.Now;
    public int RestartCount { get; internal set; }
    public int? ProcessId => Process is { } p && !SafeHasExited(p) ? p.Id : null;
    public RcClient? Rc { get; internal set; }
    public string LogFile { get; internal set; } = "";
    public string CacheDir => Request.CacheDir;
    public ReadAttributionRegistry Attribution { get; } = new();
    public IReadOnlyList<string> Warnings { get; internal set; } = [];
    public bool Adopted { get; internal set; }
    public int PausedSteamWrites { get; internal set; }

    /// <summary>True while an automatic restart is scheduled (another mount attempt must not race it).</summary>
    public bool RestartScheduled { get; internal set; }

    /// <summary>True once automatic restarts gave up (too many crashes); a supervisor may try again later.</summary>
    public bool RestartsExhausted { get; internal set; }

    public long ProviderErrors;
    public long Retries;
    public long RateLimitEvents;
    public long AuthErrors;
    public long NetworkErrors;
    public DateTime? LastNetworkError;
    public DateTime LastActivity = DateTime.Now;

    internal Process? Process;
    internal LogTailer? Tailer;
    internal RcloneLogParser? Parser;
    internal volatile bool StopRequested;
    internal readonly List<DateTime> RestartTimes = new();
    internal int FailedPings;
    internal string? RcUser;
    internal string? RcPass;

    public MountStatus ToStatus() => new(Id, State, DriveLetter + ":", ProcessId, Message, Since, RestartCount);

    internal static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch (InvalidOperationException) { return true; }
    }
}

internal sealed record PersistedMount(string Id, int Pid, long ProcessStartTicks, int Port, string User, string PassProtected,
    char Letter, string LogFile, string RequestJson, MountState State = MountState.Mounted);

/// <summary>
/// Manages rclone + WinFsp mounts: start, readiness detection, health checks, crash auto-restart with backoff,
/// graceful shutdown that respects pending uploads, and adoption of mounts that survived an application restart.
/// </summary>
public sealed class RcloneMountService : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly RcloneConfigService _config;
    private readonly ILogger _log;
    private readonly Dictionary<string, MountSession> _sessions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BoundedFileSystem _visibility = new(1);
    private int _healthChecking;
    private readonly Timer _health;
    private RcloneInfo? _rclone;

    public RcloneMountService(AppPaths paths, RcloneConfigService config, ILogger<RcloneMountService> log)
    {
        _paths = paths;
        _config = config;
        _log = log;
        _health = new Timer(_ => _ = HealthCheckAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public event Action<MountStatus>? StatusChanged;
    public event Action<AccessEvent>? ReadObserved;
    /// <summary>(mountId, path, offset, present, isPrefetch) for each cache lookup of an in-flight read.</summary>
    public event Action<string, string, long, bool, bool>? LookupObserved;
    public event Action<string, ProviderEvent>? ProviderEventObserved;

    /// <summary>Restart policy: at most this many automatic restarts within <see cref="RestartWindow"/>.</summary>
    public int MaxRestarts { get; set; } = 5;
    public TimeSpan RestartWindow { get; set; } = TimeSpan.FromMinutes(10);

    public void SetRclone(RcloneInfo info) => _rclone = info;
    public RcloneInfo? Rclone => _rclone;

    public IReadOnlyList<MountSession> Sessions
    {
        get { lock (_sessions) return _sessions.Values.ToList(); }
    }

    public MountSession? Get(string id)
    {
        lock (_sessions) return _sessions.GetValueOrDefault(id);
    }

    public async Task<MountSession> MountAsync(MountRequest request, CancellationToken ct = default)
    {
        var rclone = _rclone ?? throw new InvalidOperationException("rclone not found. Check Diagnostics.");
        if (!rclone.IsSupportedVersion)
            throw new InvalidOperationException($"rclone {rclone.Version} is too old; {RcloneInfo.MinimumVersion} or newer is required.");
        if (!WinFspDetector.Detect().Installed)
            throw new InvalidOperationException("WinFsp is not installed. Install it from https://winfsp.dev/rel/ and try again.");

        await _gate.WaitAsync(ct);
        try
        {
            if (Get(request.Definition.Id) is { State: MountState.Stopping })
                throw new InvalidOperationException("This drive is still stopping. Wait for it to finish before mounting again; its cache remains in use.");
            if (Get(request.Definition.Id) is { State: MountState.Mounted or MountState.Degraded or MountState.Starting } existing) return existing;
            var used = DriveLetterAllocator.UsedLetters();
            // A permanent drive keeps its letter: games and shortcuts point at it, so it never silently moves to another
            // letter, and it never displaces the drive that currently holds it.
            if (request.Definition is { Permanent: true, DriveLetter: { Length: > 0 } fixedLetter } && used.Contains(char.ToUpperInvariant(fixedLetter[0])))
                throw new DriveLetterConflictException(char.ToUpperInvariant(fixedLetter[0]));
            var letter = DriveLetterAllocator.Choose(request.Definition.DriveLetter, used)
                         ?? throw new InvalidOperationException("No free drive letter is available.");
            if (request.Definition.DriveLetter is { Length: > 0 } wanted && char.ToUpperInvariant(wanted[0]) != letter)
                _log.LogWarning("Drive letter {Wanted}: is in use; using {Letter}: instead", wanted, letter);

            PrepareCacheDir(request.CacheDir);
            var session = new MountSession(request, letter);
            lock (_sessions) _sessions[request.Definition.Id] = session;
            await StartProcessAsync(session, rclone, ct);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Creates the cache directory and the ownership marker that cache cleanup requires before deleting anything.</summary>
    public static void PrepareCacheDir(string dir)
    {
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, CacheMarker);
        if (!File.Exists(marker)) File.WriteAllText(marker, "This folder is managed by CloudSSD (rclone VFS cache). Safe to delete only when unmounted.\n");
    }

    public const string CacheMarker = ".cloudssd-cache";

    private async Task StartProcessAsync(MountSession s, RcloneInfo rclone, CancellationToken ct)
    {
        var paused = new PausedSteamWrites(s.CacheDir);
        paused.RecoverTransactions();
        s.PausedSteamWrites = paused.All().Count;
        s.State = MountState.Starting;
        s.Since = DateTimeOffset.Now;
        s.StopRequested = false;
        Publish(s, "Starting rclone mount…");

        var port = RcClient.FindFreeLoopbackPort();
        s.RcUser = SecretStore.RandomToken(12);
        s.RcPass = SecretStore.RandomToken(24);
        s.LogFile = Path.Combine(_paths.LogDirectory, $"rclone-{s.Id}.log");
        RotateOldLog(s.LogFile);

        var plan = MountFlagBuilder.Build(new MountPlanInput(s.Definition, s.DriveLetter, s.Request.Cache, s.Request.Network, s.CacheDir,
            s.LogFile, s.Request.EffectiveCacheMaxBytes, s.Request.Telemetry, port, s.Request.RemoteType), rclone);
        s.Warnings = plan.Warnings;
        foreach (var w in plan.Warnings) _log.LogWarning("Mount {Id}: {Warning}", s.Id, w);

        var env = new Dictionary<string, string?>(_config.BaseEnvironment())
        {
            ["RCLONE_RC_USER"] = s.RcUser,
            ["RCLONE_RC_PASS"] = s.RcPass,
        };
        if (s.Request.ExtraEnvironment != null) foreach (var (k, v) in s.Request.ExtraEnvironment) env[k] = v;

        var psi = new RcloneRunner(rclone.Path, env).CreateStartInfo(plan.Arguments);
        // Redirect and drain std streams: rclone logs to its file, and an un-redirected child would inherit (and pin)
        // the parent's console/pipe handles - a mount kept alive after exit must not hold a terminal or test host open.
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        _log.LogInformation("Mount {Id}: {Command}", s.Id, RcloneRunner.DescribeCommand(psi));

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Exited += (_, _) => OnProcessExited(s, proc);
        proc.Start();
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _log.LogDebug("rclone[{Id}] stderr: {Line}", s.Id, Redactor.Redact(e.Data)); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        s.Process = proc;
        s.Rc?.Dispose();
        s.Rc = new RcClient(port, s.RcUser, s.RcPass);
        AttachTelemetry(s, fromEnd: false);
        Persist(); // Cache restoration can take minutes. A crash here must adopt this process, never start a second one.
        Publish(s, "Checking drive startup and saved writes…");

        // Before the drive appears, rclone first finishes files that still have unsaved changes in its cache, downloading
        // their missing parts. On a slow provider (TeraBox Free: ~1 MB/s) that can take far longer than 45 s; killing it
        // then made it start over forever (measured after a restart on 2026-10-20). So the wait is extended for as long
        // as rclone keeps writing to its log (it is working), up to StartupMaxWait; 45 s without any progress still fails.
        var started = DateTime.UtcNow;
        var deadline = started + StartupQuietTimeout;
        long logLength = -1;
        var lastNote = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (s.StopRequested) throw new OperationCanceledException("Mount startup was stopped.", ct);
            long len;
            try { len = new FileInfo(s.LogFile).Length; } catch (IOException) { len = logLength; }
            if (len > logLength && logLength >= 0 && DateTime.UtcNow - started < StartupMaxWait)
            {
                deadline = DateTime.UtcNow + StartupQuietTimeout;
                if (DateTime.UtcNow - lastNote > TimeSpan.FromSeconds(30))
                {
                    lastNote = DateTime.UtcNow;
                    Publish(s, $"rclone is finishing unsaved files from its cache before showing {s.DriveLetter}: ({(DateTime.UtcNow - started).TotalMinutes:0} min so far)…");
                }
            }
            logLength = len;
            if (MountSession.SafeHasExited(proc))
            {
                var tail = SafeTail(s.LogFile, 12);
                s.State = MountState.Failed;
                var reason = RcloneConfigService.ExplainError(tail);
                Publish(s, "rclone exited during startup: " + reason);
                throw new InvalidOperationException($"Mount failed: {reason}");
            }
            if (await s.Rc.PingAsync(ct) && await ProbeRootAsync(s, ct))
            {
                if (s.StopRequested) throw new OperationCanceledException("Mount startup was stopped.", ct);
                s.State = MountState.Mounted;
                s.Since = DateTimeOffset.Now;
                s.FailedPings = 0;
                Persist();
                Publish(s, $"Mounted at {s.DriveLetter}:" + (s.PausedSteamWrites > 0 ? $" · {s.PausedSteamWrites} unfinished Steam writes paused and preserved. Resume them in Cloud Drives." : ""));
                return;
            }
            await Task.Delay(250, ct);
        }
        s.State = MountState.Failed;
        Publish(s, "Timed out waiting for the drive to appear.");
        TryKill(proc);
        throw new TimeoutException($"Drive {s.DriveLetter}: did not appear (no progress for {StartupQuietTimeout.TotalSeconds:0} s). See {s.LogFile}.");
    }

    /// <summary>How long rclone may show no progress at all before a starting mount is considered failed.</summary>
    public static TimeSpan StartupQuietTimeout { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>Longest a starting mount may keep working (log growing) before the drive must appear.</summary>
    public static TimeSpan StartupMaxWait { get; set; } = TimeSpan.FromHours(3);

    private void AttachTelemetry(MountSession s, bool fromEnd)
    {
        s.Tailer?.Dispose();
        s.Parser = new RcloneLogParser(s.Id, (path, off, len, start) => s.Attribution.IsPrefetch(path, off, len, start));
        s.Parser.Read += e => { s.LastActivity = DateTime.Now; ReadObserved?.Invoke(e); };
        s.Parser.Provider += e => OnProviderEvent(s, e);
        s.Parser.Lookup += (path, off, present, pre) => LookupObserved?.Invoke(s.Id, path, off, present, pre);
        var parser = s.Parser;
        s.Tailer = new LogTailer(s.LogFile, parser.ParseLine, fromEnd);
    }

    private void OnProviderEvent(MountSession s, ProviderEvent e)
    {
        switch (e.Kind)
        {
            case ProviderEventKind.Error: Interlocked.Increment(ref s.ProviderErrors); break;
            case ProviderEventKind.Retry: Interlocked.Increment(ref s.Retries); break;
            case ProviderEventKind.RateLimited: Interlocked.Increment(ref s.RateLimitEvents); Interlocked.Increment(ref s.Retries); break;
            case ProviderEventKind.AuthError: Interlocked.Increment(ref s.AuthErrors); Interlocked.Increment(ref s.ProviderErrors); break;
            case ProviderEventKind.NetworkError: Interlocked.Increment(ref s.NetworkErrors); Interlocked.Increment(ref s.ProviderErrors); s.LastNetworkError = e.Timestamp; break;
        }
        if (e.Kind is ProviderEventKind.Error or ProviderEventKind.AuthError or ProviderEventKind.NetworkError or ProviderEventKind.OutOfSpace)
            _log.LogWarning("rclone [{Id}] {Kind}: {Msg}", s.Id, e.Kind, e.Message);
        ProviderEventObserved?.Invoke(s.Id, e);
    }

    private void OnProcessExited(MountSession s, Process proc)
    {
        if (!ReferenceEquals(s.Process, proc)) return;
        int code = -1;
        try { code = proc.ExitCode; } catch (InvalidOperationException) { }
        if (s.StopRequested)
        {
            s.State = MountState.Stopped;
            Publish(s, "Unmounted");
            return;
        }
        var tail = SafeTail(s.LogFile, 20);
        _log.LogError("rclone mount {Id} exited unexpectedly (code {Code}). Last log lines:\n{Tail}", s.Id, code, tail);
        s.State = MountState.Failed;
        if (RcloneConfigService.IsAuthFailure(tail))
        {
            // Measured: an expired Google login was restarted 5 times in a row, failing identically each time.
            s.RestartsExhausted = true;
            Publish(s, RcloneConfigService.ExplainError(tail));
            return;
        }
        Publish(s, $"rclone exited unexpectedly (code {code}). {RcloneConfigService.ExplainError(tail)}");
        _ = AutoRestartAsync(s);
    }

    private async Task AutoRestartAsync(MountSession s)
    {
        var now = DateTime.UtcNow;
        s.RestartTimes.RemoveAll(t => now - t > RestartWindow);
        if (s.RestartTimes.Count >= MaxRestarts || _rclone is null)
        {
            s.RestartsExhausted = true;
            s.RestartScheduled = false;
            Publish(s, $"Automatic restart disabled after {s.RestartTimes.Count} crashes in {RestartWindow.TotalMinutes:0} min. Remount manually after checking Diagnostics.");
            return;
        }
        var delay = TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, s.RestartTimes.Count)));
        s.RestartTimes.Add(now);
        s.RestartCount++;
        s.RestartScheduled = true;
        Publish(s, $"Restarting mount in {delay.TotalSeconds:0} s (attempt {s.RestartCount})…");
        await Task.Delay(delay);
        if (s.StopRequested) { s.RestartScheduled = false; return; }
        // WinFsp releases the drive letter asynchronously after the process dies.
        for (int i = 0; i < 40 && await ProbeRootAsync(s); i++) await Task.Delay(250);
        await _gate.WaitAsync();
        var retry = false;
        try
        {
            await StartProcessAsync(s, _rclone, CancellationToken.None);
            _log.LogInformation("Mount {Id} restarted successfully; cached data was preserved", s.Id);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Automatic restart of mount {Id} failed", s.Id);
            retry = true;
        }
        finally
        {
            if (!retry) s.RestartScheduled = false; // a retry keeps the flag so no other mount attempt slips in between
            _gate.Release();
        }
        if (retry) _ = AutoRestartAsync(s);
    }

    public async Task UnmountAsync(string id, TimeSpan? waitForUploads = null, CancellationToken ct = default)
    {
        var s = Get(id);
        if (s is null) return;
        await EnsureCanUnmountAsync(id, ct);
        var previousState = s.State;
        s.StopRequested = true;
        s.State = MountState.Stopping;
        Publish(s, "Unmounting…");

        if (s.Rc != null && waitForUploads is { } wait)
        {
            var until = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < until)
            {
                try
                {
                    var v = VfsStats.Parse(await s.Rc.CallAsync("vfs/stats", null, ct));
                    if (v.UploadsInProgress + v.UploadsQueued == 0) break;
                    Publish(s, $"Waiting for {v.UploadsInProgress + v.UploadsQueued} pending upload(s)…");
                }
                catch (Exception e) when (e is HttpRequestException or RcException or TaskCanceledException) { break; }
                await Task.Delay(1000, ct);
            }
        }

        if (s.Rc != null) await WaitForIdleAsync(s, ct);

        try { await EnsureCanUnmountAsync(id, ct); }
        catch
        {
            s.StopRequested = false;
            s.State = previousState;
            Publish(s, "Drive kept mounted: close games and pause Steam downloads before trying again.");
            throw;
        }

        if (s.Process is { } p && !MountSession.SafeHasExited(p))
        {
            try { if (s.Rc != null) await s.Rc.CallAsync("core/quit", null, ct, TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is HttpRequestException or RcException or TaskCanceledException) { }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { TryKill(p); }
        }
        for (int i = 0; i < 40 && await ProbeRootAsync(s); i++) await Task.Delay(250, CancellationToken.None);
        s.Tailer?.Dispose();
        s.Rc?.Dispose();
        s.State = MountState.Stopped;
        lock (_sessions) _sessions.Remove(id);
        Persist();
        Publish(s, "Unmounted");
    }

    /// <summary>Keep Steam/game handles valid when a user or installer requests a planned restart.</summary>
    public async Task EnsureCanUnmountAsync(string id, CancellationToken ct = default)
    {
        var session = Get(id);
        if (session?.Process is { } process && MountSession.SafeHasExited(process)) return;
        // Windows and the VFS can retain a handle briefly after the application closes it.
        // Allow that close to settle. Only verified inactive image mappings may outlive the driver handles.
        var elapsed = Stopwatch.StartNew();
        while (await StatsAsync(id, ct) is { OpenFiles: > 0 } stats)
        {
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(10))
            {
                if (session != null && await CanReleaseInactiveImagesAsync(session, stats, ct)) return;
                throw new InvalidOperationException($"{stats.OpenFiles} file(s) are open on {session?.DriveLetter}:. Pause Steam downloads and close running games before repairing, unmounting or updating the drive.");
            }
            await Task.Delay(250, ct);
        }
    }

    /// <summary>Read-only readiness check shared with automatic power actions; unknown status is never considered idle.</summary>
    public async Task<bool> IsReadyForShutdownAsync(string id, CancellationToken ct = default)
    {
        var session = Get(id);
        var stats = await StatsAsync(id, ct);
        if (session is not { State: MountState.Mounted } || stats?.OpenFiles == null ||
            stats.UploadsInProgress + stats.UploadsQueued > 0 || stats.ErroredFiles > 0 || stats.OutOfSpace) return false;
        return stats.OpenFiles == 0 || await CanReleaseInactiveImagesAsync(session, stats, ct);
    }

    private static async Task<bool> CanReleaseInactiveImagesAsync(MountSession session, VfsStats stats, CancellationToken ct)
    {
        if (stats.UploadsInProgress + stats.UploadsQueued > 0 || stats.ErroredFiles > 0 || stats.OutOfSpace ||
            stats.OpenFilePaths is not { Count: > 0 } files || files.Count != stats.OpenFiles) return false;
        try { return await BoundedFileSystem.Shared.RunAsync(() => ClosedImageFiles.CanRelease(session.Root, files), TimeSpan.FromSeconds(5), ct); }
        catch (TimeoutException) { return false; }
    }

    /// <summary>
    /// rclone persists an item's downloaded-range index only when its last handle closes, after the handle-caching
    /// delay (5 s). Quitting earlier keeps the data on disk but loses the index, so the next session re-downloads it.
    /// Measured on rclone 1.75.2; see KNOWN_LIMITATIONS.md. vfs/stats "inUse" stays at 1 on an idle mount, so idleness
    /// is judged from the last read seen in telemetry (or a fixed window when telemetry is off). Bounded to 20 s.
    /// </summary>
    private static async Task WaitForIdleAsync(MountSession s, CancellationToken ct)
    {
        var deadline = DateTime.Now + TimeSpan.FromSeconds(20);
        var unmountRequested = DateTime.Now;
        while (DateTime.Now < deadline)
        {
            var last = s.Request.Telemetry ? s.LastActivity : unmountRequested;
            var remaining = HandleCachingWindow - (DateTime.Now - last);
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : remaining, ct);
        }
    }

    /// <summary>
    /// Drops rclone's cached directory metadata so the next access re-reads size/modtime from the provider. If the
    /// remote file changed, rclone's fingerprint check then discards the stale cached data instead of serving it.
    /// </summary>
    public async Task ForgetAsync(string id, string? relativePath = null, CancellationToken ct = default)
    {
        var s = Get(id) ?? throw new InvalidOperationException("Mount not running");
        if (s.Rc is null) return;
        if (string.IsNullOrEmpty(relativePath))
        {
            // Measured on rclone 1.75.2: after vfs/forget, folders that were already listed were not re-read, so files and
            // folders created in the cloud by other apps stayed invisible. A recursive vfs/refresh re-lists every folder
            // (one listing call per folder; this is a user-triggered action).
            await s.Rc.CallAsync("vfs/refresh", new Dictionary<string, string> { ["recursive"] = "true" }, ct, TimeSpan.FromMinutes(10));
        }
        else await s.Rc.CallAsync("vfs/forget", new Dictionary<string, string> { ["file"] = relativePath.Replace('\\', '/') }, ct);
    }

    /// <summary>Re-lists one folder (and its sub-folders) so files added to the cloud by other programs become visible.</summary>
    public async Task RefreshDirAsync(string id, string relativeDir, CancellationToken ct = default)
    {
        var s = Get(id) ?? throw new InvalidOperationException("Mount not running");
        if (s.Rc is null) return;
        var parts = relativeDir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // rclone can only refresh folders it already knows: re-list each ancestor first (non-recursive), then the target.
        for (int i = 0; i < parts.Length; i++)
        {
            var p = new Dictionary<string, string> { ["recursive"] = "false" };
            if (i > 0) p["dir"] = string.Join('/', parts[..i]);
            await s.Rc.CallAsync("vfs/refresh", p, ct, TimeSpan.FromMinutes(2));
        }
        var last = new Dictionary<string, string> { ["recursive"] = "true" };
        if (parts.Length > 0) last["dir"] = string.Join('/', parts);
        await s.Rc.CallAsync("vfs/refresh", last, ct, TimeSpan.FromMinutes(10));
    }
    /// <summary>Changes rclone's bandwidth limit at runtime (core/bwlimit). 0 = unlimited.</summary>
    public async Task SetBandwidthLimitAsync(string id, double mbps, CancellationToken ct = default)
    {
        var s = Get(id);
        if (s?.Rc is null) return;
        var rate = mbps <= 0 ? "off" : $"{(long)(mbps * 1_000_000 / 8)}B";
        await s.Rc.CallAsync("core/bwlimit", new Dictionary<string, string> { ["rate"] = rate }, ct);
    }

    public static readonly TimeSpan HandleCachingWindow = TimeSpan.FromSeconds(6);

    public async Task UnmountAllAsync(TimeSpan? waitForUploads = null)
    {
        foreach (var s in Sessions) await UnmountAsync(s.Id, waitForUploads);
    }

    /// <summary>Upload queue of a running mount (null if rclone does not answer).</summary>
    public async Task<VfsStats?> StatsAsync(string id, CancellationToken ct = default)
    {
        if (Get(id)?.Rc is not { } rc) return null;
        try { return VfsStats.Parse(await rc.CallAsync("vfs/stats", null, ct, TimeSpan.FromSeconds(5))); }
        catch (Exception e) when (e is HttpRequestException or RcException or TaskCanceledException) { return null; }
    }

    /// <summary>Detaches from running mounts without stopping them (used when the user keeps drives mounted after exit).</summary>
    public void DetachAll()
    {
        foreach (var s in Sessions)
        {
            s.StopRequested = true; // suppress auto-restart in this process
            s.Tailer?.Dispose();
        }
        Persist();
    }

    /// <summary>Re-attaches to mounts left running by a previous CloudSSD instance, or cleans up stale entries.</summary>
    public async Task<IReadOnlyList<MountSession>> AdoptExistingAsync(CancellationToken ct = default)
    {
        var adopted = new List<MountSession>();
        foreach (var pm in LoadPersisted())
        {
            if (Get(pm.Id) is { } known) { adopted.Add(known); continue; }
            Process? proc = null;
            try
            {
                proc = Process.GetProcessById(pm.Pid);
                if (!proc.ProcessName.Equals("rclone", StringComparison.OrdinalIgnoreCase) || proc.StartTime.Ticks != pm.ProcessStartTicks) proc = null;
            }
            catch (ArgumentException) { proc = null; }
            catch (InvalidOperationException) { proc = null; }

            var request = JsonSerializer.Deserialize<MountRequest>(pm.RequestJson, JsonSettingsStore.Options);
            var pass = SecretStore.Unprotect(pm.PassProtected);
            if (proc is null || request is null || pass is null)
            {
                _log.LogInformation("Discarding stale mount record {Id}", pm.Id);
                continue;
            }
            var s = new MountSession(request, pm.Letter) { Adopted = true, LogFile = pm.LogFile, RcUser = pm.User, RcPass = pass };
            s.Rc = new RcClient(pm.Port, pm.User, pass);
            if (!await s.Rc.PingAsync(ct))
            {
                _log.LogWarning("rclone mount {Id} (pid {Pid}) is running but not responding; stopping it so it can be remounted cleanly", pm.Id, pm.Pid);
                TryKill(proc);
                s.Rc.Dispose();
                continue;
            }
            s.Process = proc;
            s.PausedSteamWrites = new PausedSteamWrites(s.CacheDir).All().Count;
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => OnProcessExited(s, proc);
            s.State = pm.State == MountState.Starting ? MountState.Starting : MountState.Mounted;
            lock (_sessions) _sessions[s.Id] = s;
            AttachTelemetry(s, fromEnd: true);
            Publish(s, s.State == MountState.Starting ? $"Re-attached to cache recovery for {s.DriveLetter}:" : $"Re-attached to running mount at {s.DriveLetter}:");
            adopted.Add(s);
        }
        Persist();
        return adopted;
    }

    private async Task<bool> ProbeRootAsync(MountSession s, CancellationToken ct = default)
    {
        try { return await _visibility.RunAsync(() => Directory.Exists(s.Root), TimeSpan.FromSeconds(3), ct); }
        catch (TimeoutException) { return false; }
    }

    private async Task HealthCheckAsync()
    {
        if (Interlocked.Exchange(ref _healthChecking, 1) != 0) return;
        try
        {
        foreach (var s in Sessions)
        {
            if (s.State is not (MountState.Mounted or MountState.Degraded or MountState.Starting) || s.Rc is null) continue;
            var ok = await s.Rc.PingAsync();
            var visible = await ProbeRootAsync(s);
            if (ok && visible)
            {
                s.FailedPings = 0;
                if (s.State is MountState.Degraded or MountState.Starting)
                {
                    s.State = MountState.Mounted;
                    Persist();
                    Publish(s, "Mount healthy again");
                }
            }
            else if (++s.FailedPings >= 3 && s.State == MountState.Mounted)
            {
                s.State = MountState.Degraded;
                Publish(s, ok ? $"Drive {s.DriveLetter}: is not visible although rclone is running" : "rclone is not responding");
            }
        }
        }
        finally { Volatile.Write(ref _healthChecking, 0); }
    }

    /// <summary>Called after Windows resumes from sleep: verifies every mount and restarts dead ones.</summary>
    public async Task OnSystemResumeAsync()
    {
        _log.LogInformation("System resumed; verifying mounts");
        await Task.Delay(TimeSpan.FromSeconds(3));
        foreach (var s in Sessions)
        {
            if (s.Process is { } p && MountSession.SafeHasExited(p) && !s.StopRequested) _ = AutoRestartAsync(s);
        }
        await HealthCheckAsync();
    }

    private void Persist()
    {
        try
        {
            var list = Sessions.Where(s => s.State is MountState.Mounted or MountState.Degraded or MountState.Starting && s.Process is { } p && !MountSession.SafeHasExited(p) && s.Rc != null)
                .Select(s => new PersistedMount(s.Id, s.Process!.Id, s.Process.StartTime.Ticks, s.Rc!.Port, s.RcUser!, SecretStore.Protect(s.RcPass!),
                    s.DriveLetter, s.LogFile, JsonSerializer.Serialize(s.Request, JsonSettingsStore.Options), s.State))
                .ToList();
            var tmp = _paths.MountStateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonSettingsStore.Options));
            File.Move(tmp, _paths.MountStateFile, true);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not persist mount state");
        }
    }

    private List<PersistedMount> LoadPersisted()
    {
        try
        {
            return File.Exists(_paths.MountStateFile)
                ? JsonSerializer.Deserialize<List<PersistedMount>>(File.ReadAllText(_paths.MountStateFile), JsonSettingsStore.Options) ?? []
                : [];
        }
        catch (JsonException) { return []; }
    }

    private void Publish(MountSession s, string message)
    {
        s.Message = message;
        StatusChanged?.Invoke(s.ToStatus());
    }

    private static void RotateOldLog(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, path + ".prev", true);
        }
        catch (IOException) { /* still held by a dying process; rclone will append */ }
    }

    public static string SafeTail(string path, int lines)
    {
        try { return File.Exists(path) ? string.Join(Environment.NewLine, DiagnosticReport.ReadTail(path, lines)) : ""; }
        catch (IOException) { return ""; }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _health.DisposeAsync();
        foreach (var s in Sessions) s.Tailer?.Dispose();
    }
}
