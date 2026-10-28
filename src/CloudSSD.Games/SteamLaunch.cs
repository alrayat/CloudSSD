using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CloudSSD.Games;

/// <summary>Launches an installed Steam title through its client and tracks the game, never the Steam process.</summary>
public static class SteamLaunch
{
    public static uint? DetectAppId(string gameRoot)
    {
        var root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar);
        var common = Directory.GetParent(root);
        if (common == null || !common.Name.Equals("common", StringComparison.OrdinalIgnoreCase) ||
            common.Parent is not { } apps || !apps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var file in Directory.EnumerateFiles(apps.FullName, "appmanifest_*.acf"))
        {
            try
            {
                var text = File.ReadAllText(file);
                var dir = Field(text, "installdir");
                if (dir != null && dir.Equals(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(Field(text, "appid"), out var id) && id > 0) return id;
            }
            catch (IOException) { }
        }
        return null;
    }

    private static string? Field(string text, string key)
    {
        var match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string? FindClient()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var configured = key?.GetValue("SteamExe") as string;
        if (configured != null && File.Exists(configured)) return configured;
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    public static Process? FindGame(string executable)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            try
            {
                if (!p.HasExited && string.Equals(p.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return p;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            p.Dispose();
        }
        return null;
    }

    public static async Task<Process> StartAsync(uint appId, string executable, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FindGame(executable) is { } running) return running;
        var client = FindClient() ?? throw new InvalidOperationException("Steam is required for this game. Install Steam and sign in to the account that owns it.");
        // Steam owns authentication, updates and launcher hand-over. Never rewrite its manifests or restart its client.
        using (Process.Start(new ProcessStartInfo(client)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(client)!,
            Arguments = $"-applaunch {appId} {arguments}"
        })) { }
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (FindGame(executable) is { } game) return game;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("Steam did not start the selected game from this folder. Check Steam for a sign-in, ownership, update or installation prompt. The game is not marked Running.");
    }
}
