using CloudSSD.Core;

namespace CloudSSD.Games;

public sealed class GameEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string MountId { get; set; } = "";
    /// <summary>Game folder relative to the mount root, forward slashes ("" = mount root).</summary>
    public string RootPath { get; set; } = "";
    /// <summary>Executable relative to the game folder.</summary>
    public string? Executable { get; set; }
    public string Arguments { get; set; } = "";
    /// <summary>Optional Steam app ID; detected from the installed library manifest when absent.</summary>
    public uint? SteamAppId { get; set; }
    /// <summary>Working directory relative to the game folder (null = executable's folder).</summary>
    public string? WorkingDirectory { get; set; }
    public ExecutionMode Mode { get; set; } = ExecutionMode.CloudDirect;
    /// <summary>Local folder for Hybrid or Full Local modes.</summary>
    public string? LocalRoot { get; set; }
    public CompatibilityReport? Compatibility { get; set; }
    public List<ExecutionMode> VerifiedModes { get; set; } = new();
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? LastPlayed { get; set; }
    public ReadinessStatus Readiness { get; set; } = ReadinessStatus.Unknown;
    public string? ReadinessDetail { get; set; }
    public string? LastLaunchResult { get; set; }
    /// <summary>Listen for authorized game telemetry (Game-Aware Mode). Off unless the user enables it for this title.</summary>
    public bool GameAwareEnabled { get; set; }
}

public sealed record FileStat(string RelativePath, long Size, DateTime ModifiedUtc);

public sealed record ExecutableCandidate(string RelativePath, long Size, int Score, PeMachine Machine, PeSubsystem Subsystem, string Reason);

public sealed record ScanResult(
    string Root,
    long TotalBytes,
    int FileCount,
    int DirectoryCount,
    IReadOnlyList<FileStat> Files,
    IReadOnlyList<ExecutableCandidate> Executables,
    IReadOnlyList<string> Markers,
    string? Engine,
    bool Truncated,
    TimeSpan Elapsed);

public sealed class CompatibilityReport
{
    public CompatibilityLevel Level { get; set; } = CompatibilityLevel.NotAnalyzed;
    public ExecutionMode RecommendedMode { get; set; } = ExecutionMode.CloudDirect;
    public List<Issue> Issues { get; set; } = new();
    public string? Engine { get; set; }
    public List<string> Markers { get; set; } = new();
    public List<DependencyIssue> MissingDependencies { get; set; } = new();
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    public string? Executable { get; set; }
    public string? Architecture { get; set; }
    public TimeSpan? FullDownloadEstimate { get; set; }
    public double EstimateMbps { get; set; }
    public long? StartupBytesEstimate { get; set; }
    public int ProfileSessions { get; set; }
    public DateTime AnalyzedAt { get; set; } = DateTime.Now;

    public string Summary => Level switch
    {
        CompatibilityLevel.Verified => "Verified on this PC",
        CompatibilityLevel.NoBlockersFound => "No blockers found (not yet verified by a launch)",
        CompatibilityLevel.Uncertain => "Uncertain — may not work from the cloud drive",
        CompatibilityLevel.NeedsLocalInstall => "Requires local installation",
        CompatibilityLevel.Incompatible => "Not supported",
        _ => "Not analyzed"
    };
}
