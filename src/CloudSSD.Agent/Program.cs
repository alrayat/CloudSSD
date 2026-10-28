using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CloudSSD.App.Persistence;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CloudSSD.Agent;

/// <summary>
/// CloudSSD.Agent.exe — keeps "Keep Cloud SSD Permanent" drives available without the desktop window.
///
/// Default mode is a small supervisor (started at logon from HKCU\...\Run, or by the desktop app when its window
/// closes). It runs the worker (<c>--worker</c>) as a child process and restarts it with bounded backoff if it crashes;
/// the worker re-attaches to the drives, which keep running in the meantime. A clean worker exit (drives handed to the
/// desktop app, stopped, or nothing to do) ends the supervisor too. One supervisor per user and data folder.
/// </summary>
public static class Program
{
    private const int MaxQuickCrashes = 6;

    public static async Task<int> Main(string[] args)
    {
        var paths = new AppPaths(Opt(args, "--home"));
        if (int.TryParse(Opt(args, "--watch-desktop"), out var desktopPid) &&
            long.TryParse(Opt(args, "--started-ticks"), out var desktopTicks))
        {
            try
            {
                using var desktop = Process.GetProcessById(desktopPid);
                if (desktop.StartTime.ToUniversalTime().Ticks == desktopTicks) await desktop.WaitForExitAsync();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
            await Task.Delay(1500);
            return await SuperviseAsync(paths, false);
        }
        StartupTrace.Mark(paths.LogDirectory, args.Contains("--worker") ? "agent-worker" : "agent", "started " + string.Join(' ', args.Where(a => a.StartsWith("--") && a != "--home")));
        if (args.Contains("--worker"))
        {
            // A crash must leave evidence (the supervisor only sees the exit code).
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try
                {
                    File.AppendAllText(Path.Combine(paths.LogDirectory, $"agent-crash-{DateTime.Now:yyyyMMdd}.log"),
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid {Environment.ProcessId}: {Redactor.Redact(e.ExceptionObject.ToString() ?? "")}{Environment.NewLine}");
                }
                catch (IOException) { }
            };
            return await new AgentWorker(paths).RunAsync();
        }
        return await SuperviseAsync(paths, args.Contains("--logon"));
    }

    private static async Task<int> SuperviseAsync(AppPaths paths, bool atLogon)
    {
        var root = Path.GetFullPath(paths.DataRoot).TrimEnd('\\').ToLowerInvariant();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];
        using var single = new Mutex(true, @"Local\CloudSSD.Agent." + id, out var created);
        if (!created) return 0;

        using var lf = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information)
            .AddProvider(new AppLoggerProvider(paths.LogDirectory, new MemoryLogSink(), LogLevel.Information, "agent-supervisor")));
        var log = lf.CreateLogger("AgentSupervisor");
        log.LogInformation("Agent supervisor started{How} (pid {Pid})", atLogon ? " at Windows logon" : "", Environment.ProcessId);

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CloudSSD.Agent.exe");
        var backoff = TimeSpan.FromSeconds(2);
        var quickCrashes = 0;
        while (true)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "--worker", "--home", paths.DataRoot }) psi.ArgumentList.Add(a);
            var started = DateTime.UtcNow;
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            var code = p.ExitCode;
            if (code is AgentWorker.ExitOk or AgentWorker.ExitLockBusy)
            {
                log.LogInformation(code == AgentWorker.ExitOk ? "Agent finished" : "The CloudSSD window owns the drives; agent not needed");
                return 0;
            }
            // A crash: the drives (rclone, TeraBox bridge) are separate processes and keep running; restart the worker so
            // it re-attaches. Backoff is bounded and resets after a healthy hour. A worker that keeps dying right after
            // start is a deterministic fault, not a transient one: give up instead of looping forever (see agent-crash log).
            if (DateTime.UtcNow - started > TimeSpan.FromHours(1)) { backoff = TimeSpan.FromSeconds(2); quickCrashes = 0; }
            if (DateTime.UtcNow - started < TimeSpan.FromMinutes(2) && ++quickCrashes >= MaxQuickCrashes)
            {
                log.LogError("Agent worker crashed {Count} times right after starting (code {Code}); giving up. See agent-crash-*.log. " +
                             "Drives that are still mounted stay mounted but are not supervised.", quickCrashes, code);
                return 1;
            }
            log.LogWarning("Agent worker exited unexpectedly (code {Code}); restarting in {Delay:0} s", code, backoff.TotalSeconds);
            await Task.Delay(backoff);
            backoff = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(5).Ticks, backoff.Ticks * 2));
        }
    }

    private static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
