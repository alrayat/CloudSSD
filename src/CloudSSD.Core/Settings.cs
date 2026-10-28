using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudSSD.Core;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public CacheSettings Cache { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public StreamingSettings Streaming { get; set; } = new();
    public List<MountDefinition> Mounts { get; set; } = new();
    public string? RclonePathOverride { get; set; }
    public bool RestoreMountsOnStartup { get; set; } = true;
    public bool KeepMountsAfterExit { get; set; } = false;
    public bool EncryptRcloneConfig { get; set; } = true;
    public bool FirstRunCompleted { get; set; }
    public string? LocalGamesRoot { get; set; }
    public TeraBoxSettings TeraBox { get; set; } = new();
}

public sealed class TeraBoxSettings
{
    /// <summary>127.0.0.1 port of the TeraBox WebDAV bridge (fixed so rclone's remote URL and cache stay stable).</summary>
    public int BridgePort { get; set; }
    public string? Account { get; set; }
    public string? Plan { get; set; }

    /// <summary>"official" (Open Platform, developer credentials) or "web" (unofficial website session, chosen by the user).</summary>
    public string Transport { get; set; } = "official";

    /// <summary>TeraBox folder used as the drive root for the website connection (it can reach the whole TeraBox).</summary>
    public string WebRoot { get; set; } = "/CloudSSD";

    /// <summary>
    /// Store files larger than <see cref="ChunkSizeMiB"/> as several TeraBox objects (rclone chunker) so files above the
    /// plan's per-file limit fit; Windows still sees one file of the original size. Chosen by the account owner (2026-10-19).
    /// </summary>
    public bool ChunkLargeFiles { get; set; } = true;

    /// <summary>Size of each stored part. Must stay below the plan's per-file limit (4 GB on Free).</summary>
    public int ChunkSizeMiB { get; set; } = 1024;

    /// <summary>
    /// Parallel download streams per file. Zero uses rclone's sequential reader, which propagates interrupted ranges
    /// correctly; the 1.75.2 parallel reader can wait forever after an unexpected EOF during cache restoration.
    /// </summary>
    public int ReadStreams { get; set; } = 0;

    /// <summary>
    /// Minimum quiet time before a changed file on a TeraBox drive is uploaded. Installers (Steam) write a file in many
    /// separate sessions; with 5 s, half-written files went to TeraBox and then the whole file again (measured 2026-10-20).
    /// </summary>
    public int MinWriteBackSeconds { get; set; } = 180;
}

public sealed class CacheSettings
{
    /// <summary>Root directory for rclone VFS caches. Each mount gets its own subdirectory.</summary>
    public string? CacheRoot { get; set; }

    /// <summary>Total physical disk budget for all mount caches.</summary>
    public long BudgetBytes { get; set; } = 20 * ByteSize.GiB;

    /// <summary>Free space that must always remain on the cache volume for Windows.</summary>
    public long MinFreeSpaceBytes { get; set; } = 8 * ByteSize.GiB;

    /// <summary>How long unused cached data may stay before rclone evicts it.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// rclone --buffer-size. In full cache mode rclone reads buffer-size + read-ahead past every read position.
    /// Measured: default 16M causes large speculative downloads for random game reads, so the default here is 0 and
    /// the prediction engine owns read-ahead.
    /// </summary>
    public long BufferSizeBytes { get; set; } = 0;

    public long ReadAheadBytes { get; set; } = 0;

    public long ReadChunkSizeBytes { get; set; } = 4 * ByteSize.MiB;

    public int ReadChunkStreams { get; set; } = 0;

    public TimeSpan WriteBack { get; set; } = TimeSpan.FromSeconds(5);

    public List<string> PinnedPaths { get; set; } = new();

    public CacheSettings Clone()
    {
        var c = (CacheSettings)MemberwiseClone();
        c.PinnedPaths = new List<string>(PinnedPaths);
        return c;
    }
}

public sealed class NetworkSettings
{
    /// <summary>User-declared internet speed in megabits per second (used until a measurement exists).</summary>
    public double DeclaredDownloadMbps { get; set; } = 70;

