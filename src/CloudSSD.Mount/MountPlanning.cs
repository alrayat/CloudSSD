using System.Globalization;
using CloudSSD.Core;

namespace CloudSSD.Mount;

/// <summary>A permanent drive's fixed letter is held by another drive; the mount waits rather than taking another letter.</summary>
public sealed class DriveLetterConflictException(char letter)
    : InvalidOperationException($"Drive letter {letter}: is used by another drive. CloudSSD will not take it over or move this drive; " +
                                $"free {letter}: or choose another letter for this drive.")
{
    public char Letter { get; } = letter;
}

public static class DriveLetterAllocator
{
    private static readonly char[] Preference = "GXYZWVUTSRQPONMLKJIH".ToCharArray();

    public static IReadOnlySet<char> UsedLetters()
    {
        var used = new HashSet<char>();
        foreach (var d in DriveInfo.GetDrives()) used.Add(char.ToUpperInvariant(d.Name[0]));
        return used;
    }

    public static IReadOnlyList<char> FreeLetters(IReadOnlySet<char>? used = null)
    {
        used ??= UsedLetters();
        return "DEFGHIJKLMNOPQRSTUVWXYZ".Where(c => !used.Contains(c)).ToList();
    }

    /// <summary>Returns the requested letter if free, otherwise the first free letter by preference (G first, then X..H).</summary>
    public static char? Choose(string? requested, IReadOnlySet<char>? used = null)
    {
        used ??= UsedLetters();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var c = char.ToUpperInvariant(requested.Trim()[0]);
            if (c is >= 'D' and <= 'Z' && !used.Contains(c)) return c;
        }
        foreach (var c in Preference) if (!used.Contains(c)) return c;
        return null;
    }
}

public sealed record MountPlan(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?> Environment, string CacheDir, string LogFile,
    long EffectiveCacheMaxBytes, IReadOnlyList<string> Warnings);

public sealed record MountPlanInput(
    MountDefinition Mount,
    char DriveLetter,
    CacheSettings Cache,
    NetworkSettings Network,
    string CacheDir,
    string LogFile,
    long EffectiveCacheMaxBytes,
    bool Telemetry,
    int RcPort,
    string? RemoteType);

