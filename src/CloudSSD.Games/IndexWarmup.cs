using CloudSSD.Core;
using CloudSSD.Mount;

namespace CloudSSD.Games;

/// <summary>
/// First-launch warm-up for a game that has never been profiled. Games mount their archives at start-up and read each
/// archive's table of contents before anything else: RAGE .rpf (GTA V, RDR2) keeps it at the start of the file, Unreal
/// .pak/.utoc near the end, many other formats in one of the two. So, without any knowledge of the game, caching every
/// small file whole plus the first and last <see cref="EdgeBytes"/> of every large file covers most of what the first
/// seconds read, for a small fraction of the game's size (GTA V Enhanced: ~1–2 GB of ~94 GiB).
/// This is a heuristic, not a learned profile: it never claims readiness, and the first launch is still profiled.
/// </summary>
public static class IndexWarmup
{
    public static long SmallFileBytes { get; set; } = 8 * ByteSize.MiB;
    public static long EdgeBytes { get; set; } = 2 * ByteSize.MiB;

    public sealed record Range(string RelativePath, long Offset, long Length);

    public sealed record Plan(IReadOnlyList<Range> Ranges, long Bytes, int SmallFiles, int LargeFiles, int Trimmed);

    /// <summary>
    /// Plans the ranges, smallest cost first (small files, then file heads, then tails), within <paramref name="budget"/>.
    /// Files in <paramref name="skip"/> (already cached as essentials) are left out.
    /// </summary>
    public static Plan PlanFor(IEnumerable<FileStat> files, long budget, ISet<string>? skip = null)
    {
        var small = new List<Range>();
        var heads = new List<Range>();
        var tails = new List<Range>();
        foreach (var f in files)
        {
            if (f.Size <= 0 || skip?.Contains(f.RelativePath) == true) continue;
            if (f.Size <= SmallFileBytes) { small.Add(new Range(f.RelativePath, 0, f.Size)); continue; }
            heads.Add(new Range(f.RelativePath, 0, EdgeBytes));
            var tailStart = Math.Max(EdgeBytes, f.Size - EdgeBytes);
            tails.Add(new Range(f.RelativePath, tailStart, f.Size - tailStart));
        }
        var ordered = small.OrderBy(r => r.Length).Concat(heads).Concat(tails).ToList();
        var chosen = new List<Range>();
        long bytes = 0;
        foreach (var r in ordered)
        {
            if (bytes + r.Length > budget) continue;
            chosen.Add(r);
            bytes += r.Length;
        }
        return new Plan(chosen, bytes, small.Count, heads.Count, ordered.Count - chosen.Count);
    }

    /// <summary>Reads the planned ranges through the drive (rclone keeps them in its cache), a few files at a time.</summary>
    public static async Task<(long Bytes, int Failed)> WarmAsync(Plan plan, string gameRootOnDrive, MountSession mount, string gameRemoteRoot,
        int parallel, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        long done = 0;
        int failed = 0;
        await Parallel.ForEachAsync(plan.Ranges, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = ct }, async (r, token) =>
        {
            var remotePath = string.IsNullOrEmpty(gameRemoteRoot) ? r.RelativePath : gameRemoteRoot.TrimEnd('/') + "/" + r.RelativePath;
            try
            {
                using var reg = mount.Attribution.Register(remotePath, r.Offset, r.Length);
                await using var fs = new FileStream(Path.Combine(gameRootOnDrive, r.RelativePath.Replace('/', '\\')), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 1, FileOptions.Asynchronous);
                fs.Position = r.Offset;
                var buf = new byte[1 << 20];
                for (long left = r.Length; left > 0;)
                {
                    var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), token);
                    if (n == 0) throw new IOException($"{r.RelativePath} ended before its startup range was cached.");
                    left -= n;
                }
                progress?.Report((Interlocked.Add(ref done, r.Length), plan.Bytes));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Interlocked.Increment(ref failed); }
        });
        return (done, failed);
    }
}
