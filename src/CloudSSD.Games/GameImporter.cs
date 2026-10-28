using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudSSD.Core;
using CloudSSD.Mount;

namespace CloudSSD.Games;

public sealed record ImportFile(string RelativePath, long Size);

public sealed record ImportPlan(string SourceRoot, string GameName, string RemoteFolder, IReadOnlyList<ImportFile> Files, long TotalBytes,
    IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings)
{
    public bool CanStart => Blockers.Count == 0 && Files.Count > 0;
}

public enum ImportStage { Hashing, Uploading, Verifying, Publishing, Done }

public sealed record ImportProgress(ImportStage Stage, long Bytes, long TotalBytes, double SpeedBytesPerSec, string? Detail);

public sealed record ImportResult(bool Verified, string Destination, long Bytes, int Files, TimeSpan Elapsed, string VerificationMethod, string Message);

/// <summary>
/// Mode C, "import an existing game": copies a game folder from a local disk into cloud storage, verifies it and
/// publishes a manifest. The local folder is only read; deleting it is a separate, explicit user action.
/// Uploads use "rclone copy", which is resumable (re-running skips files that are already complete).
/// </summary>
public sealed class GameImporter(RcloneConfigService config)
{
    /// <summary>Written last: an import without it is incomplete and must not be treated as installed.</summary>
    public const string ManifestName = ".cloudssd-manifest.json";

