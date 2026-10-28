using System.Diagnostics;

namespace CloudSSD.Diagnostics;

/// <summary>
/// Step-by-step start-up trace written straight to logs\startup-trace.log, bypassing the queued logger. Added after a
/// window opened right after a Windows restart and hung without writing a single log line (2026-10-20): the next time a
/// start stalls, the last line here shows the step it is stuck in. Small, best effort, never throws.
/// </summary>
public static class StartupTrace
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Mark(string logDirectory, string process, string step)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(logDirectory);
                var file = Path.Combine(logDirectory, "startup-trace.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Move(file, file + ".prev", true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} +{Clock.Elapsed.TotalSeconds,7:0.000}s {process}[{Environment.ProcessId}] {step}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* tracing must never affect start-up */ }
    }
}
