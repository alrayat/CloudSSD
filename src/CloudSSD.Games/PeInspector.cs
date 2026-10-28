using System.Text;

namespace CloudSSD.Games;

public enum PeMachine { Unknown, X86, X64, Arm64 }
public enum PeSubsystem { Unknown, Gui, Console, Other }

public sealed record PeInfo(PeMachine Machine, PeSubsystem Subsystem, bool IsDotNet, IReadOnlyList<string> Imports, IReadOnlyList<string> DelayImports)
{
    public IEnumerable<string> AllImports => Imports.Concat(DelayImports).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Minimal PE parser: machine, subsystem, .NET flag and imported DLL names. It reads only headers and the import
/// directory with a handful of small random-access reads, so inspecting an EXE on the cloud drive downloads kilobytes,
/// not the whole file.
/// </summary>
public static class PeInspector
{
    private sealed record Section(uint VirtualAddress, uint VirtualSize, uint RawPointer, uint RawSize);

    public static PeInfo? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            return Read(fs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static PeInfo? Read(Stream s)
    {
        var br = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        if (s.Length < 0x40) return null;
        s.Position = 0;
        if (br.ReadUInt16() != 0x5A4D) return null; // MZ
        s.Position = 0x3C;
        var peOffset = br.ReadInt32();
        if (peOffset <= 0 || peOffset > s.Length - 24) return null;
        s.Position = peOffset;
        if (br.ReadUInt32() != 0x00004550) return null; // PE\0\0
        var machine = br.ReadUInt16() switch
        {
            0x014c => PeMachine.X86,
            0x8664 => PeMachine.X64,
            0xAA64 => PeMachine.Arm64,
            _ => PeMachine.Unknown
        };
        var sections = br.ReadUInt16();
        s.Position += 12;
        var optSize = br.ReadUInt16();
        s.Position += 2; // characteristics
        var optStart = s.Position;
        var magic = br.ReadUInt16();
        var pe32Plus = magic == 0x20b;
        if (magic != 0x10b && !pe32Plus) return null;
        s.Position = optStart + 68;
        var subsystem = br.ReadUInt16() switch
        {
            2 => PeSubsystem.Gui,
            3 => PeSubsystem.Console,
            0 => PeSubsystem.Unknown,
            _ => PeSubsystem.Other
        };
        var dirStart = optStart + (pe32Plus ? 112 : 96);
        (uint Rva, uint Size) Dir(int i)
        {
            s.Position = dirStart + i * 8;
            return (br.ReadUInt32(), br.ReadUInt32());
        }
        var numberOfRva = 16;
        var importDir = Dir(1);
        var delayDir = numberOfRva > 13 ? Dir(13) : (0u, 0u);
        var clrDir = numberOfRva > 14 ? Dir(14) : (0u, 0u);

        s.Position = optStart + optSize;
        var secs = new List<Section>();
        for (int i = 0; i < sections && i < 96; i++)
        {
            s.Position += 8;
            var vsize = br.ReadUInt32();
            var va = br.ReadUInt32();
            var rawSize = br.ReadUInt32();
            var rawPtr = br.ReadUInt32();
            s.Position += 16;
            secs.Add(new Section(va, vsize, rawPtr, rawSize));
        }

        long RvaToOffset(uint rva)
        {
            foreach (var sec in secs)
            {
                var size = Math.Max(sec.VirtualSize, sec.RawSize);
                if (rva >= sec.VirtualAddress && rva < sec.VirtualAddress + size)
                    return sec.RawPointer + (rva - sec.VirtualAddress);
            }
            return -1;
        }

        string? ReadName(uint rva)
        {
            var off = RvaToOffset(rva);
            if (off < 0 || off >= s.Length) return null;
            s.Position = off;
            var sb = new StringBuilder();
            for (int i = 0; i < 260; i++)
            {
                var b = br.ReadByte();
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        List<string> ReadImports(uint dirRva, int descriptorSize, int nameOffset)
        {
            var names = new List<string>();
            if (dirRva == 0) return names;
            var off = RvaToOffset(dirRva);
            if (off < 0) return names;
            for (int i = 0; i < 1024; i++)
            {
                s.Position = off + i * descriptorSize;
                var desc = br.ReadBytes(descriptorSize);
                if (desc.Length < descriptorSize || desc.All(b => b == 0)) break;
                var nameRva = BitConverter.ToUInt32(desc, nameOffset);
                if (nameRva == 0) break;
                if (ReadName(nameRva) is { } n) names.Add(n);
            }
            return names;
        }

        var imports = ReadImports(importDir.Rva, 20, 12);
        var delay = ReadImports(delayDir.Item1, 32, 4);
        return new PeInfo(machine, subsystem, clrDir.Item1 != 0, imports, delay);
    }
}

public sealed record DependencyIssue(string Dll, string Problem, string? Remedy);

/// <summary>Checks a PE's imports against the game folder and Windows system directories.</summary>
public static class DependencyResolver
{
    private static readonly (string Prefix, string Package, string Url)[] Redists =
    [
        ("msvcp140", "Microsoft Visual C++ 2015-2022 Redistributable", "https://aka.ms/vs/17/release/vc_redist.{arch}.exe"),
        ("vcruntime140", "Microsoft Visual C++ 2015-2022 Redistributable", "https://aka.ms/vs/17/release/vc_redist.{arch}.exe"),
        ("concrt140", "Microsoft Visual C++ 2015-2022 Redistributable", "https://aka.ms/vs/17/release/vc_redist.{arch}.exe"),
        ("msvcp120", "Microsoft Visual C++ 2013 Redistributable", "https://aka.ms/highdpimfc2013x64enu"),
        ("msvcr120", "Microsoft Visual C++ 2013 Redistributable", "https://aka.ms/highdpimfc2013x64enu"),
        ("msvcp110", "Microsoft Visual C++ 2012 Redistributable", "https://www.microsoft.com/download/details.aspx?id=30679"),
        ("msvcr110", "Microsoft Visual C++ 2012 Redistributable", "https://www.microsoft.com/download/details.aspx?id=30679"),
        ("msvcp100", "Microsoft Visual C++ 2010 Redistributable", "https://www.microsoft.com/download/details.aspx?id=26999"),
        ("msvcr100", "Microsoft Visual C++ 2010 Redistributable", "https://www.microsoft.com/download/details.aspx?id=26999"),
        ("d3dx9_", "DirectX End-User Runtime (June 2010)", "https://www.microsoft.com/download/details.aspx?id=8109"),
        ("d3dx10_", "DirectX End-User Runtime (June 2010)", "https://www.microsoft.com/download/details.aspx?id=8109"),
        ("d3dx11_", "DirectX End-User Runtime (June 2010)", "https://www.microsoft.com/download/details.aspx?id=8109"),
        ("d3dcompiler_43", "DirectX End-User Runtime (June 2010)", "https://www.microsoft.com/download/details.aspx?id=8109"),
        ("xinput1_3", "DirectX End-User Runtime (June 2010)", "https://www.microsoft.com/download/details.aspx?id=8109"),
        ("xaudio2_7", "DirectX End-User Runtime (June 2010)", "https://www.microsoft.com/download/details.aspx?id=8109"),
        ("x3daudio1_7", "DirectX End-User Runtime (June 2010)", "https://www.microsoft.com/download/details.aspx?id=8109"),
        ("physxloader", "NVIDIA PhysX Legacy System Software", "https://www.nvidia.com/en-us/drivers/physx/physx-9-13-0604-legacy-driver/"),
        ("openal32", "OpenAL runtime", "https://www.openal.org/downloads/"),
    ];

    public static IReadOnlyList<DependencyIssue> Check(PeInfo pe, string exeDirectory, string gameRoot)
    {
        var issues = new List<DependencyIssue>();
        var system = pe.Machine == PeMachine.X86
            ? Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
            : Environment.GetFolderPath(Environment.SpecialFolder.System);
        var arch = pe.Machine == PeMachine.X86 ? "x86" : "x64";
        foreach (var dll in pe.AllImports)
        {
            var lower = dll.ToLowerInvariant();
            if (lower.StartsWith("api-ms-win-") || lower.StartsWith("ext-ms-")) continue;
            if (File.Exists(Path.Combine(exeDirectory, dll)) || File.Exists(Path.Combine(gameRoot, dll))) continue;
            if (File.Exists(Path.Combine(system, dll))) continue;
            var redist = Redists.FirstOrDefault(r => lower.StartsWith(r.Prefix));
            issues.Add(redist.Package != null
                ? new DependencyIssue(dll, $"Not installed (part of {redist.Package} {arch})", $"Install {redist.Package} ({arch}): {redist.Url.Replace("{arch}", arch)}")
                : new DependencyIssue(dll, "Not found in the game folder or Windows", "It may be installed by the game's own installer or a redistributable in its _CommonRedist/Redist folder."));
        }
        if (pe.IsDotNet)
            issues.Add(new DependencyIssue("(.NET)", "This executable is a .NET application", "It may require a specific .NET runtime; check the game's requirements."));
        return issues;
    }
}
