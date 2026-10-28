using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CloudSSD.Core;

/// <summary>Windows disk queries: physical (allocated) size of sparse files, free space and volume filesystem.</summary>
public static partial class DiskInfo
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemNameBuffer, int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string lpszFileName, StringBuilder lpszVolumePathName, int cchBufferLength);

    private const uint InvalidFileSize = 0xFFFFFFFF;
    public const uint FILE_SUPPORTS_SPARSE_FILES = 0x00000040;

    /// <summary>
    /// Bytes actually allocated on disk. For sparse files (rclone VFS cache files) this is much smaller than the
    /// logical length when only parts of the file were downloaded.
    /// </summary>
    public static long PhysicalSize(string path)
    {
        var low = GetCompressedFileSizeW(LongPath(path), out var high);
        if (low == InvalidFileSize)
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 0) throw new Win32Exception(err, $"GetCompressedFileSize failed for {path}");
        }
        return ((long)high << 32) | low;
    }

    public static (long FreeForUser, long Total) FreeSpace(string anyPathOnVolume)
    {
        var dir = anyPathOnVolume;
        while (!Directory.Exists(dir))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        if (!GetDiskFreeSpaceExW(dir, out var free, out var total, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetDiskFreeSpaceEx failed for {dir}");
        return ((long)free, (long)total);
    }

    public static string VolumeRoot(string path)
    {
        var sb = new StringBuilder(300);
        var full = Path.GetFullPath(path);
        return GetVolumePathNameW(full, sb, sb.Capacity) ? sb.ToString() : Path.GetPathRoot(full)!;
    }

    public static (string FileSystem, bool SupportsSparse) VolumeFileSystem(string path)
    {
        var root = VolumeRoot(path);
        var fs = new StringBuilder(64);
        if (!GetVolumeInformationW(root, null, 0, out _, out _, out var flags, fs, fs.Capacity))
            return ("Unknown", false);
        return (fs.ToString(), (flags & FILE_SUPPORTS_SPARSE_FILES) != 0);
    }

    private static string LongPath(string p) => p.StartsWith(@"\\?\") || p.Length < 248 ? p : @"\\?\" + Path.GetFullPath(p);
}
