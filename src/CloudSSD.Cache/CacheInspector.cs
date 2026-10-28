using System.Text.Json;
using CloudSSD.Core;

namespace CloudSSD.Cache;

public sealed record CachedRange(long Pos, long Size);

/// <summary>One file in rclone's VFS cache, combining rclone's metadata (vfsMeta) with the on-disk sparse data file.</summary>
public sealed record CachedFile(
    string RemotePath,
    long LogicalSize,
    long CachedBytes,
    long PhysicalBytes,
    bool Dirty,
    DateTime LastAccess,
    string? Fingerprint,
    IReadOnlyList<CachedRange> Ranges)
{
    public double CachedFraction => LogicalSize > 0 ? Math.Min(1.0, CachedBytes / (double)LogicalSize) : 1.0;

    public bool Covers(long offset, long length)
    {
        var end = offset + length;
        foreach (var r in Ranges)
            if (r.Pos <= offset && r.Pos + r.Size >= end) return true;
        return false;
    }
}

public sealed record CacheUsage(
    int Files,
    long LogicalBytes,
    long CachedBytes,
    long PhysicalBytes,
    int DirtyFiles,
    long VolumeFreeBytes,
    long VolumeTotalBytes,
    DateTime MeasuredAt);

/// <summary>
/// Reads rclone's VFS cache layout: &lt;cache-dir&gt;\vfs\&lt;fs&gt;\path (sparse data) and &lt;cache-dir&gt;\vfsMeta\&lt;fs&gt;\path (JSON
/// with Size, ATime, Dirty, Fingerprint and the list of downloaded ranges "Rs"). Read-only: CloudSSD never edits
/// rclone's cache while rclone owns it.
/// </summary>
public static class CacheInspector
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed class MetaDto
    {
        public DateTime ATime { get; set; }
        public long Size { get; set; }
        public List<CachedRange>? Rs { get; set; }
        public string? Fingerprint { get; set; }
        public bool Dirty { get; set; }
    }

    public static IEnumerable<CachedFile> Enumerate(string cacheDir, string? remoteRoot = null)
    {
        var metaRoot = Path.Combine(cacheDir, "vfsMeta");
        var dataRoot = Path.Combine(cacheDir, "vfs");
        if (!Directory.Exists(metaRoot)) yield break;
        foreach (var fsDir in Directory.EnumerateDirectories(metaRoot))
        {
            var fsName = Path.GetFileName(fsDir);
            foreach (var metaFile in SafeEnumerateFiles(fsDir))
            {
                var rel = Path.GetRelativePath(fsDir, metaFile);
                var cf = Read(metaFile, Path.Combine(dataRoot, fsName, rel), rel);
                if (cf != null) yield return remoteRoot is null ? cf : cf with { RemotePath = StripRoot(cf.RemotePath, remoteRoot) };
            }
        }
    }

    public static CachedFile? Find(string cacheDir, string remotePath, string? remoteRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(remoteRoot))
            remotePath = NormalizeRoot(remoteRoot) + "/" + remotePath.Replace('\\', '/').TrimStart('/');
        var rel = Encode(remotePath.Replace('/', '\\').TrimStart('\\'));
        var metaRoot = Path.Combine(cacheDir, "vfsMeta");
        if (!Directory.Exists(metaRoot)) return null;
        foreach (var fsDir in Directory.EnumerateDirectories(metaRoot))
        {
            var meta = Path.Combine(fsDir, rel);
            if (File.Exists(meta)) return Read(meta, Path.Combine(cacheDir, "vfs", Path.GetFileName(fsDir), rel), rel);
        }
        return null;
    }

    private static CachedFile? Read(string metaFile, string dataFile, string rel)
    {
        MetaDto? dto;
        try
        {
            using var fs = new FileStream(metaFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            dto = JsonSerializer.Deserialize<MetaDto>(fs, Json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // being rewritten by rclone right now
        }
        if (dto is null) return null;
        var ranges = (IReadOnlyList<CachedRange>?)dto.Rs ?? Array.Empty<CachedRange>();
        long cached = 0;
        foreach (var r in ranges) cached += r.Size;
        long physical = 0;
        try { if (File.Exists(dataFile)) physical = DiskInfo.PhysicalSize(dataFile); }
        catch (Exception) { physical = 0; }
        return new CachedFile(Decode(rel).Replace('\\', '/'), dto.Size, cached, physical, dto.Dirty, dto.ATime, dto.Fingerprint, ranges);
    }

    /// <summary>rclone nests the remote root (e.g. "Games" for gdrive:Games) inside the cache folder.</summary>
    public static string StripRoot(string path, string remoteRoot)
    {
        var root = NormalizeRoot(remoteRoot);
        var p = path.StartsWith("?/", StringComparison.Ordinal) ? path[2..] : path;
        return root.Length > 0 && p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? p[(root.Length + 1)..] : path;
    }

    private static string NormalizeRoot(string root)
    {
        var r = root.Replace('\\', '/').Trim('/');
        return r.StartsWith("?/", StringComparison.Ordinal) ? r[2..] : r;
    }

    public static CacheUsage Measure(string cacheDir)
    {
        int files = 0, dirty = 0;
        long logical = 0, cached = 0, physical = 0;
        foreach (var f in Enumerate(cacheDir))
        {
            files++;
            logical += f.LogicalSize;
            cached += f.CachedBytes;
            physical += f.PhysicalBytes;
            if (f.Dirty) dirty++;
        }
        Directory.CreateDirectory(cacheDir);
        var (free, total) = DiskInfo.FreeSpace(cacheDir);
        return new CacheUsage(files, logical, cached, physical, dirty, free, total, DateTime.Now);
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(dir, "*", opts);
    }

    // rclone's local encoder replaces characters Windows forbids with full-width look-alikes.
    private static readonly (char Plain, char Encoded)[] Map =
    [
        (':', '：'), ('*', '＊'), ('?', '？'), ('"', '＂'), ('<', '＜'), ('>', '＞'), ('|', '｜')
    ];

    public static string Decode(string s)
    {
        foreach (var (p, e) in Map) s = s.Replace(e, p);
        return s;
    }

    public static string Encode(string s)
    {
        foreach (var (p, e) in Map) s = s.Replace(p, e);
        return s;
    }
}
