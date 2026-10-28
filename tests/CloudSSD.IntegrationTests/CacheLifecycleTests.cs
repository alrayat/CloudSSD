using System.Diagnostics;
using CloudSSD.Cache;
using CloudSSD.Core;
using CloudSSD.Mount;
using Xunit.Abstractions;

namespace CloudSSD.IntegrationTests;

public sealed class CacheLifecycleTests(ITestOutputHelper output)
{
    private static readonly Dataset D = Dataset.Create(Path.Combine(TestEnv.WorkRoot, "dataset"));

    private static string FreshCache(string name)
    {
        var dir = Path.Combine(TestEnv.WorkRoot, $"cache-{name}-{Guid.NewGuid().ToString("N")[..10]}");
        return dir;
    }

    [Fact]
    public async Task Cache_is_reused_after_unmount_and_remount_without_network_traffic()
    {
        var cache = FreshCache("reuse");
        const string rel = "Game Data/level 1 (final).pak";
        long[] offsets = [0, 3 * ByteSize.MiB, 17 * ByteSize.MiB];
        // rclone names the cache sub-folder after the remote's connection parameters (including the URL/port), so the
        // same server must be reused - exactly like a named remote ("gdrive:") is stable across restarts.
        await using var server = new CloudSSD.SimCloud.SimCloudServer(D.Root);
        await using (var h = await MountHarness.StartAsync("reuse", D.Root, cacheDirOverride: cache, server: server))
        {
            foreach (var o in offsets) Bytes.ReadRange(h.P(rel), o, 128 * 1024);
            await h.Service.UnmountAsync(h.Session.Id, TimeSpan.FromSeconds(10));
        }
        var cf = CacheInspector.Find(cache, rel);
        Assert.NotNull(cf);
        output.WriteLine($"Persisted index after unmount: {cf!.Ranges.Count} ranges, {cf.CachedBytes} bytes");
        Assert.All(offsets, o => Assert.True(cf.Covers(o, 128 * 1024)));

        await using var h2 = await MountHarness.StartAsync("reuse", D.Root, cacheDirOverride: cache, server: server);
        h2.Server.ResetCounters();
        foreach (var o in offsets)
            Assert.Equal(Bytes.ReadRange(D.Source(rel), o, 128 * 1024), Bytes.ReadRange(h2.P(rel), o, 128 * 1024));
        output.WriteLine($"After restart: {h2.Server.BytesSent} bytes fetched from cloud for previously cached ranges");
        foreach (var r in h2.Server.ServedRanges) output.WriteLine($"  served {r.Path} @{r.Start} len {r.Length}");
        Assert.Equal(0, h2.Server.BytesSent);
    }

