using System.Diagnostics;
using CloudSSD.Core;
using Microsoft.Win32;

namespace CloudSSD.Diagnostics;

public enum DependencyState { Ok, Warning, Missing, Error }

public sealed record DependencyStatus(string Name, DependencyState State, string Detail, string? Remedy = null);

public sealed record WinFspInfo(bool Installed, string? InstallDir, string? Version, string? Dll);

public static class WinFspDetector
{
    public static WinFspInfo Detect()
    {
        string? dir = null;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(@"SOFTWARE\WinFsp");
            dir = key?.GetValue("InstallDir") as string;
            if (dir != null) break;
        }
        dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp");
        var dll = Path.Combine(dir, "bin", "winfsp-x64.dll");
        if (!File.Exists(dll)) return new WinFspInfo(false, null, null, null);
        var vi = FileVersionInfo.GetVersionInfo(dll);
        var version = $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart} (product {vi.ProductVersion})";
        return new WinFspInfo(true, dir, version, dll);
    }
}

/// <summary>Checks the runtime prerequisites needed for mounting, caching and launching.</summary>
public static class DependencyChecker
{
    public static bool IsDeveloperModeEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
        return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int v && v == 1;
    }

    public static bool IsAdministrator()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>True when the current process can create unprivileged symbolic links (Developer Mode or admin).</summary>
    public static bool CanCreateSymlinks() => IsDeveloperModeEnabled() || IsAdministrator();

    public static bool IsVcRedistInstalled(bool x64)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = hklm.OpenSubKey($@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{(x64 ? "x64" : "x86")}");
        return key?.GetValue("Installed") is int v && v == 1;
    }

    public static IReadOnlyList<DependencyStatus> CheckAll(string? rclonePath, string? rcloneVersion, string cacheRoot, long minFreeBytes)
    {
        var list = new List<DependencyStatus>();

        var winfsp = WinFspDetector.Detect();
        list.Add(winfsp.Installed
            ? new DependencyStatus("WinFsp", DependencyState.Ok, $"Installed at {winfsp.InstallDir}, version {winfsp.Version}")
            : new DependencyStatus("WinFsp", DependencyState.Missing, "WinFsp is required to create the virtual drive.",
                "Install WinFsp from the official site https://winfsp.dev/rel/ (or run: winget install WinFsp.WinFsp)."));

        list.Add(rclonePath != null
            ? new DependencyStatus("rclone", DependencyState.Ok, $"{rcloneVersion ?? "unknown version"} at {rclonePath}")
            : new DependencyStatus("rclone", DependencyState.Missing, "rclone executable not found.",
                "Reinstall CloudSSD or set a custom rclone path in Settings → Advanced."));

        try
        {
            Directory.CreateDirectory(cacheRoot);
            var (fs, sparse) = DiskInfo.VolumeFileSystem(cacheRoot);
            list.Add(fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && sparse
                ? new DependencyStatus("Cache volume", DependencyState.Ok, $"{fs} with sparse-file support ({DiskInfo.VolumeRoot(cacheRoot)})")
                : new DependencyStatus("Cache volume", DependencyState.Warning, $"{fs} (sparse files: {sparse}). Partial downloads may occupy full file size.",
                    "Choose a cache folder on an NTFS drive."));

            var (free, total) = DiskInfo.FreeSpace(cacheRoot);
            list.Add(free > minFreeBytes
                ? new DependencyStatus("Cache free space", DependencyState.Ok, $"{ByteSize.Format(free)} free of {ByteSize.Format(total)}")
                : new DependencyStatus("Cache free space", DependencyState.Warning,
                    $"Only {ByteSize.Format(free)} free; protected reserve is {ByteSize.Format(minFreeBytes)}. Caching will be limited.",
                    "Move the cache to a drive with more free space (Settings → Cache)."));
        }
        catch (Exception e)
        {
            list.Add(new DependencyStatus("Cache volume", DependencyState.Error, e.Message, "Check that the cache folder is writable."));
        }

        var dev = IsDeveloperModeEnabled();
        list.Add(dev || IsAdministrator()
            ? new DependencyStatus("Symbolic links", DependencyState.Ok, dev ? "Developer Mode enabled (unprivileged symlinks)" : "Running as administrator")
            : new DependencyStatus("Symbolic links", DependencyState.Warning, "Hybrid Optimized mode needs symbolic links.",
                "Enable Windows Developer Mode (Settings → System → For developers). CloudSSD will not change this for you."));

        list.Add(IsVcRedistInstalled(true)
            ? new DependencyStatus("VC++ 2015-2022 x64", DependencyState.Ok, "Installed")
            : new DependencyStatus("VC++ 2015-2022 x64", DependencyState.Warning, "Not detected; many games require it.",
                "Install from https://aka.ms/vs/17/release/vc_redist.x64.exe"));

        list.Add(new DependencyStatus(".NET runtime", DependencyState.Ok, Environment.Version.ToString()));
        return list;
    }
}
