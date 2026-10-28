using System.Globalization;

namespace CloudSSD.Core;

/// <summary>Byte-size helpers. All sizes are binary (1 KiB = 1024 B) unless stated otherwise.</summary>
public static class ByteSize
{
    public const long KiB = 1024;
    public const long MiB = 1024 * KiB;
    public const long GiB = 1024 * MiB;
    public const long TiB = 1024 * GiB;

    public static string Format(long bytes)
    {
        if (bytes < 0) return "-" + Format(-bytes);
        if (bytes < KiB) return $"{bytes} B";
        if (bytes < MiB) return $"{bytes / (double)KiB:0.0} KiB";
        if (bytes < GiB) return $"{bytes / (double)MiB:0.0} MiB";
        if (bytes < TiB) return $"{bytes / (double)GiB:0.00} GiB";
        return $"{bytes / (double)TiB:0.00} TiB";
    }

    public static string Format(long? bytes) => bytes is { } b ? Format(b) : "Unavailable";

    /// <summary>Renders a size the way rclone's SizeSuffix parser accepts it (base-1024 suffixes).</summary>
    public static string ToRclone(long bytes)
    {
        if (bytes <= 0) return "0";
        if (bytes % GiB == 0) return (bytes / GiB).ToString(CultureInfo.InvariantCulture) + "Gi";
        if (bytes % MiB == 0) return (bytes / MiB).ToString(CultureInfo.InvariantCulture) + "Mi";
        if (bytes % KiB == 0) return (bytes / KiB).ToString(CultureInfo.InvariantCulture) + "Ki";
        // A bare integer is KiB to rclone, not bytes. Disk-safety budgets are often not a round KiB.
        return bytes.ToString(CultureInfo.InvariantCulture) + "B";
    }

    /// <summary>Parses values such as "10G", "512Mi", "1.5GiB", "4096".</summary>
    public static long Parse(string text)
    {
        if (!TryParse(text, out var v)) throw new FormatException($"Invalid size: '{text}'");
        return v;
    }

    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().Replace(" ", "");
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0) return false;
        if (!double.TryParse(s[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return false;
        var suffix = s[i..].ToUpperInvariant().TrimEnd('B').TrimEnd('I');
        long mul = suffix switch
        {
            "" => 1,
            "K" => KiB,
            "M" => MiB,
            "G" => GiB,
            "T" => TiB,
            _ => -1
        };
        if (mul < 0) return false;
        bytes = (long)(num * mul);
        return true;
    }
}

/// <summary>
/// Network bandwidth helpers. Internet plans are quoted in megabits per second (Mbps, base 10);
/// file transfers are measured in bytes. 70 Mbps = 70,000,000 bits/s = 8,750,000 B/s ≈ 8.34 MiB/s.
/// </summary>
public static class Bandwidth
{
    public static double MbpsToBytesPerSecond(double mbps) => mbps * 1_000_000.0 / 8.0;

    public static double BytesPerSecondToMbps(double bytesPerSecond) => bytesPerSecond * 8.0 / 1_000_000.0;

    /// <summary>Megabytes per second (base 10, MB/s) as shown by most download tools.</summary>
    public static double BytesPerSecondToMBps(double bytesPerSecond) => bytesPerSecond / 1_000_000.0;

    /// <summary>
    /// Estimated transfer time for <paramref name="bytes"/> at <paramref name="mbps"/> with a protocol/efficiency factor
    /// (HTTPS + provider overhead usually yields 80-90% of line rate) and a per-request latency cost.
    /// </summary>
    public static TimeSpan EstimateTransfer(long bytes, double mbps, double efficiency = 0.85, int requests = 0, double latencyMs = 0)
    {
        if (mbps <= 0) return TimeSpan.MaxValue;
        var bps = MbpsToBytesPerSecond(mbps) * Math.Clamp(efficiency, 0.05, 1.0);
        return TimeSpan.FromSeconds(bytes / bps + requests * latencyMs / 1000.0);
    }

    public static string FormatRate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } b || double.IsNaN(b)) return "Unavailable";
        return $"{BytesPerSecondToMBps(b):0.00} MB/s ({BytesPerSecondToMbps(b):0.0} Mbps)";
    }

    public static string FormatDuration(TimeSpan t)
    {
        if (t == TimeSpan.MaxValue) return "unknown";
        if (t.TotalSeconds < 60) return $"{t.TotalSeconds:0} s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} min {t.Seconds} s";
        return $"{(int)t.TotalHours} h {t.Minutes} min";
    }
}