    [Fact]
    public async Task Crashed_rclone_is_restarted_automatically_and_data_stays_correct()
    {
        await using var h = await MountHarness.StartAsync("crash", D.Root, cacheDirOverride: FreshCache("crash"));
        Bytes.ReadRange(h.P("big.bin"), 0, 4096);
        var pid = h.Session.ProcessId!.Value;
        Process.GetProcessById(pid).Kill();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(40))
        {
            if (h.Session.State == MountState.Mounted && h.Session.ProcessId is { } np && np != pid) break;
            await Task.Delay(250);
        }
        output.WriteLine($"Recovered in {sw.Elapsed.TotalSeconds:0.0} s; state={h.Session.State} restarts={h.Session.RestartCount}");
        Assert.Equal(MountState.Mounted, h.Session.State);
        Assert.Equal(1, h.Session.RestartCount);
        Assert.Equal(Bytes.ReadRange(D.Source("big.bin"), 40 * ByteSize.MiB, 8192), Bytes.ReadRange(h.P("big.bin"), 40 * ByteSize.MiB, 8192));
    }

    [Fact]
    public async Task Least_recently_used_files_are_evicted_when_max_size_is_exceeded()
    {
        var cache = FreshCache("evict");
        await using var h = await MountHarness.StartAsync("evict", D.Root, cacheDirOverride: cache,
            cache: c => c.PollInterval = TimeSpan.FromSeconds(3), budget: 40 * ByteSize.MiB);
        foreach (var f in new[] { "Game Data/level 1 (final).pak", "Game Data/level 2.pak", "Game Data/level 3.pak" })
        {
            Bytes.HashFile(h.P(f));
            await Task.Delay(7000); // past rclone's 5 s handle-caching window so the file is really closed
        }
        CacheUsage usage = CacheInspector.Measure(cache);
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            usage = CacheInspector.Measure(cache);
            if (usage.PhysicalBytes <= 40 * ByteSize.MiB && usage.Files > 0) break;
        }
        output.WriteLine($"After eviction: files={usage.Files} physical={ByteSize.Format(usage.PhysicalBytes)} cached={ByteSize.Format(usage.CachedBytes)}");
        Assert.True(usage.PhysicalBytes <= 40 * ByteSize.MiB, "rclone should evict down to --vfs-cache-max-size");
        var newest = CacheInspector.Find(cache, "Game Data/level 3.pak");
        var oldest = CacheInspector.Find(cache, "Game Data/level 1 (final).pak");
        Assert.True(newest is { CachedBytes: > 0 }, "most recently used file should survive");
        Assert.True(oldest is null || oldest.CachedBytes == 0, "least recently used file should be evicted");
        // Original cloud data is never touched by eviction.
        Assert.Equal(D.Hashes["Game Data/level 1 (final).pak"], Bytes.HashFile(D.Source("Game Data/level 1 (final).pak")));
    }

    [Fact]
    public async Task Low_disk_space_triggers_eviction_through_min_free_space()
    {
        var cache = FreshCache("lowdisk");
        Directory.CreateDirectory(cache);
        var (free, _) = DiskInfo.FreeSpace(cache);
        var decision = DiskSafetyGuard.Decide(20 * ByteSize.GiB, free + ByteSize.TiB, free, 0);
        Assert.Equal(DiskPressure.Critical, decision.Pressure);
        output.WriteLine(string.Join(Environment.NewLine, decision.Warnings));

        await using var h = await MountHarness.StartAsync("lowdisk", D.Root, cacheDirOverride: cache,
            cache: c => c.PollInterval = TimeSpan.FromSeconds(3), minFree: free + ByteSize.TiB);
        Assert.Equal(D.Hashes["Game Data/level 2.pak"], Bytes.HashFile(h.P("Game Data/level 2.pak")));
        CacheUsage usage = CacheInspector.Measure(cache);
        for (int i = 0; i < 40 && usage.CachedBytes > 0; i++)
        {
            await Task.Delay(500);
            usage = CacheInspector.Measure(cache);
        }
        output.WriteLine($"Cache under simulated disk pressure: {ByteSize.Format(usage.CachedBytes)} indexed, {ByteSize.Format(usage.PhysicalBytes)} physical");
        // rclone drops the range index immediately; a little sparse allocation may linger until the data file is removed.
        Assert.Equal(0, usage.CachedBytes);
        Assert.True(usage.PhysicalBytes < Dataset.PakSize / 4, "closed files must be evicted when free space is below the reserve");
    }

    [Fact]
    public async Task Remote_file_change_invalidates_stale_cache()
    {
        var mutableDir = Path.Combine(D.Root, "mutable");
        Directory.CreateDirectory(mutableDir);
        var file = Path.Combine(mutableDir, "save.bin");
        var v1 = new byte[2 * ByteSize.MiB];
        new Random(1).NextBytes(v1);
        File.WriteAllBytes(file, v1);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));

        await using var h = await MountHarness.StartAsync("change", D.Root, cacheDirOverride: FreshCache("change"));
        Assert.Equal(v1, File.ReadAllBytes(h.P("mutable/save.bin")));

        var v2 = new byte[2 * ByteSize.MiB];
        new Random(2).NextBytes(v2);
        File.WriteAllBytes(file, v2);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        await Task.Delay(6500); // let rclone close the cached handle
        await h.Service.ForgetAsync(h.Session.Id, "mutable/save.bin");
        var read = File.ReadAllBytes(h.P("mutable/save.bin"));
        Assert.Equal(v2, read);
        output.WriteLine("Changed remote file was re-fetched; stale cached bytes were not served.");
    }

    [Fact]
    public async Task Disconnection_returns_errors_for_uncached_data_and_cached_data_stays_readable()
    {
        await using var h = await MountHarness.StartAsync("offline", D.Root, cacheDirOverride: FreshCache("offline"),
            extraFlags: new() { ["low-level-retries"] = "2", ["retries"] = "1", ["contimeout"] = "2s", ["timeout"] = "5s" });
        const string rel = "big.bin";
        var warm = Bytes.ReadRange(h.P(rel), 0, 256 * 1024);
        await Task.Delay(500);
        h.Server.Offline = true;

        var sw = Stopwatch.StartNew();
        Exception? error = null;
        byte[]? data = null;
        try { data = await Task.Run(() => Bytes.ReadRange(h.P(rel), 70 * ByteSize.MiB, 64 * 1024)).WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (Exception e) { error = e; }
        output.WriteLine($"Uncached read while offline: {(error is null ? "returned " + data?.Length + " bytes" : error.GetType().Name + ": " + error.Message)} after {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.True(error is IOException or UnauthorizedAccessException, "an uncached read must fail, not return fabricated data");

        Assert.Equal(warm, Bytes.ReadRange(h.P(rel), 0, 256 * 1024));
        output.WriteLine("Cached range remained readable while offline.");

        h.Server.Offline = false;
        // rclone keeps the errored handle for its 5 s handle-caching window, so the first read after reconnecting may
        // still fail. It must then recover with correct bytes; it must never return wrong data.
        var expected = Bytes.ReadRange(D.Source(rel), 70 * ByteSize.MiB, 64 * 1024);
        var recovery = Stopwatch.StartNew();
        byte[]? after = null;
        while (recovery.Elapsed < TimeSpan.FromSeconds(30))
        {
            try { after = Bytes.ReadRange(h.P(rel), 70 * ByteSize.MiB, 64 * 1024); break; }
            catch (IOException) { await Task.Delay(500); }
        }
        output.WriteLine($"Recovered after reconnect in {recovery.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(expected, after);
        Assert.Contains(h.ProviderEvents, e => e.Kind is ProviderEventKind.NetworkError or ProviderEventKind.Error);
    }

    [Theory]
    [InlineData(0.3, 0.0)]
    [InlineData(0.0, 0.3)]
    public async Task Transient_http_errors_and_dropped_connections_are_retried_transparently(double failureRate, double dropRate)
    {
        await using var h = await MountHarness.StartAsync($"flaky{(int)(failureRate * 10)}{(int)(dropRate * 10)}", D.Root, cacheDirOverride: FreshCache("flaky"),
            extraFlags: new() { ["low-level-retries"] = "20" });
        h.Server.FailureRate = failureRate;
        h.Server.DropRate = dropRate;
        var rng = new Random(9);
        // Faults are random per request; keep reading (each read verified) until at least one was really injected.
        for (int i = 0; i < 60 && (i < 12 || h.Server.FailuresInjected == 0); i++)
        {
            var off = (long)(rng.NextDouble() * (Dataset.BigSize - ByteSize.MiB));
            Assert.Equal(Bytes.ReadRange(D.Source("big.bin"), off, 256 * 1024), Bytes.ReadRange(h.P("big.bin"), off, 256 * 1024));
        }
        output.WriteLine($"Injected failures={h.Server.FailuresInjected}, requests={h.Server.Requests}, retry/error events parsed={h.ProviderEvents.Count}");
        Assert.True(h.Server.FailuresInjected > 0);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(70)]
    [InlineData(100)]
    [InlineData(500)]
    public async Task Cold_sequential_throughput_tracks_the_simulated_link_speed(double mbps)
    {
        await using var h = await MountHarness.StartAsync($"bw{mbps}", D.Root, cacheDirOverride: FreshCache($"bw{mbps}"), mbps: mbps, telemetry: false);
        var bytes = (long)Math.Min(Bandwidth.MbpsToBytesPerSecond(mbps) * 3, 400 * ByteSize.MiB);
        var offset = 100 * ByteSize.MiB;
        var sw = Stopwatch.StartNew();
        using (var fs = new FileStream(h.P("huge-sparse.bin"), FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[ByteSize.MiB];
            long done = 0;
            while (done < bytes) done += fs.Read(buf, 0, (int)Math.Min(buf.Length, bytes - done));
        }
        var achieved = Bandwidth.BytesPerSecondToMbps(bytes / sw.Elapsed.TotalSeconds);
        output.WriteLine($"Link {mbps} Mbps: read {ByteSize.Format(bytes)} in {sw.Elapsed.TotalSeconds:0.00} s = {achieved:0.0} Mbps ({achieved / mbps:P0} of link)");
        File.AppendAllText(Path.Combine(TestEnv.WorkRoot, "throughput.csv"), $"{DateTime.Now:o},{mbps},{bytes},{sw.Elapsed.TotalSeconds:0.000},{achieved:0.0}\n");
        Assert.InRange(achieved, mbps * 0.5, mbps * 1.2);
    }

    [Fact]
    public async Task Read_only_mount_rejects_writes()
    {
        await using var h = await MountHarness.StartAsync("ro", D.Root, cacheDirOverride: FreshCache("ro"), readOnly: true);
        Assert.ThrowsAny<Exception>(() => File.WriteAllText(h.P("new-file.txt"), "x"));
        // Windows reports DeleteFile success once delete-on-close is set; the unlink then fails with EROFS at cleanup
        // and the file survives. Verify the outcome rather than the API return value.
        try { File.Delete(h.P("small.txt")); } catch (Exception) { }
        Assert.True(File.Exists(h.P("small.txt")));
        Assert.True(File.Exists(D.Source("small.txt")));
        Assert.False(File.Exists(D.Source("new-file.txt")));
    }
}
