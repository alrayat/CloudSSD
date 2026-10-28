using System.Diagnostics;
using CloudSSD.Core;

namespace CloudSSD.Games;

/// <summary>
/// Walks a game folder (on the cloud drive this costs directory listings only, no file data) and ranks candidate
/// executables. PE headers are read for the top candidates only (a few KiB each).
/// </summary>
public static class GameScanner
{
    private static readonly string[] ExcludedExePatterns =
    [
        "unins", "setup", "install", "vc_redist", "vcredist", "dxsetup", "dxwebsetup", "crash", "report", "ue4prereq", "ueprereq",
        "dotnet", "redist", "easyanticheat", "beservice", "battleye", "updater", "patcher", "cefsharp", "qtwebengine", "helper",
        "uninstall", "dxsdk", "oalinst", "physx", "touchup", "cleanup", "benchmark", "server", "dedicated", "notification"
    ];

    public static ScanResult Scan(string root, int maxEntries = 250_000, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var files = new List<FileStat>();
        int dirs = 0;
        bool truncated = false;
        var markers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir, "*", opts))
                {
                    dirs++;
                    var name = Path.GetFileName(d);
                    DetectDirMarker(name, markers);
                    stack.Push(d);
                }
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
                {
                    files.Add(new FileStat(Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc));
                    DetectFileMarker(f.Name, markers);
                    if (files.Count >= maxEntries) { truncated = true; stack.Clear(); break; }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        var engine = DetectEngine(files, markers);
        var folderName = Path.GetFileName(root.TrimEnd('\\', '/'));
        var candidates = files
            .Where(f => f.RelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(f => (f, score: ScoreName(f, folderName)))
            .Where(x => x.score > int.MinValue)
            .OrderByDescending(x => x.score).ThenByDescending(x => x.f.Size)
            .Take(8)
            .Select(x =>
            {
                var pe = PeInspector.TryRead(Path.Combine(root, x.f.RelativePath));
                var score = x.score + (pe?.Subsystem == PeSubsystem.Gui ? 2 : pe?.Subsystem == PeSubsystem.Console ? -1 : 0) + (pe is null ? -5 : 0);
                var reason = $"{(pe?.Subsystem ?? PeSubsystem.Unknown)} {pe?.Machine}, {ByteSize.Format(x.f.Size)}";
                return new ExecutableCandidate(x.f.RelativePath, x.f.Size, score, pe?.Machine ?? PeMachine.Unknown, pe?.Subsystem ?? PeSubsystem.Unknown, reason);
            })
            .OrderByDescending(c => c.Score).ThenByDescending(c => c.Size)
            .ToList();

        return new ScanResult(root, files.Sum(f => f.Size), files.Count, dirs, files, candidates, markers.ToList(), engine, truncated, sw.Elapsed);
    }

    private static int ScoreName(FileStat f, string folderName)
    {
        var name = Path.GetFileNameWithoutExtension(f.RelativePath).ToLowerInvariant();
        if (ExcludedExePatterns.Any(p => name.Contains(p))) return int.MinValue;
        var path = f.RelativePath.ToLowerInvariant();
        if (path.Contains("_commonredist") || path.Contains("/redist") || path.Contains("directx/") || path.Contains("easyanticheat")) return int.MinValue;
        int score = 0;
        var depth = f.RelativePath.Count(c => c == '/');
        if (depth == 0) score += 3;
        if (path.Contains("binaries/win64") || path.Contains("bin/x64") || path.Contains("bin64") || path.Contains("x64/")) score += 2;
        var norm = new string(folderName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var nn = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (norm.Length > 2 && (nn.Contains(norm) || norm.Contains(nn))) score += 3;
        if (name.Contains("shipping")) score += 2;
        if (name.Contains("launcher")) score -= 2;
        score += (int)Math.Min(5, f.Size / (10 * ByteSize.MiB));
        score -= Math.Max(0, depth - 3);
        return score;
    }

    private static void DetectDirMarker(string name, SortedSet<string> m)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("easyanticheat")) m.Add("anticheat:EasyAntiCheat");
        else if (n.Contains("battleye")) m.Add("anticheat:BattlEye");
        else if (n is "_commonredist" or "redist" or "redistributables") m.Add("redist-folder");
        else if (n.EndsWith("_data")) m.Add("unity-data-folder");
        else if (n == "steamworks shared") m.Add("launcher:Steam");
    }

    private static void DetectFileMarker(string file, SortedSet<string> m)
    {
        var n = file.ToLowerInvariant();
        switch (n)
        {
            case "steam_api.dll" or "steam_api64.dll": m.Add("launcher:Steam"); break;
            case "steam_appid.txt": m.Add("steam_appid.txt"); break;
            case "eossdk-win64-shipping.dll" or "eossdk-win32-shipping.dll": m.Add("launcher:EpicOnlineServices"); break;
            case "uplay_r1_loader64.dll" or "upc_r2_loader64.dll" or "uplay_r1_loader.dll": m.Add("launcher:Ubisoft"); break;
            case "galaxy64.dll" or "galaxy.dll": m.Add("sdk:GOG-Galaxy"); break;
            case "unityplayer.dll": m.Add("engine:Unity"); break;
            case "beservice.exe" or "beservice_x64.exe" or "beclient_x64.dll": m.Add("anticheat:BattlEye"); break;
            case "easyanticheat.exe" or "easyanticheat_x64.dll" or "easyanticheat_eos.exe": m.Add("anticheat:EasyAntiCheat"); break;
            case "pnkbstra.exe" or "pnkbstrb.exe": m.Add("anticheat:PunkBuster"); break;
            case "vgk.sys" or "vgc.exe": m.Add("anticheat:Vanguard"); break;
            case "xigncode.dll" or "x3.xem": m.Add("anticheat:XIGNCODE"); break;
            case "gameguard.des" or "npgamemon.des": m.Add("anticheat:nProtect"); break;
            case "setup.exe" or "install.exe": m.Add("installer"); break;
            case "unins000.exe": m.Add("installed-by-inno"); break;
        }
        if (n.EndsWith(".msi")) m.Add("installer");
        else if (n.EndsWith(".rpf")) m.Add("engine:RAGE");
        else if (n.EndsWith(".vpk")) m.Add("engine:Source");
        else if (n.EndsWith(".archive")) m.Add("engine:REDengine-archive");
        else if (n.EndsWith(".pak") || n.EndsWith(".utoc") || n.EndsWith(".ucas")) m.Add("packed-archives");
    }

    private static string? DetectEngine(List<FileStat> files, SortedSet<string> markers)
    {
        if (markers.Contains("engine:Unity")) return "Unity";
        if (files.Any(f => f.RelativePath.Contains("Engine/Binaries/Win64", StringComparison.OrdinalIgnoreCase))
            || files.Any(f => f.RelativePath.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))) return "Unreal Engine";
        if (markers.Contains("engine:RAGE")) return "RAGE";
        if (markers.Contains("engine:REDengine-archive")) return "REDengine";
        if (markers.Contains("engine:Source")) return "Source";
        return null;
    }
}
