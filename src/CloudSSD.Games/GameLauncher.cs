using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Text.Json;
using CloudSSD.Core;
using Microsoft.Win32.SafeHandles;

namespace CloudSSD.Games;

/// <summary>Tracks a process tree with a Windows Job Object (no kill-on-close: closing CloudSSD never kills the game).</summary>
public sealed class ProcessTreeTracker : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attrs, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out JobBasicAccounting info, int length, IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    private readonly SafeFileHandle _job;

    public ProcessTreeTracker(Process root)
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        Assigned = AssignProcessToJobObject(_job, root.Handle);
    }

    /// <summary>False if the process could not be placed in the job (e.g. it already belongs to an incompatible job).</summary>
    public bool Assigned { get; }

    public (int Active, int Total) Counts()
    {
        if (!Assigned) return (0, 0);
        return QueryInformationJobObject(_job, 1, out var info, Marshal.SizeOf<JobBasicAccounting>(), IntPtr.Zero)
            ? ((int)info.ActiveProcesses, (int)info.TotalProcesses)
            : (0, 0);
    }

    public void Dispose() => _job.Dispose();
}

public enum LaunchState { NotStarted, Starting, Running, Exited, Failed }

public sealed record LaunchDiagnostics(
    string GameId, string Executable, string WorkingDirectory, ExecutionMode Mode, DateTime Started, DateTime? Ended, int? ExitCode,
    string? ExitMeaning, int ProcessesSeen, TimeSpan Runtime, bool LikelyFailure, IReadOnlyList<string> Notes, IReadOnlyList<string> EventLogEntries);

/// <summary>A launched game, monitored until its whole process tree has exited.</summary>
public sealed class LaunchSession
{
    internal LaunchSession(GameEntry game, Process process, ProcessTreeTracker? tracker, string exe, string workDir, ExecutionMode mode)
    {
        Game = game;
        Process = process;
        Tracker = tracker;
        Executable = exe;
        WorkingDirectory = workDir;
        Mode = mode;
        Started = DateTime.Now;
    }

    public GameEntry Game { get; }
    public Process Process { get; }
    internal ProcessTreeTracker? Tracker { get; }
    public string Executable { get; }
    public string WorkingDirectory { get; }
    public ExecutionMode Mode { get; }
    public DateTime Started { get; }
    public LaunchState State { get; internal set; } = LaunchState.Starting;
    public LaunchDiagnostics? Result { get; internal set; }
    public Task<LaunchDiagnostics> Completion { get; internal set; } = Task.FromResult<LaunchDiagnostics>(null!);

    public string StateText
    {
        get
        {
            if (State == LaunchState.Starting) return "Starting — waiting for the game window";
            if (State != LaunchState.Running) return State.ToString();
            var (active, total) = Tracker?.Counts() ?? (Process.HasExited ? 0 : 1, 1);
            return $"Running ({active} process{(active == 1 ? "" : "es")}, {Bandwidth.FormatDuration(DateTime.Now - Started)})";
        }
    }
}

/// <summary>Starts games in the chosen execution mode and monitors them. It never modifies game executables.</summary>
public sealed class GameLauncher
{
    private readonly string _diagnosticsDir;

    public GameLauncher(string diagnosticsDir) => _diagnosticsDir = diagnosticsDir;

    public event Action<LaunchSession>? Started;
    public event Action<LaunchSession, LaunchDiagnostics>? Ended;

