using System.Text;
using System.Text.Json;
using CloudSSD.Cache;

namespace CloudSSD.Games;

/// <summary>Read-only snapshots of small, fully present Steam manifests. Never reads sparse holes as valid data.</summary>
public static class SteamManifestCache
{
    public static string? Read(string cacheDir, string remoteRoot, string relative)
    {
        if (relative.Split('/', '\\').Any(p => p == "..")) return null;
        var remote = string.IsNullOrWhiteSpace(remoteRoot) ? relative : remoteRoot.Trim('/', '\\') + "/" + relative;
        var encoded = CacheInspector.Encode(remote.Replace('/', '\\').TrimStart('\\'));
        var metaRoot = Path.Combine(cacheDir, "vfsMeta");
        if (!Directory.Exists(metaRoot)) return null;
        foreach (var fs in Directory.EnumerateDirectories(metaRoot))
        {
            var data = Path.Combine(cacheDir, "vfs", Path.GetFileName(fs), encoded);
            try
            {
                var metaFile = Path.Combine(fs, encoded);
                var metaText = File.ReadAllText(metaFile);
                using var meta = JsonDocument.Parse(metaText);
                var size = meta.RootElement.GetProperty("Size").GetInt64();
                if (size <= 0 || size > 1024 * 1024 || !meta.RootElement.GetProperty("Rs").EnumerateArray().Any(r =>
                    r.GetProperty("Pos").GetInt64() == 0 && r.GetProperty("Size").GetInt64() >= size)) continue;
                byte[] Snapshot()
                {
                    using var input = new FileStream(data, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (input.Length != size) return [];
                    var buffer = new byte[(int)input.Length]; input.ReadExactly(buffer); return buffer;
                }
                var first = Snapshot(); var second = Snapshot();
                if (first.Length > 0 && first.AsSpan().SequenceEqual(second) && File.ReadAllText(metaFile) == metaText)
                    return Encoding.UTF8.GetString(first).TrimStart('\uFEFF');
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return null;
    }

    public static IReadOnlyList<(string RelativePath, string Text)> List(string cacheDir, string remoteRoot)
    {
        var result = new List<(string, string)>();
        var dataRoot = Path.Combine(cacheDir, "vfs");
        if (!Directory.Exists(dataRoot)) return result;
        foreach (var fs in Directory.EnumerateDirectories(dataRoot))
            foreach (var apps in new[] { "SteamLibrary/steamapps", "steamapps" })
            {
                var prefix = string.IsNullOrWhiteSpace(remoteRoot) ? apps : remoteRoot.Trim('/', '\\') + "/" + apps;
                var dir = Path.Combine(fs, CacheInspector.Encode(prefix.Replace('/', '\\')));
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "appmanifest_*.acf").Take(200))
                {
                    var relative = apps + "/" + Path.GetFileName(file);
                    if (Read(cacheDir, remoteRoot, relative) is { } text) result.Add((relative, text));
                }
            }
        return result;
    }
}
