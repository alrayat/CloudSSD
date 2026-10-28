using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using CloudSSD.Cache;
using CloudSSD.Core;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CloudSSD.IntegrationTests;

public sealed class SharedMountFixture : IAsyncLifetime
{
    public Dataset Data { get; private set; } = null!;
    public MountHarness H { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Data = Dataset.Create(Path.Combine(TestEnv.WorkRoot, "dataset"));
        var cacheDir = Path.Combine(TestEnv.WorkRoot, "cache-shared");
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        H = await MountHarness.StartAsync("shared", Data.Root, cacheDirOverride: cacheDir);
    }

    public async Task DisposeAsync() { if (H != null) await H.DisposeAsync(); }
}

[CollectionDefinition("shared-mount")]
public sealed class SharedMountCollection : ICollectionFixture<SharedMountFixture>;

[Collection("shared-mount")]
public sealed class FilesystemTests(SharedMountFixture fx, ITestOutputHelper output)
{
    private MountHarness H => fx.H;
    private Dataset D => fx.Data;

    [Fact]
    public void Drive_is_visible_and_lists_directories()
    {
        Assert.True(Directory.Exists(H.Root));
        var drive = new DriveInfo(H.Root);
        Assert.True(drive.IsReady);
        output.WriteLine($"Drive {drive.Name} type={drive.DriveType} fs={drive.DriveFormat} label={drive.VolumeLabel}");
        var top = Directory.GetFileSystemEntries(H.Root).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in new[] { "small.txt", "big.bin", "Game Data", "many", "unicode", "app" })
            Assert.Contains(expected, top);
        Assert.Equal(120, Directory.GetFiles(H.P("many")).Length);
        Assert.True(File.Exists(H.P("unicode/ñandú über file.txt")));
        Assert.Equal(Dataset.PakSize, new FileInfo(H.P("Game Data/level 1 (final).pak")).Length);
    }

    [Fact]
    public void Whole_file_reads_match_source_hashes()
    {
        foreach (var rel in new[] { "small.txt", "Game Data/level 1 (final).pak", "unicode/ñandú über file.txt", "many/f007.dat" })
            Assert.Equal(D.Hashes[rel], Bytes.HashFile(H.P(rel)));
    }

    [Fact]
    public void Random_range_reads_match_source_bytes()
    {
        var rng = new Random(77);
        using var src = File.OpenRead(D.Source("big.bin"));
        for (int i = 0; i < 40; i++)
        {
            var len = rng.Next(1, 1 << 20);
            var off = (long)(rng.NextDouble() * (Dataset.BigSize - len));
            var expected = new byte[len];
            src.Seek(off, SeekOrigin.Begin);
            src.ReadExactly(expected);
            Assert.Equal(expected, Bytes.ReadRange(H.P("big.bin"), off, len));
        }
    }

    [Fact]
    public void Offsets_beyond_4GiB_boundary_work_on_sparse_large_file()
    {
        foreach (var off in Dataset.SparseMarkers)
        {
            var m = Dataset.MarkerBytes(off);
            Assert.Equal(m, Bytes.ReadRange(H.P("huge-sparse.bin"), off, m.Length));
        }
        Assert.Equal(new byte[4096], Bytes.ReadRange(H.P("huge-sparse.bin"), 2L * ByteSize.GiB + 1024 * 1024, 4096));
    }

    [Fact]
    public async Task Partial_reads_create_sparse_cache_and_warm_reads_use_no_network()
    {
        const string rel = "Game Data/level 2.pak";
        var offsets = new long[] { 0, 9 * ByteSize.MiB, 20 * ByteSize.MiB };
        H.Server.ResetCounters();
        foreach (var off in offsets) Bytes.ReadRange(H.P(rel), off, 64 * 1024);
        var coldBytes = H.Server.BytesSent;
        output.WriteLine($"Cold: 3 x 64 KiB reads fetched {coldBytes} bytes from the cloud ({H.Server.RangeRequests} range requests)");
        Assert.InRange(coldBytes, 3 * 64 * 1024, 8 * ByteSize.MiB);

        // rclone persists the range index once the last handle closes (after its 5 s handle-caching delay).
        CachedFile? cf = null;
        for (int i = 0; i < 40; i++)
        {
            cf = CacheInspector.Find(H.Session.CacheDir, rel);
            if (cf != null && offsets.All(o => cf.Covers(o, 64 * 1024)) && cf.PhysicalBytes > 0) break;
            await Task.Delay(500);
        }
        Assert.NotNull(cf);
        output.WriteLine($"Cache: logical={cf!.LogicalSize} cached={cf.CachedBytes} physical={cf.PhysicalBytes} ranges={cf.Ranges.Count}");
        Assert.Equal(Dataset.PakSize, cf.LogicalSize);
        Assert.True(cf.PhysicalBytes < cf.LogicalSize / 2, "sparse cache file should be mostly unallocated");
        foreach (var off in offsets) Assert.True(cf.Covers(off, 64 * 1024), $"range at {off} should be cached");

        H.Server.ResetCounters();
        var before = H.Reads.Count;
        foreach (var off in offsets) Bytes.ReadRange(H.P(rel), off, 64 * 1024);
        Assert.Equal(0, H.Server.BytesSent);
        var warm = await H.WaitForReadsAsync(e => e.Path == rel && e.CacheHit == true, 3);
        Assert.True(warm.Count >= 3, "telemetry should report cache hits for warm reads");
        var cold = H.Reads.Where(e => e.Path == rel && e.CacheHit == false).ToList();
        Assert.NotEmpty(cold);
        output.WriteLine($"Telemetry: cold avg wait {cold.Average(e => e.WaitMs ?? 0):0.0} ms, warm avg wait {warm.Average(e => e.WaitMs ?? 0):0.00} ms");
    }

    [Fact]
    public void Multiple_concurrent_handles_return_correct_data()
    {
        var files = Enumerable.Range(0, 24).Select(i => $"many/f{i:000}.dat").ToList();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, rel =>
            Assert.Equal(D.Hashes[rel], Bytes.HashFile(H.P(rel))));

        using var a = new FileStream(H.P("big.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var b = new FileStream(H.P("big.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
        a.Seek(10 * ByteSize.MiB, SeekOrigin.Begin);
        b.Seek(50 * ByteSize.MiB, SeekOrigin.Begin);
        var ba = new byte[4096];
        var bb = new byte[4096];
        a.ReadExactly(ba);
        b.ReadExactly(bb);
        Assert.Equal(Bytes.ReadRange(D.Source("big.bin"), 10 * ByteSize.MiB, 4096), ba);
        Assert.Equal(Bytes.ReadRange(D.Source("big.bin"), 50 * ByteSize.MiB, 4096), bb);
    }

    [Fact]
    public void Memory_mapped_read_matches_source()
    {
        const string rel = "Game Data/level 3.pak";
        using var mmf = MemoryMappedFile.CreateFromFile(H.P(rel), FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mmf.CreateViewAccessor(5 * ByteSize.MiB, 256 * 1024, MemoryMappedFileAccess.Read);
        var buf = new byte[256 * 1024];
        view.ReadArray(0, buf, 0, buf.Length);
        Assert.Equal(Bytes.ReadRange(D.Source(rel), 5 * ByteSize.MiB, buf.Length), buf);
    }

    [Fact]
    public void File_locking_behaviour_is_characterized()
    {
        // Records what WinFsp+rclone actually enforce; documented in COMPATIBILITY.md.
        using var first = new FileStream(H.P("small.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        bool secondOpenDenied;
        try
        {
            using var second = new FileStream(H.P("small.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            secondOpenDenied = false;
        }
        catch (IOException) { secondOpenDenied = true; }
        output.WriteLine($"Share-mode (FileShare.None) enforced on the mount: {secondOpenDenied}");

        bool rangeLockWorks;
        try
        {
            first.Lock(0, 4);
            first.Unlock(0, 4);
            rangeLockWorks = true;
        }
        catch (IOException) { rangeLockWorks = false; }
        output.WriteLine($"Byte-range LockFile supported: {rangeLockWorks}");
        File.WriteAllText(Path.Combine(TestEnv.WorkRoot, "locking-result.txt"), $"share-mode-enforced={secondOpenDenied}\nrange-lock={rangeLockWorks}\n");
    }

    [Fact]
    public void Executables_launch_from_the_mounted_drive()
    {
        var psi = new ProcessStartInfo(H.P("app/where.exe"), "cmd") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("cmd.exe", stdout, StringComparison.OrdinalIgnoreCase);

        using var h = Process.Start(new ProcessStartInfo(H.P("app/hostname.exe")) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var host = h.StandardOutput.ReadToEnd().Trim();
        h.WaitForExit(30_000);
        Assert.Equal(Environment.MachineName, host, ignoreCase: true);
    }

    [Fact]
    public void Directory_metadata_is_correct()
    {
        var fi = new FileInfo(H.P("big.bin"));
        Assert.Equal(Dataset.BigSize, fi.Length);
        var srcTime = File.GetLastWriteTimeUtc(D.Source("big.bin"));
        Assert.True(Math.Abs((fi.LastWriteTimeUtc - srcTime).TotalSeconds) < 2, $"mtime {fi.LastWriteTimeUtc:o} vs {srcTime:o}");
        Assert.True(new DirectoryInfo(H.P("Game Data")).Attributes.HasFlag(FileAttributes.Directory));
    }
}
