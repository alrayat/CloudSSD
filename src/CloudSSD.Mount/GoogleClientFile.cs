using System.Text.Json;

namespace CloudSSD.Mount;

/// <summary>
/// A Google OAuth client file as downloaded from Google Cloud Console ("Download JSON":
/// client_secret_&lt;id&gt;.apps.googleusercontent.com.json). Lets the user connect with their own client without typing.
/// </summary>
public sealed record GoogleClientFile(string Path, string ClientId, string ClientSecret, string? ProjectId, DateTime Modified)
{
    /// <summary>The Google Cloud page where test users are added for this project.</summary>
    public string? TestUsersUrl => ProjectId is null ? null : $"https://console.cloud.google.com/auth/audience?project={Uri.EscapeDataString(ProjectId)}";

    public static GoogleClientFile? TryLoad(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var kind in new[] { "installed", "web" })
            {
                if (!doc.RootElement.TryGetProperty(kind, out var c)) continue;
                var id = c.TryGetProperty("client_id", out var i) ? i.GetString() : null;
                var secret = c.TryGetProperty("client_secret", out var s) ? s.GetString() : null;
                var project = c.TryGetProperty("project_id", out var p) ? p.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) return null;
                if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase)) return null;
                return new GoogleClientFile(path, id, secret, project, File.GetLastWriteTime(path));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// Optional publisher key shipped next to the program (added at build time from installer\google-client.json, never
    /// committed). Lets everyone who installs a release sign in without creating their own Google key.
    /// </summary>
    public const string BundledFileName = "google-client.json";

    public static GoogleClientFile? FindBundled() => TryLoad(System.IO.Path.Combine(AppContext.BaseDirectory, BundledFileName));

    /// <summary>Newest valid client file in the user's Downloads (and Desktop) folders, if any.</summary>
    public static GoogleClientFile? FindDownloaded()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[] { System.IO.Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        return dirs.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "client_secret*.json"))
            .Select(TryLoad)
            .Where(f => f != null)
            .OrderByDescending(f => f!.Modified)
            .FirstOrDefault();
    }
}