/// <summary>
/// Translates CloudSSD settings into rclone mount arguments. Every optional flag is checked against the flags the
/// installed rclone reports, so an older rclone gets a warning instead of a failed mount.
/// </summary>
public static class MountFlagBuilder
{
    public static MountPlan Build(MountPlanInput i, RcloneInfo rclone)
    {
        var warnings = new List<string>();
        var a = new List<string> { "mount", i.Mount.RemoteSpec, $"{i.DriveLetter}:" };

        void Flag(string name, string? value = null)
        {
            if (!rclone.Supports(name))
            {
                warnings.Add($"rclone {rclone.Version} does not support --{name}; skipped.");
                return;
            }
            a.Add("--" + name);
            if (value != null) a.Add(value);
        }

        Flag("vfs-cache-mode", "full");
        Flag("cache-dir", i.CacheDir);
        Flag("vfs-cache-max-size", ByteSize.ToRclone(Math.Max(16 * ByteSize.MiB, i.EffectiveCacheMaxBytes)));
        if (i.Cache.MinFreeSpaceBytes > 0) Flag("vfs-cache-min-free-space", ByteSize.ToRclone(i.Cache.MinFreeSpaceBytes));
        Flag("vfs-cache-max-age", Dur(i.Cache.MaxAge));
        Flag("vfs-cache-poll-interval", Dur(i.Cache.PollInterval));
        Flag("buffer-size", ByteSize.ToRclone(i.Cache.BufferSizeBytes));
        Flag("vfs-read-ahead", ByteSize.ToRclone(i.Cache.ReadAheadBytes));
        Flag("vfs-read-chunk-size", ByteSize.ToRclone(i.Cache.ReadChunkSizeBytes));
        if (i.Cache.ReadChunkStreams > 0) Flag("vfs-read-chunk-streams", i.Cache.ReadChunkStreams.ToString(CultureInfo.InvariantCulture));
        Flag("vfs-write-back", Dur(i.Cache.WriteBack));
        Flag("dir-cache-time", "30m");
        // The bundled rclone keeps half of its upload workers for files that are fully cached; the other half may wait
        // on cloud reads to complete partly cached files. With rclone's default of 4 that left 2 workers, slower than a
        // Steam download (2026-10-21: 59 complete files queued, D: filling at 9 MB/s). 8 gives 4 + 4.
        if (!i.Mount.ExtraFlags.Keys.Any(k => k.TrimStart('-').Equals("transfers", StringComparison.OrdinalIgnoreCase)))
            Flag("transfers", "8");
        // WinFsp otherwise allocates one dispatcher per CPU. A few slow reads/closes can then
        // occupy every dispatcher and block even Explorer's metadata requests (measured on 8 CPUs).
        if (OperatingSystem.IsWindows() && !i.Mount.ExtraFlags.Keys.Any(k => k.TrimStart('-').Equals("option", StringComparison.OrdinalIgnoreCase)))
            Flag("option", "ThreadCount=64");
        // Executables need the execute bit in the WinFsp ACL mapping; default 0666 makes CreateProcess fail with
        // "Access is denied" (verified on rclone 1.75.2 / WinFsp 2025).
        Flag("file-perms", "0777");
        Flag("dir-perms", "0777");
        // Keep the label on one Explorer tile line so its free/total capacity text has room.
        Flag("volname", Sanitize(i.Mount.DisplayName));
        if (i.Mount.ReadOnly) Flag("read-only");

        if (i.Network.HardLimitMbps > 0)
            Flag("bwlimit", ((long)Bandwidth.MbpsToBytesPerSecond(i.Network.HardLimitMbps)).ToString(CultureInfo.InvariantCulture) + "B");
        if (i.Network.TpsLimit > 0 && i.RemoteType is "drive" or null)
            Flag("tpslimit", i.Network.TpsLimit.ToString(CultureInfo.InvariantCulture));

        Flag("rc");
        Flag("rc-addr", $"127.0.0.1:{i.RcPort}");
        Flag("log-file", i.LogFile);
        Flag("log-format", "date,time,microseconds");
        if (rclone.Supports("log-file-max-size")) { Flag("log-file-max-size", "64Mi"); Flag("log-file-max-backups", "2"); }
        a.Add(i.Telemetry ? "-vv" : "-v");

        foreach (var (k, v) in i.Mount.ExtraFlags)
        {
            var name = k.TrimStart('-');
            if (IsForbiddenExtraFlag(name))
            {
                warnings.Add($"Extra flag --{name} is not allowed and was ignored.");
                continue;
            }
            Flag(name, string.IsNullOrEmpty(v) ? null : v);
        }

        return new MountPlan(a, new Dictionary<string, string?>(), i.CacheDir, i.LogFile, i.EffectiveCacheMaxBytes, warnings);
    }

    /// <summary>Flags that could execute commands, expose the rc server or redirect credentials.</summary>
    public static bool IsForbiddenExtraFlag(string name) =>
        name.Contains("password-command", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("rc", StringComparison.OrdinalIgnoreCase)
        || name.Equals("config", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("cache-dir", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("log-file", StringComparison.OrdinalIgnoreCase)
        || name.Contains("command", StringComparison.OrdinalIgnoreCase);

    public static string Dur(TimeSpan t) => t.TotalHours >= 1 && t.TotalHours == Math.Floor(t.TotalHours)
        ? $"{(long)t.TotalHours}h"
        : t.TotalSeconds >= 1 ? $"{(long)t.TotalSeconds}s" : $"{(long)t.TotalMilliseconds}ms";

    private static string Sanitize(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').Take(32).ToArray());
}