    /// <summary>A process that exits within this window with a non-zero code counts as a failed launch.</summary>
    public TimeSpan EarlyExitWindow { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan SteamStartTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public async Task<LaunchSession> LaunchAsync(GameEntry g, string? mountRoot, CancellationToken ct = default)
    {
        var root = ResolveRoot(g, mountRoot);
        var id = g.SteamAppId ?? await BoundedFileSystem.Shared.RunAsync(() => SteamLaunch.DetectAppId(root), TimeSpan.FromSeconds(10), ct);
        if (g.Executable == null) throw new InvalidOperationException("No executable configured.");
        var exe = Path.GetFullPath(Path.Combine(root, g.Executable.Replace('/', '\\')));
        if (!await BoundedFileSystem.Shared.RunAsync(() => File.Exists(exe), TimeSpan.FromSeconds(5), ct)) throw new FileNotFoundException("The selected game executable is missing.", exe);
        if (id == null) return StartDirect(g, root, exe, ct);
        g.SteamAppId = id;
        var p = await SteamLaunch.StartAsync(id.Value, exe, g.Arguments, SteamStartTimeout, ct);
        var workDir = string.IsNullOrWhiteSpace(g.WorkingDirectory) ? Path.GetDirectoryName(exe)! : Path.Combine(root, g.WorkingDirectory);
        return Track(g, p, exe, workDir);
    }

    public static string ResolveRoot(GameEntry g, string? mountRoot) => g.Mode switch
    {
        ExecutionMode.CloudDirect => mountRoot is null ? throw new InvalidOperationException("Cloud drive is not mounted.")
            : Path.Combine(mountRoot, g.RootPath.Replace('/', '\\')),
        _ => g.LocalRoot ?? throw new InvalidOperationException($"{g.Mode} mode has no local folder yet. Run Optimize & Prepare / Install first.")
    };

    public LaunchSession Launch(GameEntry g, string? mountRoot, CancellationToken ct = default)
    {
        if (g.Executable is null) throw new InvalidOperationException("No executable configured.");
        var root = ResolveRoot(g, mountRoot);
        var exe = Path.Combine(root, g.Executable.Replace('/', '\\'));
        if (!File.Exists(exe)) throw new FileNotFoundException($"Executable not found: {exe}");
        return StartDirect(g, root, exe, ct);
    }

    private LaunchSession StartDirect(GameEntry g, string root, string exe, CancellationToken ct)
    {
        var workDir = string.IsNullOrWhiteSpace(g.WorkingDirectory) ? Path.GetDirectoryName(exe)! : Path.Combine(root, g.WorkingDirectory.Replace('/', '\\'));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = workDir,
            Arguments = g.Arguments ?? "",
        };
        ct.ThrowIfCancellationRequested();
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        return Track(g, p, exe, workDir);
    }

    private LaunchSession Track(GameEntry g, Process p, string exe, string workDir)
    {
        ProcessTreeTracker? tracker = null;
        try { tracker = new ProcessTreeTracker(p); } catch (Win32Exception) { }
        var session = new LaunchSession(g, p, tracker, exe, workDir, g.Mode);
        session.Completion = MonitorAsync(session);
        Started?.Invoke(session);
        return session;
    }

