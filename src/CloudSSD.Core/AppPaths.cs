namespace CloudSSD.Core;

/// <summary>
/// File-system locations used by CloudSSD. Profiles use %LOCALAPPDATA%\CloudSSD.
/// CLOUDSSD_HOME redirects portable and test profiles.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? dataRoot = null)
    {
        DataRoot = dataRoot
                   ?? Environment.GetEnvironmentVariable("CLOUDSSD_HOME")
                   ?? ResolveDefaultDataRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogDirectory);
    }

    public string DataRoot { get; }
    public static string ResolveDefaultDataRoot(string localAppData) => Path.Combine(localAppData, "CloudSSD");
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public string DatabaseFile => Path.Combine(DataRoot, "cloudssd.db");
    public string LogDirectory => Path.Combine(DataRoot, "logs");
    public string RcloneConfigFile => Path.Combine(DataRoot, "rclone.conf");
    public string ConfigPasswordFile => Path.Combine(DataRoot, "rclone.conf.key");
    public string MountStateFile => Path.Combine(DataRoot, "mounts.state.json");
    public string DefaultCacheRoot => Path.Combine(DataRoot, "cache");
    public string DefaultLocalGamesRoot => Path.Combine(DataRoot, "local-games");
    public string DiagnosticsDirectory => Path.Combine(DataRoot, "diagnostics");
    public string TeraBoxTokenFile => Path.Combine(DataRoot, "terabox.tokens.key");
    public string TeraBoxClientFile => Path.Combine(DataRoot, "terabox.client.key");
    public string TeraBoxBridgeSecretFile => Path.Combine(DataRoot, "terabox.bridge.key");
    /// <summary>Unofficial website session (cookie header, page token), DPAPI-protected.</summary>
    public string TeraBoxWebSessionFile => Path.Combine(DataRoot, "terabox.websession.key");
    /// <summary>WebView2 profile used only while the user signs in to the TeraBox website; cleared after the session is captured.</summary>
    public string TeraBoxSignInProfileDir => Path.Combine(DataRoot, "webview2-terabox");
    public string TeraBoxStagingDir(AppSettings s) => Path.Combine(CacheRootFor(s), "terabox-staging");

    public string CacheRootFor(AppSettings s) => string.IsNullOrWhiteSpace(s.Cache.CacheRoot) ? DefaultCacheRoot : s.Cache.CacheRoot!;

    public string MountCacheDir(AppSettings s, string mountId) => Path.Combine(CacheRootFor(s), "m-" + mountId);

    public string LocalGamesRootFor(AppSettings s) => string.IsNullOrWhiteSpace(s.LocalGamesRoot) ? DefaultLocalGamesRoot : s.LocalGamesRoot!;

    /// <summary>Directory containing the running executables (bundled rclone lives under tools\rclone).</summary>
    public static string AppDirectory => AppContext.BaseDirectory;
}