    /// <summary>Last measured throughput (Mbps), null if never measured.</summary>
    public double? MeasuredDownloadMbps { get; set; }
    public double? MeasuredLatencyMs { get; set; }
    public DateTimeOffset? MeasuredAt { get; set; }

    /// <summary>Maximum share of bandwidth background (P2/P3) prefetch may use.</summary>
    public double BackgroundShare { get; set; } = 0.6;

    /// <summary>Optional hard rclone --bwlimit in Mbps (0 = unlimited).</summary>
    public double HardLimitMbps { get; set; } = 0;

    public int MaxPrefetchConcurrency { get; set; } = 4;

    /// <summary>rclone --tpslimit for provider API calls (0 = unlimited). Protects Google Drive quotas.</summary>
    public double TpsLimit { get; set; } = 10;

    public double EffectiveMbps => MeasuredDownloadMbps ?? DeclaredDownloadMbps;
}

public sealed class StreamingSettings
{
    public bool PredictionEnabled { get; set; } = true;

    /// <summary>Record per-read telemetry from rclone debug logs. Costs some CPU while active.</summary>
    public bool ReadProfilingEnabled { get; set; } = true;

    public long BlockSizeBytes { get; set; } = ByteSize.MiB;

    public double MinConfidence { get; set; } = 0.4;

    /// <summary>Upper bound of bytes the engine may prefetch speculatively in one game session.</summary>
    public long SessionPrefetchBudgetBytes { get; set; } = 4 * ByteSize.GiB;

    public TimeSpan StartupWindow { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How far ahead (in game-progress time) learned startup data is released for prefetch during a launch.</summary>
    public TimeSpan StartupHorizon { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Predictors with accuracy below this after enough samples are disabled automatically.</summary>
    public double MinPredictorAccuracy { get; set; } = 0.25;

    public int PredictorEvaluationSamples { get; set; } = 64;
}

public sealed class MountDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string DisplayName { get; set; } = "Cloud Drive";

    /// <summary>rclone remote name without the trailing colon, e.g. "gdrive".</summary>
    public string RemoteName { get; set; } = "";

    /// <summary>Optional path inside the remote, e.g. "Games".</summary>
    public string RemotePath { get; set; } = "";

    /// <summary>Preferred drive letter, e.g. "X". Null = pick automatically.</summary>
    public string? DriveLetter { get; set; }

    public bool ReadOnly { get; set; }
    public bool AutoMount { get; set; } = true;

    /// <summary>
    /// "Keep Cloud SSD Permanent": the drive is owned by the background agent when the window is closed and is restored
    /// at Windows logon. Its drive letter is then fixed: if another drive holds it, the mount waits instead of moving.
    /// </summary>
    public bool Permanent { get; set; }

    /// <summary>Extra backend-specific options passed as connection string parameters (non-secret only).</summary>
    public Dictionary<string, string> ExtraFlags { get; set; } = new();

    [JsonIgnore] public string RemoteSpec => string.IsNullOrEmpty(RemotePath) ? RemoteName + ":" : $"{RemoteName}:{RemotePath.TrimStart('/')}";

    /// <summary>Shown in drive pickers.</summary>
    public override string ToString() => DriveLetter is { Length: > 0 } l ? $"{DisplayName} ({l}:)" : DisplayName;
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
    string Path { get; }
}

/// <summary>JSON settings persisted atomically (temp file + replace) so a crash cannot leave a truncated file.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    public JsonSettingsStore(string path) => Path = path;

    public string Path { get; }

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return new AppSettings();
            try
            {
                var json = File.ReadAllText(Path);
                return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
            catch (JsonException)
            {
                // Keep the damaged file for diagnostics and start from defaults.
                File.Copy(Path, Path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                try
                {
                    if (File.Exists(Path + ".bak"))
                    {
                        var backup = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path + ".bak"), Options);
                        if (backup != null) { File.Copy(Path + ".bak", Path, true); return backup; }
                    }
                }
                catch (JsonException) { }
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, Options);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(Path)) File.Replace(tmp, Path, Path + ".bak");
            else File.Move(tmp, Path);
        }
    }
}
