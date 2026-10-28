using System.Collections.Concurrent;
using System.Security.Cryptography;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using CloudSSD.Mount;
using CloudSSD.SimCloud;
using Microsoft.Extensions.Logging;

namespace CloudSSD.IntegrationTests;

/// <summary>Locations and shared resources for tests that drive real rclone + WinFsp mounts.</summary>
public static class TestEnv
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string WorkRoot { get; } = Environment.GetEnvironmentVariable("CLOUDSSD_TEST_ROOT") ?? Path.Combine(RepoRoot, ".work", "it");

    public static bool WinFspAvailable => WinFspDetector.Detect().Installed;

    private static RcloneInfo? _rclone;
    private static readonly SemaphoreSlim RcloneGate = new(1, 1);

    public static async Task<RcloneInfo> RcloneAsync()
    {
        await RcloneGate.WaitAsync();
        try
        {
            return _rclone ??= await RcloneLocator.LocateAsync(Environment.GetEnvironmentVariable("CLOUDSSD_TEST_RCLONE"), WorkRoot)
                               ?? throw new InvalidOperationException("rclone not found (expected tools\\rclone\\rclone.exe)");
        }
        finally { RcloneGate.Release(); }
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "CloudSSD.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    /// <summary>Deletes a per-test work folder (tests copy whole games; leftovers once filled the disk).</summary>
    public static void Cleanup(string dir)
    {
        for (int i = 0; i < 5 && Directory.Exists(dir); i++)
        {
            try { Directory.Delete(dir, true); }
            catch (IOException) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }

    public static ILoggerFactory Logging(string dir)
    {
        var sink = new MemoryLogSink();
        return LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(new AppLoggerProvider(dir, sink, LogLevel.Debug)));
    }
}

/// <summary>Deterministic dataset acting as the "cloud" content, with SHA-256 hashes of every file.</summary>
public sealed class Dataset
{
    public string Root { get; }
    public Dictionary<string, string> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public const long BigSize = 96 * ByteSize.MiB;
    public const long PakSize = 24 * ByteSize.MiB;
    public const long SparseSize = 3L * ByteSize.GiB;

    private Dataset(string root) => Root = root;

    public static Dataset Create(string root)
    {
        var ds = new Dataset(root);
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, ".dataset-v3");
        if (!File.Exists(marker))
        {
            Write(root, "small.txt", "hello cloudssd\n"u8.ToArray());
            WriteRandom(root, "big.bin", BigSize, 1);
            WriteRandom(root, Path.Combine("Game Data", "level 1 (final).pak"), PakSize, 2);
            WriteRandom(root, Path.Combine("Game Data", "level 2.pak"), PakSize, 3);
            WriteRandom(root, Path.Combine("Game Data", "level 3.pak"), PakSize, 4);
            for (int i = 0; i < 120; i++) WriteRandom(root, Path.Combine("many", $"f{i:000}.dat"), 4096, 100 + i);
            Write(root, Path.Combine("unicode", "ñandú über file.txt"), "unicode ok\n"u8.ToArray());
            Directory.CreateDirectory(Path.Combine(root, "app"));
            File.Copy(@"C:\Windows\System32\where.exe", Path.Combine(root, "app", "where.exe"), true);
            File.Copy(@"C:\Windows\System32\hostname.exe", Path.Combine(root, "app", "hostname.exe"), true);
            CreateSparse(Path.Combine(root, "huge-sparse.bin"), SparseSize);
            File.WriteAllText(marker, "ok");
        }
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel.StartsWith(".dataset") || rel == "huge-sparse.bin" || rel.StartsWith("mutable")) continue;
            using var fs = File.OpenRead(f);
            ds.Hashes[rel] = Convert.ToHexString(SHA256.HashData(fs));
        }
        return ds;
    }

    public string Source(string rel) => Path.Combine(Root, rel.Replace('/', '\\'));

    private static void Write(string root, string rel, byte[] data)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
    }

    private static void WriteRandom(string root, string rel, long size, int seed)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var rng = new Random(seed);
        var buf = new byte[ByteSize.MiB];
        using var fs = File.Create(p);
        for (long done = 0; done < size; done += buf.Length)
        {
            rng.NextBytes(buf);
            fs.Write(buf, 0, (int)Math.Min(buf.Length, size - done));
        }
    }

    /// <summary>NTFS sparse file with markers beyond 2 GiB, used to verify 64-bit offsets without using disk space.</summary>
    private static void CreateSparse(string path, long size)
    {
        using (var fs = File.Create(path)) { }
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("fsutil", $"sparse setflag \"{path}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        using var f = new FileStream(path, FileMode.Open, FileAccess.Write);
        f.SetLength(size);
        foreach (var off in SparseMarkers)
        {
            f.Seek(off, SeekOrigin.Begin);
            f.Write(MarkerBytes(off));
        }
    }

    public static readonly long[] SparseMarkers = [0, 2L * ByteSize.GiB + 12345, SparseSize - 64];

    public static byte[] MarkerBytes(long off) => System.Text.Encoding.ASCII.GetBytes($"MARK@{off:D12}");
}

