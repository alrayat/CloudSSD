using System.Text.RegularExpressions;

namespace CloudSSD.Games;

public sealed record SteamLibraryStatus(string Path, bool SteamInstalled, bool Registered);

/// <summary>Checks a writable library and Steam's own registration without editing client configuration or manifests.</summary>
public static class SteamLibrarySetup
{
    public static SteamLibraryStatus Prepare(string driveRoot, string? steamClient)
    {
        var root = Path.GetFullPath(driveRoot);
        var library = Directory.Exists(Path.Combine(root, "steamapps")) ? root : Path.Combine(root, "SteamLibrary");
        Directory.CreateDirectory(Path.Combine(library, "steamapps", "common"));
        var probe = Path.Combine(library, ".cloudssd-write-check-" + Guid.NewGuid().ToString("N"));
        var moved = probe + ".moved";
        try
        {
            using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write([0x43, 0x50, 0x58]); file.Flush(true); }
            File.Move(probe, moved);
            if (!File.ReadAllBytes(moved).AsSpan().SequenceEqual(new byte[] { 0x43, 0x50, 0x58 }))
                throw new IOException("The cloud drive did not return the data written to it.");
        }
        finally
        {
            foreach (var path in new[] { probe, moved })
                try { File.Delete(path); } catch (IOException) { }
        }
        var installed = steamClient != null && File.Exists(steamClient);
        return new(library, installed, installed && IsRegistered(Path.GetDirectoryName(steamClient!)!, library));
    }

    public static bool IsRegistered(string steamRoot, string library)
    {
        if (SamePath(steamRoot, library)) return true;
        foreach (var relative in new[] { "steamapps", "config" })
        {
            try
            {
                var text = File.ReadAllText(Path.Combine(steamRoot, relative, "libraryfolders.vdf"));
                if (RegisteredPaths(text).Any(path => SamePath(path, library))) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    public static IEnumerable<string> RegisteredPaths(string vdf) =>
        Regex.Matches(vdf, "\"path\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\""));

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