    public static ImportPlan Plan(string sourceRoot, string remoteFolder, long? maxFileSize, long? cloudFreeBytes, string? maxFileSizeReason = null)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        var blockers = new List<string>();
        var warnings = new List<string>();
        var files = new List<ImportFile>();
        if (!Directory.Exists(sourceRoot)) blockers.Add("The folder does not exist.");
        else
        {
            foreach (var f in Directory.EnumerateFiles(sourceRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 }))
            {
                var fi = new FileInfo(f);
                if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) { warnings.Add($"Skipped link {Path.GetRelativePath(sourceRoot, f)}."); continue; }
                files.Add(new ImportFile(Path.GetRelativePath(sourceRoot, f).Replace('\\', '/'), fi.Length));
            }
        }
        var total = files.Sum(f => f.Size);
        if (maxFileSize is { } max)
        {
            var big = files.Where(f => f.Size > max).OrderByDescending(f => f.Size).ToList();
            if (big.Count > 0)
                blockers.Add($"{big.Count} file(s) are larger than {ByteSize.Format(max)}{(maxFileSizeReason != null ? $" ({maxFileSizeReason})" : "")}, " +
                             $"for example {big[0].RelativePath} ({ByteSize.Format(big[0].Size)}). CloudSSD does not split files to get around a plan limit.");
        }
        if (cloudFreeBytes is { } free && free < total) blockers.Add($"Not enough cloud space: {ByteSize.Format(total)} needed, {ByteSize.Format(free)} free.");
        if (files.Any(f => f.RelativePath.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))) warnings.Add("The folder already contains a CloudSSD manifest; it will be replaced.");
        if (files.Any(f => Path.GetFileName(f.RelativePath).Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase) || f.RelativePath.Contains("steam_api", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("Steam game: it may still need Steam running and the game owned on your account to start. Steam will not see this cloud copy as installed.");
        var name = Path.GetFileName(sourceRoot.TrimEnd('\\'));
        return new ImportPlan(sourceRoot, name, remoteFolder.Replace('\\', '/').Trim('/'), files, total, blockers, warnings);
    }

    /// <summary>Maps a path copied from the selected Windows drive to a folder inside its remote root.</summary>
    public static string NormalizeFolder(MountDefinition mount, string remoteFolder, string? activeDriveLetter = null)
    {
        var folder = remoteFolder.Trim().Replace('\\', '/');
        if (folder.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Choose a folder inside the selected cloud drive, not a network share.", nameof(remoteFolder));
        if (folder.Length >= 2 && char.IsAsciiLetter(folder[0]) && folder[1] == ':')
        {
            var drive = (activeDriveLetter ?? mount.DriveLetter)?.TrimEnd(':', '/', '\\');
            if (folder.Length < 3 || folder[2] != '/' || !string.Equals(drive, folder[..1], StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use a full path on the selected cloud drive, or a folder such as Games/Example.", nameof(remoteFolder));
            folder = folder[3..];
        }
        folder = folder.Trim('/');
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.Contains(':') || p.Any(char.IsControl)))
            throw new ArgumentException("Enter a game folder inside the cloud drive, such as Games/Example; parent paths and remote names are not allowed.", nameof(remoteFolder));
        return string.Join('/', parts);
    }

    public static string Destination(MountDefinition mount, string remoteFolder)
    {
        var spec = mount.RemoteSpec.TrimEnd('/');
        return spec + (spec.EndsWith(':') ? "" : "/") + NormalizeFolder(mount, remoteFolder);
    }

    public async Task<ImportResult> ImportAsync(RcloneInfo rclone, MountDefinition mount, ImportPlan plan, bool remoteHasHashes, bool fullVerify,
        IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        if (!plan.CanStart) throw new InvalidOperationException(string.Join(" ", plan.Blockers));
        var sw = Stopwatch.StartNew();
        var dest = Destination(mount, plan.RemoteFolder);

        // 1. Hash locally (SHA-256 goes into the manifest; it lets later repairs detect damaged files).
        var hashes = new Dictionary<string, string>();
        long hashed = 0;
        var buf = new byte[1 << 20];
        foreach (var f in plan.Files)
        {
            ct.ThrowIfCancellationRequested();
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var fs = new FileStream(Path.Combine(plan.SourceRoot, f.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            int n;
            while ((n = await fs.ReadAsync(buf, ct)) > 0)
            {
                sha.AppendData(buf, 0, n);
                hashed += n;
                progress?.Report(new ImportProgress(ImportStage.Hashing, hashed, plan.TotalBytes, 0, f.RelativePath));
            }
            hashes[f.RelativePath] = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        // 2. Upload (resumable).
        var runner = config.CreateRunner(rclone);
        var common = new List<string>();
        foreach (var (k, v) in mount.ExtraFlags)
        {
            if (MountFlagBuilder.IsForbiddenExtraFlag(k)) continue;
            common.Add("--" + k.TrimStart('-'));
            if (!string.IsNullOrEmpty(v)) common.Add(v);
        }
        var copyArgs = new List<string> { "copy", plan.SourceRoot, dest, "--exclude", "/" + ManifestName, "--stats", "1s", "--use-json-log", "--stats-log-level", "NOTICE",
            "-v", "--transfers", "2", "--checkers", "4", "--retries", "5" };
        if (!remoteHasHashes) copyArgs.Add("--size-only");
        copyArgs.AddRange(common);
        var copy = await runner.RunAsync(copyArgs, TimeSpan.FromDays(3), ct, onStdErrLine: line => Stats(line, ImportStage.Uploading, progress));
        if (!copy.Success)
            return new ImportResult(false, dest, plan.TotalBytes, plan.Files.Count, sw.Elapsed, "none",
                "Upload failed: " + RcloneConfigService.ExplainError(copy.StdErr) + " Run the import again to resume; completed files are skipped.");

        // 3. Verify against the source.
        var method = remoteHasHashes ? "provider checksums (MD5) + sizes" : fullVerify ? "full download and byte comparison" : "sizes only";
        progress?.Report(new ImportProgress(ImportStage.Verifying, 0, plan.TotalBytes, 0, method));
        var checkArgs = new List<string> { "check", plan.SourceRoot, dest, "--one-way", "--exclude", "/" + ManifestName, "--checkers", "4" };
        if (!remoteHasHashes) checkArgs.Add(fullVerify ? "--download" : "--size-only");
        checkArgs.AddRange(common);
        var check = await runner.RunAsync(checkArgs, TimeSpan.FromDays(3), ct);
        if (!check.Success)
            return new ImportResult(false, dest, plan.TotalBytes, plan.Files.Count, sw.Elapsed, method,
                "Verification found differences: " + RcloneConfigService.ExplainError(check.StdErr) + " Run the import again to re-upload them.");

        // 4. Publish the manifest last.
        progress?.Report(new ImportProgress(ImportStage.Publishing, plan.TotalBytes, plan.TotalBytes, 0, ManifestName));
        var manifest = new JsonObject
        {
            ["format"] = 1, ["game"] = plan.GameName, ["importedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["source"] = plan.SourceRoot,
            ["verification"] = method, ["totalBytes"] = plan.TotalBytes,
            ["files"] = new JsonArray(plan.Files.Select(f => (JsonNode)new JsonObject { ["path"] = f.RelativePath, ["size"] = f.Size, ["sha256"] = hashes[f.RelativePath] }).ToArray())
        };
        var tmp = Path.Combine(Path.GetTempPath(), "cloudssd-cli-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(tmp, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        try
        {
            var put = await runner.RunAsync(new List<string> { "copyto", tmp, dest + "/" + ManifestName }.Concat(common), TimeSpan.FromMinutes(10), ct);
            if (!put.Success)
                return new ImportResult(false, dest, plan.TotalBytes, plan.Files.Count, sw.Elapsed, method, "Files uploaded and verified, but the manifest could not be written: " + RcloneConfigService.ExplainError(put.StdErr));
        }
        finally { File.Delete(tmp); }
        progress?.Report(new ImportProgress(ImportStage.Done, plan.TotalBytes, plan.TotalBytes, 0, null));
        return new ImportResult(true, dest, plan.TotalBytes, plan.Files.Count, sw.Elapsed, method,
            $"Imported {plan.Files.Count:N0} files ({ByteSize.Format(plan.TotalBytes)}) in {Bandwidth.FormatDuration(sw.Elapsed)}; verified by {method}. Your local copy was not changed.");
    }

    private static void Stats(string line, ImportStage stage, IProgress<ImportProgress>? progress)
    {
        if (progress is null || !line.StartsWith('{')) return;
        try
        {
            var s = JsonNode.Parse(line)?["stats"];
            if (s is null) return;
            progress.Report(new ImportProgress(stage, s["bytes"]?.GetValue<long>() ?? 0, s["totalBytes"]?.GetValue<long>() ?? 0, s["speed"]?.GetValue<double>() ?? 0, null));
        }
        catch (JsonException) { }
    }

    /// <summary>Reads a published manifest from a mounted drive (null when missing = import incomplete).</summary>
    public static JsonObject? ReadManifest(string gameRootOnDrive)
    {
        var p = Path.Combine(gameRootOnDrive, ManifestName);
        try { return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) as JsonObject : null; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }
}