/// <summary>One SimCloud server + one rclone mount, torn down on dispose.</summary>
public sealed class MountHarness : IAsyncDisposable
{
    public required SimCloudServer Server { get; init; }
    public required RcloneMountService Service { get; init; }
    public required MountSession Session { get; set; }
    public required AppPaths Paths { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public required MountRequest Request { get; init; }
    public ConcurrentQueue<AccessEvent> Reads { get; } = new();
    public ConcurrentQueue<ProviderEvent> ProviderEvents { get; } = new();
    public bool OwnsServer { get; init; } = true;

    public string Root => Session.Root;
    public string P(string rel) => Path.Combine(Root, rel.Replace('/', '\\'));

    public static async Task<MountHarness> StartAsync(string name, string contentRoot, Action<CacheSettings>? cache = null,
        double mbps = 0, TimeSpan? latency = null, Dictionary<string, string>? extraFlags = null, bool telemetry = true,
        SimCloudServer? server = null, string? cacheDirOverride = null, long budget = 4 * ByteSize.GiB, long minFree = 2 * ByteSize.GiB,
        MountDefinition? definition = null, string remoteType = "http", bool readOnly = false)
    {
        var rclone = await TestEnv.RcloneAsync();
        var home = Path.Combine(TestEnv.WorkRoot, "home-" + name);
        Directory.CreateDirectory(home);
        var paths = new AppPaths(home);
        var lf = TestEnv.Logging(paths.LogDirectory);
        var srv = server ?? new SimCloudServer(contentRoot);
        srv.BandwidthMbps = mbps;
        srv.Latency = latency ?? TimeSpan.Zero;

        var config = new RcloneConfigService(paths, lf.CreateLogger<RcloneConfigService>());
        var svc = new RcloneMountService(paths, config, lf.CreateLogger<RcloneMountService>());
        svc.SetRclone(rclone);
        var cs = new CacheSettings { PollInterval = TimeSpan.FromSeconds(60), MinFreeSpaceBytes = minFree, BudgetBytes = budget };
        cache?.Invoke(cs);
        var flags = new Dictionary<string, string> { ["http-url"] = srv.Url, ["low-level-retries"] = "3", ["contimeout"] = "5s" };
        if (extraFlags != null) foreach (var (k, v) in extraFlags) flags[k] = v;
        var def = definition ?? new MountDefinition { Id = name, DisplayName = name, RemoteName = ":http", ExtraFlags = flags };
        def.Id = name;
        def.ReadOnly = readOnly;
        if (definition != null) foreach (var (k, v) in flags) if (k != "http-url") def.ExtraFlags.TryAdd(k, v);
        var cacheDir = cacheDirOverride ?? Path.Combine(TestEnv.WorkRoot, "cache-" + name);
        var req = new MountRequest(def, cs, new NetworkSettings { TpsLimit = 0 }, cacheDir, cs.BudgetBytes, telemetry, remoteType);
        var h = new MountHarness
        {
            Server = srv, Service = svc, Paths = paths, LoggerFactory = lf, Request = req, Session = null!, OwnsServer = server is null
        };
        svc.ReadObserved += e => h.Reads.Enqueue(e);
        svc.ProviderEventObserved += (_, e) => h.ProviderEvents.Enqueue(e);
        h.Session = await svc.MountAsync(req);
        return h;
    }

    /// <summary>Waits until telemetry has caught up (log tailer runs every 150 ms).</summary>
    public async Task<List<AccessEvent>> WaitForReadsAsync(Func<AccessEvent, bool> predicate, int atLeast, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < until)
        {
            var list = Reads.Where(predicate).ToList();
            if (list.Count >= atLeast) return list;
            await Task.Delay(100);
        }
        return Reads.Where(predicate).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        try { await Service.UnmountAllAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        await Service.DisposeAsync();
        if (OwnsServer) await Server.DisposeAsync();
        LoggerFactory.Dispose();
    }
}

public static class Bytes
{
    public static byte[] ReadRange(string path, long offset, int length)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[length];
        int total = 0;
        while (total < length)
        {
            var n = fs.Read(buf, total, length - total);
            if (n == 0) break;
            total += n;
        }
        return total == length ? buf : buf[..total];
    }

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