    private async Task<LaunchDiagnostics> MonitorAsync(LaunchSession s)
    {
        var notes = new List<string>();
        int? exitCode = null;
        while (!s.Process.HasExited)
        {
            s.Process.Refresh();
            if (s.Process.MainWindowHandle != IntPtr.Zero) { s.State = LaunchState.Running; break; }
            await Task.Delay(250);
        }
        await s.Process.WaitForExitAsync();
        try { exitCode = s.Process.ExitCode; } catch (InvalidOperationException) { }
        var rootExit = DateTime.Now;
        int maxSeen = 1;
        // Launchers often start the real game and exit; keep monitoring while the tree is alive.
        if (s.Tracker is { Assigned: true })
        {
            while (true)
            {
                var (active, total) = s.Tracker.Counts();
                maxSeen = Math.Max(maxSeen, total);
                if (active == 0) break;
                await Task.Delay(1000);
            }
            if (maxSeen > 1) notes.Add($"The game started {maxSeen - 1} child process(es); monitored until all exited.");
        }
        else notes.Add("Child processes could not be tracked (job object unavailable); only the main process was monitored.");

        var end = DateTime.Now;
        var runtime = end - s.Started;
        var meaning = exitCode is { } c ? ExplainExitCode(c) : null;
        var early = (rootExit - s.Started) < EarlyExitWindow && maxSeen <= 1;
        var likelyFailure = early && exitCode is not null and not 0;
        if (likelyFailure) notes.Add($"Exited after {runtime.TotalSeconds:0.0} s with code 0x{exitCode:X8}{(meaning != null ? " (" + meaning + ")" : "")}.");
        if (early && exitCode == 0) notes.Add("Exited quickly with code 0 — it may have handed off to a launcher (e.g. Steam) or shown a dialog.");
        var events = ReadApplicationEvents(Path.GetFileName(s.Executable), s.Started.AddSeconds(-5), end.AddSeconds(5));
        if (events.Count > 0) notes.Add($"{events.Count} Windows Application event(s) reference this executable.");

        var diag = new LaunchDiagnostics(s.Game.Id, s.Executable, s.WorkingDirectory, s.Mode, s.Started, end, exitCode, meaning, maxSeen, runtime,
            likelyFailure || events.Any(e => e.Contains("Application Error", StringComparison.OrdinalIgnoreCase)), notes, events);
        s.Result = diag;
        s.State = diag.LikelyFailure ? LaunchState.Failed : LaunchState.Exited;
        try
        {
            Directory.CreateDirectory(_diagnosticsDir);
            var file = Path.Combine(_diagnosticsDir, $"launch-{s.Game.Id}-{s.Started:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(diag, JsonSettingsStore.Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        s.Tracker?.Dispose();
        s.Process.Dispose();
        Ended?.Invoke(s, diag);
        return diag;
    }

    public static string? ExplainExitCode(int code) => unchecked((uint)code) switch
    {
        0xC0000135 => "STATUS_DLL_NOT_FOUND: a required DLL is missing (install the listed redistributables)",
        0xC000007B => "STATUS_INVALID_IMAGE_FORMAT: 32/64-bit DLL mismatch or corrupt file",
        0xC0000005 => "STATUS_ACCESS_VIOLATION: the game crashed",
        0xC0000142 => "STATUS_DLL_INIT_FAILED: a DLL failed to initialise",
        0xC0000409 => "STATUS_STACK_BUFFER_OVERRUN / fail-fast: the game aborted",
        0xC000001D => "STATUS_ILLEGAL_INSTRUCTION: CPU feature not supported (e.g. AVX)",
        0xC0000022 => "STATUS_ACCESS_DENIED",
        0xC00000FD => "STATUS_STACK_OVERFLOW",
        0xE0434352 => ".NET unhandled exception",
        0xC0000034 => "STATUS_OBJECT_NAME_NOT_FOUND: a file the game needs was not found",
        0xC000009C or 0xC0000185 or 0xC000009D => "Device I/O error: data could not be read (cloud fetch failed or connection lost)",
        _ => null
    };

    private static List<string> ReadApplicationEvents(string exeName, DateTime from, DateTime to)
    {
        var list = new List<string>();
        try
        {
            var ms = (long)Math.Max(1000, (DateTime.Now - from).TotalMilliseconds);
            var q = new EventLogQuery("Application", PathType.LogName,
                $"*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
            using var reader = new EventLogReader(q);
            for (EventRecord? e = reader.ReadEvent(); e != null && list.Count < 20; e = reader.ReadEvent())
            {
                using (e)
                {
                    if (e.TimeCreated is { } t && (t < from || t > to)) continue;
                    string? msg = null;
                    try { msg = e.FormatDescription(); } catch (EventLogException) { }
                    var props = string.Join(" ", e.Properties.Select(p => p.Value?.ToString()));
                    if ((msg ?? props).Contains(exeName, StringComparison.OrdinalIgnoreCase))
                        list.Add($"{e.TimeCreated:HH:mm:ss} {e.ProviderName} ({e.Id}): {Truncate(msg ?? props, 600)}");
                }
            }
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException) { }
        return list;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

public sealed record NetworkProbeResult(bool Success, double? Mbps, double? FirstByteMs, long Bytes, string? Error);

/// <summary>
/// Measures real provider throughput by reading a byte range of a cloud file with "rclone cat" (bypasses the mount,
/// does not pollute the cache). Reports megabits per second and time to first byte.
/// </summary>
public static class NetworkProbe
{
    public static async Task<NetworkProbeResult> MeasureAsync(Mount.RcloneRunner runner, MountDefinition mount, string remoteFilePath, long offset,
        long count, CancellationToken ct)
    {
        var spec = mount.RemoteSpec + (mount.RemoteSpec.EndsWith(':') ? "" : "/") + remoteFilePath.TrimStart('/');
        var args = new List<string> { "cat", spec, "--offset", offset.ToString(), "--count", count.ToString() };
        // Measure the actual provider, even when the bridge can serve this game from a verified local source.
        args.AddRange(["--header-download", "X-CloudSSD-Cloud-Only: 1"]);
        foreach (var (k, v) in mount.ExtraFlags)
        {
            if (Mount.MountFlagBuilder.IsForbiddenExtraFlag(k)) continue;
            args.Add("--" + k.TrimStart('-'));
            if (!string.IsNullOrEmpty(v)) args.Add(v);
        }
        var psi = runner.CreateStartInfo(args);
        psi.StandardOutputEncoding = null;
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        p.BeginErrorReadLine();
        var stderr = new System.Text.StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        var stream = p.StandardOutput.BaseStream;
        var buf = new byte[256 * 1024];
        long total = 0;
        double? ttfb = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf, cts.Token)) > 0)
            {
                ttfb ??= sw.Elapsed.TotalMilliseconds;
                total += n;
            }
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
        }
        var elapsed = sw.Elapsed.TotalSeconds;
        if (total == 0)
            return new NetworkProbeResult(false, null, null, 0, Mount.RcloneConfigService.ExplainError(stderr.ToString()));
        var transferSeconds = Math.Max(0.001, elapsed - (ttfb ?? 0) / 1000.0);
        return new NetworkProbeResult(true, Bandwidth.BytesPerSecondToMbps(total / transferSeconds), ttfb, total, null);
    }
}
