namespace CloudSSD.Core;

/// <summary>Origin of a read observed on the virtual drive.</summary>
public enum ReadSource { Application, Prefetch }

/// <summary>
/// One read served by the virtual filesystem, reconstructed from rclone VFS debug telemetry.
/// <see cref="CacheHit"/> is rclone's own answer to "was the range already present in the local cache".
/// </summary>
public sealed record AccessEvent(
    DateTime Timestamp,
    string MountId,
    string Path,
    long Offset,
    int Length,
    bool? CacheHit,
    double? WaitMs,
    ReadSource Source,
    string? Error = null);

/// <summary>A fixed-size block of a file, the unit of prediction and prefetch.</summary>
public readonly record struct BlockKey(string Path, long Index)
{
    public long Offset(long blockSize) => Index * blockSize;
    public override string ToString() => $"{Path}#{Index}";
}

public static class BlockMath
{
    public static IEnumerable<long> BlocksFor(long offset, long length, long blockSize)
    {
        if (length <= 0) yield break;
        var first = offset / blockSize;
        var last = (offset + length - 1) / blockSize;
        for (var b = first; b <= last; b++) yield return b;
    }
}

public enum MountState { Stopped, Starting, Mounted, Degraded, Stopping, Failed }

public sealed record MountStatus(
    string MountId,
    MountState State,
    string? DriveLetter,
    int? ProcessId,
    string? Message,
    DateTimeOffset Since,
    int RestartCount);

/// <summary>
/// A metrics snapshot. Every field is nullable: null means "Unavailable" and must be displayed as such,
/// never substituted with a guess.
/// </summary>
public sealed record EngineMetrics
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public double? DownloadBytesPerSec { get; init; }
    public double? UploadBytesPerSec { get; init; }
    public int? ActiveTransfers { get; init; }
    public long? TotalBytesTransferred { get; init; }
    public long? CacheBytesOnDisk { get; init; }
    public long? CacheLogicalBytes { get; init; }
    public long? CacheVolumeFreeBytes { get; init; }
    public long? BytesServedToApps { get; init; }
    public long? CacheHits { get; init; }
    public long? CacheMisses { get; init; }
    public double? AverageReadWaitMs { get; init; }
    public double? LongestReadStallMs { get; init; }
    public double? PrefetchAccuracy { get; init; }
    public long? PrefetchUsefulBytes { get; init; }
    public long? PrefetchWastedBytes { get; init; }
    public long? ProviderErrors { get; init; }
    public long? ProviderRetries { get; init; }
    public int? UploadsPending { get; init; }
    public int? PrefetchQueueLength { get; init; }
    public string? GameProcessState { get; init; }

    public double? HitRate => CacheHits is { } h && CacheMisses is { } m && h + m > 0 ? h / (double)(h + m) : null;
}

public enum Severity { Info, Warning, Error }

public sealed record Issue(Severity Severity, string Code, string Message, string? Suggestion = null);

public enum ReadinessStatus
{
    Unknown,
    ReadyToLaunch,
    PreparingStartupData,
    MayRequireAdditionalDownloads,
    InsufficientCacheSpace,
    UnsupportedFilesystemOperation,
    RequiresFullLocalInstallation,
    CloudAuthenticationRequired,
    NetworkUnavailable,
    DriveNotMounted,
    Cancelled
}

public static class ReadinessText
{
    public static string Describe(ReadinessStatus s) => s switch
    {
        ReadinessStatus.ReadyToLaunch => "Ready to launch",
        ReadinessStatus.PreparingStartupData => "Preparing startup data",
        ReadinessStatus.MayRequireAdditionalDownloads => "May require additional downloads",
        ReadinessStatus.InsufficientCacheSpace => "Insufficient cache space",
        ReadinessStatus.UnsupportedFilesystemOperation => "Unsupported filesystem operation",
        ReadinessStatus.RequiresFullLocalInstallation => "Requires full local installation",
        ReadinessStatus.CloudAuthenticationRequired => "Cloud authentication required",
        ReadinessStatus.NetworkUnavailable => "Network unavailable",
        ReadinessStatus.DriveNotMounted => "Cloud drive not mounted",
        ReadinessStatus.Cancelled => "Preparation cancelled",
        _ => "Unknown — not yet analyzed"
    };
}

public enum ExecutionMode
{
    /// <summary>Launch directly from the mounted cloud drive.</summary>
    CloudDirect,
    /// <summary>Essential files local on SSD, large assets streamed through symlinks into the mount.</summary>
    HybridOptimized,
    /// <summary>Complete local copy (after user approval).</summary>
    FullLocal
}

public enum CompatibilityLevel
{
    NotAnalyzed,
    /// <summary>Verified to launch in this mode on this PC (a successful monitored launch was recorded).</summary>
    Verified,
    /// <summary>No blockers found, not yet verified by an actual launch.</summary>
    NoBlockersFound,
    /// <summary>Some features are known to be risky on a virtual drive.</summary>
    Uncertain,
    /// <summary>Should be installed locally (installer, anti-cheat, launcher requirements).</summary>
    NeedsLocalInstall,
    /// <summary>Known not to work through CloudSSD.</summary>
    Incompatible
}
