using CloudSSD.Core;

namespace CloudSSD.Cache;

public enum DiskPressure { Normal, Low, Critical }

public sealed record CacheBudgetDecision(
    long EffectiveMaxBytes,
    long RequestedBudgetBytes,
    long ReserveBytes,
    long VolumeFreeBytes,
    long CurrentCacheBytes,
    DiskPressure Pressure,
    IReadOnlyList<string> Warnings)
{
    public bool BudgetReduced => EffectiveMaxBytes < RequestedBudgetBytes;
}

/// <summary>
/// Turns the user's cache budget into a value rclone can honour without endangering Windows.
/// rclone's --vfs-cache-max-size is not an instantaneous quota: open files are never evicted and cleanup runs every
/// poll interval, so the cache can temporarily exceed it. The guard therefore keeps an overrun margin below the free
/// space reserve and pauses CloudSSD's own prefetching under disk pressure.
/// </summary>
public static class DiskSafetyGuard
{
    /// <summary>Fraction of the budget assumed as possible transient overrun (open files + poll delay).</summary>
    public const double OverrunMargin = 0.10;

    public static readonly long[] BudgetPresets = [10 * ByteSize.GiB, 20 * ByteSize.GiB, 30 * ByteSize.GiB, 40 * ByteSize.GiB];

    public static CacheBudgetDecision Decide(long requestedBudget, long reserve, long volumeFree, long currentCacheBytes, int mountCount = 1)
    {
        var warnings = new List<string>();
        mountCount = Math.Max(1, mountCount);
        // Space rclone may use = what it already occupies + what is free beyond the reserve.
        var available = Math.Max(0, currentCacheBytes + volumeFree - reserve);
        var safeTotal = (long)(available / (1 + OverrunMargin));
        var total = Math.Min(requestedBudget, safeTotal);
        var perMount = total / mountCount;
        if (total < requestedBudget)
            warnings.Add($"Cache budget reduced from {ByteSize.Format(requestedBudget)} to {ByteSize.Format(total)} to keep {ByteSize.Format(reserve)} free for Windows.");
        if (perMount < ByteSize.GiB)
            warnings.Add($"Only {ByteSize.Format(perMount)} of cache space is available per drive; most games will need far more. Free up space or move the cache.");
        var pressure = Pressure(volumeFree, reserve);
        if (pressure != DiskPressure.Normal)
            warnings.Add(pressure == DiskPressure.Critical
                ? $"Disk space critical: {ByteSize.Format(volumeFree)} free. Prefetching is stopped; rclone will evict cached data."
                : $"Disk space low: {ByteSize.Format(volumeFree)} free (reserve {ByteSize.Format(reserve)}). Prefetching is paused.");
        return new CacheBudgetDecision(perMount, requestedBudget, reserve, volumeFree, currentCacheBytes, pressure, warnings);
    }

    public static DiskPressure Pressure(long volumeFree, long reserve) =>
        volumeFree < reserve / 2 ? DiskPressure.Critical
        : volumeFree < reserve ? DiskPressure.Low
        : DiskPressure.Normal;

    /// <summary>Whether CloudSSD may download <paramref name="bytes"/> more speculatively without breaching the reserve.</summary>
    public static bool MayPrefetch(long bytes, long volumeFree, long reserve) =>
        Pressure(volumeFree, reserve) == DiskPressure.Normal && volumeFree - bytes > reserve;
}

/// <summary>Cache maintenance that only ever touches folders CloudSSD created (marker file required).</summary>
public static class CacheMaintenance
{
    public const string Marker = ".cloudssd-cache";

    public sealed record ClearResult(bool Cleared, long BytesFreed, int DirtyFilesBlocking, string Message);

    /// <summary>
    /// Deletes a mount's cache directory. Refuses when the folder lacks the CloudSSD marker, is not inside the
    /// configured cache root, or holds files with pending uploads (unless <paramref name="discardPendingUploads"/>).
    /// Must only be called while the mount is stopped. Never touches cloud data.
    /// </summary>
    public static ClearResult ClearMountCache(string mountCacheDir, string cacheRoot, bool discardPendingUploads = false)
    {
        var dir = Path.GetFullPath(mountCacheDir).TrimEnd('\\');
        var root = Path.GetFullPath(cacheRoot).TrimEnd('\\');
        if (!Directory.Exists(dir)) return new ClearResult(true, 0, 0, "Cache already empty.");
        if (!dir.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            return new ClearResult(false, 0, 0, "Refused: folder is outside the CloudSSD cache root.");
        if (!File.Exists(Path.Combine(dir, Marker)))
            return new ClearResult(false, 0, 0, "Refused: folder is not marked as a CloudSSD cache.");

        var files = CacheInspector.Enumerate(dir).ToList();
        var dirty = files.Count(f => f.Dirty);
        if (dirty > 0 && !discardPendingUploads)
            return new ClearResult(false, 0, dirty, $"{dirty} file(s) have changes that were not uploaded yet. Mount the drive and let uploads finish first.");

        long freed = files.Sum(f => f.PhysicalBytes);
        foreach (var sub in new[] { "vfs", "vfsMeta" })
        {
            var p = Path.Combine(dir, sub);
            if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
        }
        return new ClearResult(true, freed, dirty, $"Freed {ByteSize.Format(freed)}.");
    }
}
