using CloudSSD.Core;

namespace CloudSSD.Games;

/// <summary>
/// Turns a folder scan into an honest compatibility report. It flags conditions known to break or endanger
/// cloud-drive execution (installers, kernel anti-cheat, launcher ownership checks, missing runtimes) and never marks a
/// game "Verified" — only a successful monitored launch does that.
/// </summary>
public static class CompatibilityAnalyzer
{
    public static CompatibilityReport Analyze(ScanResult scan, string? chosenExe, double mbps, long? startupBytes, int profileSessions,
        IReadOnlyCollection<ExecutionMode>? verifiedModes = null)
    {
        var r = new CompatibilityReport
        {
            TotalBytes = scan.TotalBytes,
            FileCount = scan.FileCount,
            Engine = scan.Engine,
            Markers = scan.Markers.ToList(),
            EstimateMbps = mbps,
            FullDownloadEstimate = Bandwidth.EstimateTransfer(scan.TotalBytes, mbps),
            StartupBytesEstimate = startupBytes,
            ProfileSessions = profileSessions,
        };
        var exe = chosenExe ?? scan.Executables.FirstOrDefault()?.RelativePath;
        r.Executable = exe;
        var level = CompatibilityLevel.NoBlockersFound;
        void Raise(CompatibilityLevel l) { if (l > level) level = l; }

        var anticheat = scan.Markers.Where(m => m.StartsWith("anticheat:")).Select(m => m[10..]).ToList();
        var launchers = scan.Markers.Where(m => m.StartsWith("launcher:")).Select(m => m[9..]).ToList();

        if (exe is null)
        {
            if (scan.Markers.Contains("installer"))
            {
                r.Issues.Add(new Issue(Severity.Error, "installer-only",
                    "This folder contains an installer, not an installed game.",
                    "Run the installer on a local drive (Full Local). Installers write registry entries and system components that cannot be streamed."));
                Raise(CompatibilityLevel.NeedsLocalInstall);
            }
            else
            {
                r.Issues.Add(new Issue(Severity.Error, "no-executable", "No game executable was found in this folder.",
                    "Choose the folder that contains the game's .exe, or pick the executable manually."));
                Raise(CompatibilityLevel.Incompatible);
            }
        }

        if (anticheat.Count > 0)
        {
            r.Issues.Add(new Issue(Severity.Error, "anti-cheat",
                $"Anti-cheat detected: {string.Join(", ", anticheat)}. Kernel anti-cheat systems may refuse to run from a virtual (WinFsp) drive or flag modified paths.",
                "Install the game locally through its official launcher. CloudSSD does not bypass or modify anti-cheat."));
            Raise(CompatibilityLevel.NeedsLocalInstall);
        }

        if (launchers.Contains("Steam"))
        {
            r.Issues.Add(new Issue(Severity.Warning, "steam",
                "Uses Steamworks. Starting the EXE directly usually hands off to the Steam client, which expects the game in a Steam library folder and checks ownership.",
                "Keep Steam running and signed in. If the game relaunches through Steam or fails, install it through Steam (Full Local). Ownership checks are never bypassed."));
            Raise(CompatibilityLevel.Uncertain);
        }
        if (launchers.Contains("EpicOnlineServices"))
        {
            r.Issues.Add(new Issue(Severity.Warning, "epic",
                "Uses Epic Online Services; many such games require the Epic Games Launcher to start them with an authentication token.",
                "Launch through the Epic Games Launcher with a local install if direct launch fails."));
            Raise(CompatibilityLevel.Uncertain);
        }
        if (launchers.Contains("Ubisoft"))
        {
            r.Issues.Add(new Issue(Severity.Warning, "ubisoft", "Requires Ubisoft Connect to start.", "Install through Ubisoft Connect."));
            Raise(CompatibilityLevel.NeedsLocalInstall);
        }

        if (scan.Executables.FirstOrDefault(e => e.RelativePath == exe) is { } cand && cand.Machine == PeMachine.Unknown)
        {
            r.Issues.Add(new Issue(Severity.Warning, "pe-unreadable", $"Could not read the executable header of {exe}.", "Check that the file is intact and readable."));
            Raise(CompatibilityLevel.Uncertain);
        }

        if (scan.FileCount > 50_000)
        {
            r.Issues.Add(new Issue(Severity.Warning, "many-files",
                $"{scan.FileCount:N0} files. Each first access needs a cloud metadata round-trip, which can slow loading.",
                "Hybrid Optimized mode keeps small files on the SSD."));
            Raise(CompatibilityLevel.Uncertain);
        }
        if (scan.Files.Any(f => f.RelativePath.Length > 240))
            r.Issues.Add(new Issue(Severity.Warning, "long-paths", "Some paths are close to the Windows 260-character limit.", "Mount the game folder closer to the drive root."));
        if (scan.Truncated)
            r.Issues.Add(new Issue(Severity.Warning, "scan-truncated", "The folder is very large; the scan stopped early. Size estimates are incomplete."));

        if (scan.Markers.Contains("packed-archives") || scan.Engine is "Unreal Engine" or "RAGE" or "REDengine")
            r.Issues.Add(new Issue(Severity.Info, "packed-archives",
                "Assets are packed in large archives. Streaming works at byte-range level, but loads may touch many scattered ranges; expect waits on first loads.",
                "Use Optimize & Prepare after a first profiling launch."));

        if (startupBytes is { } sb)
            r.Issues.Add(new Issue(Severity.Info, "startup-estimate",
                $"Learned startup data ≈ {ByteSize.Format(sb)} from {profileSessions} launch(es) ≈ {Bandwidth.FormatDuration(Bandwidth.EstimateTransfer(sb, mbps))} at {mbps:0} Mbps if not cached."));
        else
            r.Issues.Add(new Issue(Severity.Info, "no-profile",
                "No launch profile yet: the data needed at startup is unknown. The first launch will profile it and may wait on cloud downloads."));

        r.Issues.Add(new Issue(Severity.Info, "full-download",
            $"Full download of {ByteSize.Format(scan.TotalBytes)} would take about {Bandwidth.FormatDuration(r.FullDownloadEstimate!.Value)} at {mbps:0} Mbps ({Bandwidth.BytesPerSecondToMBps(Bandwidth.MbpsToBytesPerSecond(mbps)):0.0} MB/s)."));

        r.RecommendedMode = level switch
        {
            CompatibilityLevel.NeedsLocalInstall => ExecutionMode.FullLocal,
            CompatibilityLevel.Uncertain => ExecutionMode.HybridOptimized,
            _ => scan.Markers.Contains("packed-archives") || scan.FileCount > 2000 ? ExecutionMode.HybridOptimized : ExecutionMode.CloudDirect
        };
        if (verifiedModes is { Count: > 0 } && level < CompatibilityLevel.NeedsLocalInstall)
        {
            level = CompatibilityLevel.Verified;
            r.RecommendedMode = verifiedModes.First();
        }
        r.Level = level;
        return r;
    }

    public static void AddDependencyIssues(CompatibilityReport r, IReadOnlyList<DependencyIssue> deps)
    {
        r.MissingDependencies = deps.Where(d => d.Dll != "(.NET)").ToList();
        foreach (var d in r.MissingDependencies)
            r.Issues.Add(new Issue(Severity.Warning, "missing-dll", $"{d.Dll}: {d.Problem}", d.Remedy));
        if (r.MissingDependencies.Count > 0 && r.Level is CompatibilityLevel.NoBlockersFound or CompatibilityLevel.Verified)
            r.Level = CompatibilityLevel.Uncertain;
    }
}
