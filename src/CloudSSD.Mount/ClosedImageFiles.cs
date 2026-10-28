using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloudSSD.Mount;

/// <summary>Distinguishes inactive executable mappings from active readers using Windows Restart Manager and sharing checks.</summary>
internal static class ClosedImageFiles
{
    public static bool CanRelease(string root, IReadOnlyList<string> relativePaths)
    {
        if (!OperatingSystem.IsWindows() || relativePaths.Count is 0 or > 32) return false;
        var prefix = Path.GetFullPath(root).TrimEnd('\\') + '\\';
        var files = new List<string>();
        var handles = new List<SafeFileHandle>();
        try
        {
            foreach (var relative in relativePaths)
            {
                if (Path.IsPathRooted(relative) || relative.Contains(':')) return false;
                var path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', '\\')));
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                var file = Path.GetFileName(path);
                if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) return false;
                files.Add(path);
            }
            // Restart Manager can omit the calling process; explicitly check its mapped views too.
            if (HasCurrentProcessMapping(prefix) || !HasNoUsers(files.ToArray())) return false;
            foreach (var file in files)
                handles.Add(File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.None));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }

    private static bool HasNoUsers(string[] files)
    {
        var key = new System.Text.StringBuilder(33);
        if (RmStartSession(out var session, 0, key) != 0) return false;
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return false;
            uint count = 0;
            var result = RmGetList(session, out var needed, ref count, IntPtr.Zero, out _);
            // Any application using a resource, or any incomplete/error result, keeps the drive mounted.
            return result == 0 && needed == 0 && count == 0;
        }
        finally { RmEndSession(session); }
    }

    private static bool HasCurrentProcessMapping(string root)
    {
        var device = new System.Text.StringBuilder(4096);
        if (QueryDosDevice(root[..2], device, device.Capacity) == 0) return true;
        var prefix = device.ToString().Split('\0')[0].TrimEnd('\\') + '\\';
        nuint address = 0;
        var name = new System.Text.StringBuilder(4096);
        while (VirtualQuery((IntPtr)address, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) != 0)
        {
            if (region.State == 0x1000 && region.Type is 0x40000 or 0x1000000)
            {
                name.Clear();
                if (GetMappedFileName((IntPtr)(-1), region.BaseAddress, name, name.Capacity) > 0 &&
                    name.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            var next = (nuint)region.BaseAddress + region.RegionSize;
            if (next <= address) break;
            address = next;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string device, System.Text.StringBuilder target, int length);
    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(IntPtr address, out MemoryRegion region, nuint length);
    [DllImport("psapi.dll", EntryPoint = "GetMappedFileNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetMappedFileName(IntPtr process, IntPtr address, System.Text.StringBuilder name, int length);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, System.Text.StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint appCount, IntPtr apps, uint serviceCount, IntPtr services);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, IntPtr processes, out uint reasons);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
