using System.Diagnostics;
using CloudSSD.Cache;
using CloudSSD.Core;

namespace CloudSSD.Games;

public sealed record HydrateProgress(long DoneBytes, long TotalBytes, int FilesDone, int Files, double MBps, string? Current);

public sealed record HydrateResult(bool Complete, long TotalBytes, long VerifiedBytes, int Files, int FilesIncomplete, TimeSpan Elapsed,
    IReadOnlyList<string> Incomplete, string Message);

/// <summary>
/// "Fully cached" mode: reads every byte of a game folder through the mounted drive, so rclone stores all of it in its
/// local VFS cache, then verifies against rclone's own range index (never against in-memory bookkeeping) that each file is
/// completely present. Large files are split into segments read by several workers in parallel, because the measured
/// TeraBox free plan gives ~1 MB/s per connection but more with parallel requests. This costs the game's full size on the
/// local disk; it is labelled as such and is not the storage-saving mode.
/// </summary>
public static class CacheHydrator
{
    public const long SegmentBytes = 32L * 1024 * 1024;

    public static async Task<HydrateResult> HydrateAsync(string gameRootOnDrive, string cacheDir, string mountRemotePath, string remotePrefix, int parallel,
        IProgress<HydrateProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var files = Directory.EnumerateFiles(gameRootOnDrive, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(f => (Path: f, Size: new FileInfo(f).Length)).ToList();
        var total = files.Sum(f => f.Size);
        var segments = files.SelectMany(f => Enumerable.Range(0, (int)Math.Max(1, (f.Size + SegmentBytes - 1) / SegmentBytes))
            .Select(i => (f.Path, Offset: i * SegmentBytes, Length: Math.Min(SegmentBytes, f.Size - i * SegmentBytes)))).ToList();
        long done = 0;
        int filesDone = 0;
        var remaining = files.ToDictionary(f => f.Path, f => (int)Math.Max(1, (f.Size + SegmentBytes - 1) / SegmentBytes), StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(segments, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = ct }, async (seg, c) =>
        {
            var buf = new byte[1024 * 1024];
            await using (var fs = new FileStream(seg.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.Asynchronous))
            {
                fs.Seek(seg.Offset, SeekOrigin.Begin);
                long left = seg.Length;
                while (left > 0)
                {
                    var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), c);
                    if (n == 0) throw new IOException($"{Path.GetFileName(seg.Path)} ended early while caching it");
                    left -= n;
                    var d = Interlocked.Add(ref done, n);
                    progress?.Report(new HydrateProgress(d, total, Volatile.Read(ref filesDone), files.Count, d / 1e6 / Math.Max(0.001, sw.Elapsed.TotalSeconds),
                        Path.GetFileName(seg.Path)));
                }
            }
            lock (remaining)
                if (--remaining[seg.Path] == 0) Interlocked.Increment(ref filesDone);
        });

        // rclone records a file's downloaded ranges when its last handle has been closed for its handle-caching period.
        await Task.Delay(Mount.RcloneMountService.HandleCachingWindow + TimeSpan.FromSeconds(2), ct);
        var root = Path.GetFullPath(gameRootOnDrive).TrimEnd('\\');
        var prefix = remotePrefix.Trim('/');
        var expected = files.ToDictionary(f => (prefix.Length > 0 ? prefix + "/" : "") + Path.GetRelativePath(root, f.Path).Replace('\\', '/'), f => f.Size,
            StringComparer.OrdinalIgnoreCase);
        // A mount's cache can hold sub-folders for more than one remote name (e.g. after switching to the chunked remote).
        var cached = CacheInspector.Enumerate(cacheDir, mountRemotePath).GroupBy(f => f.RemotePath.Trim('/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CachedBytes).First(), StringComparer.OrdinalIgnoreCase);
        var incomplete = new List<string>();
        long verified = 0;
        foreach (var (rel, size) in expected)
        {
            if (size == 0) continue;
            if (cached.TryGetValue(rel, out var cf) && cf.LogicalSize == size && cf.Covers(0, size)) verified += size;
            else incomplete.Add(rel);
        }
        var complete = incomplete.Count == 0;
        var msg = complete
            ? $"Fully cached: {ByteSize.Format(total)} in {files.Count} files, verified in rclone's cache index ({Bandwidth.FormatDuration(sw.Elapsed)})."
            : $"{incomplete.Count} of {files.Count} files are not completely in the cache (for example {incomplete[0]}); the cache budget may be too small or data was evicted.";
        return new HydrateResult(complete, total, verified, files.Count, incomplete.Count, sw.Elapsed, incomplete, msg);
    }
}
