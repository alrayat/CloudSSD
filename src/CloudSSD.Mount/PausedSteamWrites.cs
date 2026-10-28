using System.Text.Json;
using CloudSSD.Core;

namespace CloudSSD.Mount;

public sealed record PausedSteamWrite(string RelativePath, long Size, long CachedBytes, string State = "Paused");

/// <summary>
/// Pauses incomplete Steam staging writes while the mount is stopped. Both native data and range metadata are kept,
/// outside the active cache, so rclone cannot spend hours filling their missing ranges before exposing the drive.
/// No missing range is marked present, no cloud file is changed, and restoring requires an unmounted cache.
/// </summary>
public sealed class PausedSteamWrites
{
    private readonly string _cache;
    public string Root { get; }
    public PausedSteamWrites(string cacheDir)
    {
        _cache = Path.GetFullPath(cacheDir);
        Root = Path.Combine(Path.GetDirectoryName(_cache)!, "paused-steam-writes", Path.GetFileName(_cache));
    }

    private static string Within(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid cache recovery path.");
        var current = Path.GetDirectoryName(full);
        while (current != null && current.Length >= root.Length)
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recovery cannot follow a reparse point.");
            current = Path.GetDirectoryName(current);
        }
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid recovery file.");
        return full;
    }

    public IReadOnlyList<PausedSteamWrite> All()
    {
        if (!Directory.Exists(Root)) return [];
        var result = new List<PausedSteamWrite>();
        foreach (var file in Records())
        {
            var entry = Read(file);
            if (entry.State != "Restored") result.Add(entry);
        }
        return result;
    }

    private IEnumerable<string> Records() => Directory.Exists(Root)
        ? Directory.EnumerateFiles(Root, "entry.json", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }) : [];

    private static PausedSteamWrite Read(string file) => JsonSerializer.Deserialize<PausedSteamWrite>(File.ReadAllText(file)) ?? throw new IOException("Invalid paused write record.");
    private static void Save(string file, PausedSteamWrite entry)
    {
        using (var stream = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, entry); stream.Flush(true); }
        File.Move(file + ".tmp", file, true);
    }

    /// <summary>Rolls forward a process crash between moving data and moving its metadata. Call only before rclone starts.</summary>
    public void RecoverTransactions()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var record in Records())
        {
            var entry = Read(record);
            if (entry.State == "Pausing") FinishPause(record, entry);
            else if (entry.State == "Restoring") FinishRestore(record, entry);
            else if (entry.State == "Merging") FinishMerge(record, entry);
        }
    }

    private void FinishPause(string record, PausedSteamWrite entry)
    {
        var folder = Path.GetDirectoryName(record)!;
        var data = Within(Path.Combine(_cache, "vfs"), entry.RelativePath);
        var meta = Within(Path.Combine(_cache, "vfsMeta"), entry.RelativePath);
        var savedData = Path.Combine(folder, "data"); var savedMeta = Path.Combine(folder, "metadata.json");
        if (!File.Exists(savedMeta))
        {
            var raw = File.ReadAllBytes(meta);
            using var stream = new FileStream(savedMeta, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(raw); stream.Flush(true);
        }
        if (File.Exists(meta) && File.ReadAllText(meta) != File.ReadAllText(savedMeta)) throw new IOException("Pending metadata changed during pause. Both versions kept.");
        if (!File.Exists(savedData)) File.Move(data, savedData);
        else if (File.Exists(data)) throw new IOException("Two versions of a pending write exist. Both kept.");
        File.Delete(meta);
        Save(record, entry with { State = "Paused" });
    }

    /// <param name="appId">Only this Steam download is paused; unrelated writes are not touched.</param>
    public IReadOnlyList<PausedSteamWrite> PauseIncompleteDownloads()
    {
        RecoverTransactions();
        var meta = Path.Combine(_cache, "vfsMeta");
        if (!Directory.Exists(meta)) return All();
        var ids = Directory.EnumerateFiles(meta, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(f => Path.GetRelativePath(meta, f).Replace('\\', '/'))
            .Select(p => (Path: p, Index: p.IndexOf("/steamapps/downloading/", StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.Index >= 0).Select(p => p.Path[(p.Index + "/steamapps/downloading/".Length)..].Split('/')[0])
            .Where(p => uint.TryParse(p, out _)).Select(p => uint.Parse(p!)).Distinct().ToArray();
        foreach (var id in ids) Pause(id);
        return All();
    }

    public IReadOnlyList<PausedSteamWrite> Pause(uint appId)
    {
        RecoverTransactions();
        var meta = Path.Combine(_cache, "vfsMeta");
        if (!Directory.Exists(meta)) return All();
        foreach (var file in Directory.EnumerateFiles(meta, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var relative = Path.GetRelativePath(meta, file);
            if (!relative.Replace('\\', '/').Contains($"/steamapps/downloading/{appId}/", StringComparison.OrdinalIgnoreCase)) continue;
            var safeMeta = Within(meta, relative);
            using var json = JsonDocument.Parse(File.ReadAllText(safeMeta));
            var info = json.RootElement;
            if (!info.GetProperty("Dirty").GetBoolean()) continue;
            var size = info.GetProperty("Size").GetInt64();
            long present = 0, end = 0;
            foreach (var range in info.GetProperty("Rs").EnumerateArray())
            {
                var pos = range.GetProperty("Pos").GetInt64(); var length = range.GetProperty("Size").GetInt64();
                if (pos < end || length < 0 || pos > size || length > size - pos) throw new IOException("Invalid pending cache ranges.");
                end = pos + length; present = checked(present + length);
            }
            if (present >= size) continue; // fully cached writes can already finish in the background
            var data = Within(Path.Combine(_cache, "vfs"), relative);
            if (!File.Exists(data) || new FileInfo(data).Length != size) throw new IOException("Pending write data is missing or has changed. Cache kept.");
            // Never overwrite an earlier paused transaction, even if a later Steam session reused its path.
            if (All().Any(e => e.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("This Steam file already has paused writes. Resolve them before pausing a newer version.");
            var folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var entry = new PausedSteamWrite(relative, size, present, "Pausing");
            var record = Path.Combine(folder, "entry.json");
            Save(record, entry); // intent reaches disk before either source file is moved
            FinishPause(record, entry);
        }
        return All();
    }

    public void Restore()
    {
        RecoverTransactions();
        if (!Directory.Exists(Root)) return;
        var records = Records().Where(r => Read(r).State == "Paused").ToArray();
        // Preflight every target before changing anything. New dirty writes must never be overwritten by an old pause.
        var merge = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var e = Read(record);
            var meta = Within(Path.Combine(_cache, "vfsMeta"), e.RelativePath);
            if (File.Exists(meta))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(meta));
                if (document.RootElement.GetProperty("Dirty").GetBoolean())
                {
                    // Steam wrote to the same file again after the pause. Both writes are real: the paused bytes are merged
                    // into the newer version wherever it has no bytes of its own (2026-10-21: x64t/x64u had 1 MiB paused and a
                    // different 1 MiB written later; leaving the pause aside would upload the file without it).
                    if (document.RootElement.GetProperty("Size").GetInt64() != e.Size)
                        throw new IOException("Steam changed the size of a paused file. Both versions are kept; restoration was stopped.");
                    var live = Within(Path.Combine(_cache, "vfs"), e.RelativePath);
                    if (!File.Exists(live) || new FileInfo(live).Length != e.Size)
                        throw new IOException("Pending write data is missing or has changed. Both versions are kept; restoration was stopped.");
                    merge.Add(record);
                }
            }
            var saved = Path.Combine(Path.GetDirectoryName(record)!, "data");
            if (!File.Exists(saved) || new FileInfo(saved).Length != e.Size) throw new IOException("Paused data is missing or changed. Restoration stopped.");
        }
        foreach (var record in records)
        {
            var e = Read(record) with { State = merge.Contains(record) ? "Merging" : "Restoring" };
            Save(record, e);
            if (e.State == "Merging") FinishMerge(record, e);
            else FinishRestore(record, e);
        }
    }

    private readonly record struct ByteRange(long Pos, long Size)
    {
        public long End => Pos + Size;
    }

    private static List<ByteRange> Ranges(JsonElement info) =>
        info.GetProperty("Rs").EnumerateArray().Select(r => new ByteRange(r.GetProperty("Pos").GetInt64(), r.GetProperty("Size").GetInt64()))
            .Where(r => r.Size > 0).OrderBy(r => r.Pos).ToList();

    /// <summary>Parts of <paramref name="want"/> not covered by <paramref name="have"/> (both sorted).</summary>
    private static IEnumerable<ByteRange> Missing(ByteRange want, IReadOnlyList<ByteRange> have)
    {
        var pos = want.Pos;
        foreach (var h in have)
        {
            if (h.End <= pos) continue;
            if (h.Pos >= want.End) break;
            if (h.Pos > pos) yield return new ByteRange(pos, h.Pos - pos);
            pos = Math.Max(pos, h.End);
            if (pos >= want.End) yield break;
        }
        if (pos < want.End) yield return new ByteRange(pos, want.End - pos);
    }

    private static List<ByteRange> Union(IEnumerable<ByteRange> ranges)
    {
        var result = new List<ByteRange>();
        foreach (var r in ranges.Where(r => r.Size > 0).OrderBy(r => r.Pos))
        {
            if (result.Count > 0 && r.Pos <= result[^1].End)
            {
                var last = result[^1];
                result[^1] = new ByteRange(last.Pos, Math.Max(last.End, r.End) - last.Pos);
            }
            else result.Add(r);
        }
        return result;
    }

    /// <summary>
    /// Copies the paused bytes into the newer cached version where it has none, then records those ranges as present.
    /// Newer bytes always win. Safe to repeat after a crash: the copy is idempotent and the metadata is replaced atomically.
    /// The paused copy is kept as a backup.
    /// </summary>
    private void FinishMerge(string record, PausedSteamWrite e)
    {
        var folder = Path.GetDirectoryName(record)!;
        var data = Within(Path.Combine(_cache, "vfs"), e.RelativePath);
        var meta = Within(Path.Combine(_cache, "vfsMeta"), e.RelativePath);
        using var pausedInfo = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "metadata.json")));
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta))!.AsObject();
        List<ByteRange> live;
        using (var liveInfo = JsonDocument.Parse(node.ToJsonString())) live = Ranges(liveInfo.RootElement);
        var copy = Ranges(pausedInfo.RootElement).SelectMany(r => Missing(r, live)).ToList();
        using (var src = new FileStream(Path.Combine(folder, "data"), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var dst = new FileStream(data, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            if (src.Length != e.Size || dst.Length != e.Size) throw new IOException("Paused or pending data changed size. Both versions are kept.");
            var buf = new byte[1 << 20];
            foreach (var r in copy)
            {
                src.Position = r.Pos;
                dst.Position = r.Pos;
                for (long left = r.Size; left > 0;)
                {
                    var n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) throw new IOException("Paused data ended early. Both versions are kept.");
                    dst.Write(buf, 0, n);
                    left -= n;
                }
            }
            dst.Flush(true);
        }
        var merged = new System.Text.Json.Nodes.JsonArray(Union(live.Concat(copy))
            .Select(r => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["Pos"] = r.Pos, ["Size"] = r.Size }).ToArray());
        node["Rs"] = merged;
        node["Dirty"] = true;
        using (var stream = new FileStream(meta + ".merge", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            stream.Flush(true);
        }
        File.Move(meta + ".merge", meta, true);
        Save(record, e with { State = "Restored" });
    }

    private void FinishRestore(string record, PausedSteamWrite e)
    {
            var folder = Path.GetDirectoryName(record)!;
            var data = Within(Path.Combine(_cache, "vfs"), e.RelativePath);
            var meta = Within(Path.Combine(_cache, "vfsMeta"), e.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
            // Preserve any clean read cache rather than deleting it.
            if (File.Exists(Path.Combine(folder, "data")))
            {
                if (File.Exists(data)) File.Move(data, Path.Combine(folder, "read-cache-" + Guid.NewGuid().ToString("N")));
                if (File.Exists(meta)) File.Move(meta, Path.Combine(folder, "read-metadata-" + Guid.NewGuid().ToString("N")));
                File.Move(Path.Combine(folder, "data"), data);
            }
            if (!File.Exists(data) || new FileInfo(data).Length != e.Size) throw new IOException("Restored data is missing or changed. Kept recovery metadata.");
            var raw = File.ReadAllBytes(Path.Combine(folder, "metadata.json"));
            using (var stream = new FileStream(meta + ".restore", FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(raw); stream.Flush(true); }
            File.Move(meta + ".restore", meta, true);
            Save(record, e with { State = "Restored" });
    }
}
