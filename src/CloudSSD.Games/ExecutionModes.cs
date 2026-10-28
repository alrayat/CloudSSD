using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudSSD.Core;
using CloudSSD.Mount;

namespace CloudSSD.Games;

public sealed record HybridPlan(IReadOnlyList<FileStat> Copy, IReadOnlyList<FileStat> Link, long CopyBytes, long LinkBytes);

public sealed record HybridBuildResult(string LocalRoot, int Copied, int Linked, int Unchanged, long CopiedBytes, TimeSpan Elapsed, IReadOnlyList<string> Errors);

/// <summary>
/// Hybrid Optimized mode: builds a real local folder on the SSD that mirrors the game's directory tree. Executables,
/// DLLs, configuration and small files are copied locally (fast, writable, launch from a normal NTFS path); large asset
/// files become symbolic links into the cloud drive and are streamed. New files the game writes land on the SSD.
/// Requires Windows Developer Mode (or admin) for unprivileged symbolic links; CloudSSD never enables it silently.
/// </summary>
public static class HybridBuilder
{
    public static readonly HashSet<string> EssentialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".ini", ".cfg", ".config", ".json", ".xml", ".txt", ".ico", ".manifest", ".dat", ".lua", ".bat", ".cmd",
        ".pdb", ".sig", ".url", ".info", ".vdf", ".yaml", ".yml", ".toml", ".log"
    };

    public const string ManifestName = ".cloudssd-hybrid.json";

    public static HybridPlan Plan(IReadOnlyList<FileStat> files, long smallFileLimit = 2 * ByteSize.MiB, long essentialLimit = 256 * ByteSize.MiB,
        IReadOnlyCollection<string>? pinned = null)
    {
        var copy = new List<FileStat>();
        var link = new List<FileStat>();
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.RelativePath);
            var isPinned = pinned?.Contains(f.RelativePath, StringComparer.OrdinalIgnoreCase) == true;
            if (isPinned || f.Size <= smallFileLimit || (EssentialExtensions.Contains(ext) && f.Size <= essentialLimit)) copy.Add(f);
            else link.Add(f);
        }
        return new HybridPlan(copy, link, copy.Sum(f => f.Size), link.Sum(f => f.Size));
    }

    public static async Task<HybridBuildResult> BuildAsync(string cloudGameRoot, string localRoot, HybridPlan plan, IProgress<(long Done, long Total, string File)>? progress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(localRoot);
        var manifestPath = Path.Combine(localRoot, ManifestName);
        var old = LoadManifest(manifestPath);
        var manifest = new JsonObject();
        var errors = new List<string>();
        int copied = 0, linked = 0, unchanged = 0;
        long copiedBytes = 0, done = 0;
        var total = plan.CopyBytes;

        foreach (var f in plan.Copy)
        {
            ct.ThrowIfCancellationRequested();
            var src = Path.Combine(cloudGameRoot, f.RelativePath);
            var dst = Path.Combine(localRoot, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            var key = Stamp(f);
            var existing = new FileInfo(dst);
            bool localModified = existing.Exists && old.TryGetPropertyValue(f.RelativePath, out var prev)
                                 && prev?["local"]?.GetValue<long>() is { } lt && lt != existing.LastWriteTimeUtc.Ticks;
            if (existing.Exists && existing.LinkTarget is null && old[f.RelativePath]?["stamp"]?.GetValue<string>() == key)
            {
                unchanged++;
                manifest[f.RelativePath] = new JsonObject { ["kind"] = "copy", ["stamp"] = key, ["local"] = existing.LastWriteTimeUtc.Ticks };
                done += f.Size;
                continue;
            }
            if (localModified)
            {
                // The game changed this local copy (e.g. a config); keep the user's version.
                errors.Add($"Kept locally modified {f.RelativePath} (cloud version differs).");
                manifest[f.RelativePath] = old[f.RelativePath]!.DeepClone();
                continue;
            }
            try
            {
                if (existing.Exists) File.Delete(dst);
                await CopyAsync(src, dst, f.Size, b => progress?.Report((done + b, total, f.RelativePath)), ct);
                File.SetLastWriteTimeUtc(dst, f.ModifiedUtc);
                copied++;
                copiedBytes += f.Size;
                manifest[f.RelativePath] = new JsonObject { ["kind"] = "copy", ["stamp"] = key, ["local"] = File.GetLastWriteTimeUtc(dst).Ticks };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Copy failed for {f.RelativePath}: {e.Message}");
            }
            done += f.Size;
        }

        foreach (var f in plan.Link)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(cloudGameRoot, f.RelativePath);
            var link = Path.Combine(localRoot, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var fi = new FileInfo(link);
            if (fi.Exists || fi.LinkTarget != null)
            {
                if (fi.LinkTarget != null && string.Equals(fi.LinkTarget, target, StringComparison.OrdinalIgnoreCase))
                {
                    unchanged++;
                    manifest[f.RelativePath] = new JsonObject { ["kind"] = "link", ["stamp"] = Stamp(f) };
                    continue;
                }
                if (fi.LinkTarget is null)
                {
                    errors.Add($"Skipped {f.RelativePath}: a local file already exists (not replaced).");
                    continue;
                }
                File.Delete(link); // only ever delete our own stale symlink, never a real file
            }
            try
            {
                File.CreateSymbolicLink(link, target);
                linked++;
                manifest[f.RelativePath] = new JsonObject { ["kind"] = "link", ["stamp"] = Stamp(f) };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Symbolic link failed for {f.RelativePath}: {e.Message}. Enable Windows Developer Mode for Hybrid mode.");
                break;
            }
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new HybridBuildResult(localRoot, copied, linked, unchanged, copiedBytes, sw.Elapsed, errors);
    }

    private static string Stamp(FileStat f) => $"{f.Size}:{f.ModifiedUtc.Ticks}";

    private static JsonObject LoadManifest(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static async Task CopyAsync(string src, string dst, long size, Action<long> onProgress, CancellationToken ct)
    {
        var tmp = dst + ".cloudssdpart";
        await using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous))
        await using (var o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
        {
            var buf = new byte[1 << 20];
            long copied = 0;
            int n;
            while ((n = await i.ReadAsync(buf, ct)) > 0)
            {
                await o.WriteAsync(buf.AsMemory(0, n), ct);
                copied += n;
                onProgress(copied);
            }
            if (copied != size) throw new IOException($"Size mismatch copying {src}: expected {size}, got {copied}");
        }
        File.Move(tmp, dst, true);
    }
}

public sealed record LocalCopyProgress(long Bytes, long TotalBytes, double SpeedBytesPerSec, double? EtaSeconds, long Transfers, long Errors);

/// <summary>
/// Full Local Fallback: copies the whole game from the cloud with "rclone copy" (checksummed, resumable - re-running
/// skips files already complete). Only runs after the caller has obtained explicit user approval.
/// </summary>
public sealed class FullLocalInstaller
{
    private readonly RcloneConfigService _config;

    public FullLocalInstaller(RcloneConfigService config) => _config = config;

    public static (bool Fits, long Free, long Needed) CheckSpace(string localRoot, long totalBytes, long reserve)
    {
        Directory.CreateDirectory(localRoot);
        var (free, _) = DiskInfo.FreeSpace(localRoot);
        return (free - reserve > totalBytes, free, totalBytes);
    }

    public async Task<RcloneResult> CopyAsync(RcloneInfo rclone, MountDefinition mount, string gameRootInMount, string localRoot, bool userApproved,
        IProgress<LocalCopyProgress>? progress, CancellationToken ct)
    {
        if (!userApproved) throw new InvalidOperationException("Full local installation requires explicit user approval.");
        Directory.CreateDirectory(localRoot);
        var src = mount.RemoteSpec.TrimEnd('/') + (string.IsNullOrEmpty(gameRootInMount) ? "" : (mount.RemoteSpec.EndsWith(':') ? "" : "/") + gameRootInMount.Trim('/'));
        var args = new List<string> { "copy", src, localRoot, "--stats", "1s", "--use-json-log", "--stats-log-level", "NOTICE", "-v", "--transfers", "4", "--checkers", "8" };
        foreach (var (k, v) in mount.ExtraFlags)
        {
            if (MountFlagBuilder.IsForbiddenExtraFlag(k)) continue;
            args.Add("--" + k.TrimStart('-'));
            if (!string.IsNullOrEmpty(v)) args.Add(v);
        }
        var runner = _config.CreateRunner(rclone);
        return await runner.RunAsync(args, TimeSpan.FromDays(2), ct, onStdErrLine: line =>
        {
            if (!line.StartsWith('{')) return;
            try
            {
                var n = JsonNode.Parse(line);
                var s = n?["stats"];
                if (s is null) return;
                progress?.Report(new LocalCopyProgress(
                    s["bytes"]?.GetValue<long>() ?? 0, s["totalBytes"]?.GetValue<long>() ?? 0, s["speed"]?.GetValue<double>() ?? 0,
                    s["eta"]?.GetValueKind() == JsonValueKind.Number ? s["eta"]!.GetValue<double>() : null,
                    s["transfers"]?.GetValue<long>() ?? 0, s["errors"]?.GetValue<long>() ?? 0));
            }
            catch (JsonException) { }
        });
    }
}
