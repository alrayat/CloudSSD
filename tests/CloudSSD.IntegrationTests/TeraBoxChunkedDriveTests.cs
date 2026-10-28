using System.Security.Cryptography;
using CloudSSD.Core;
using CloudSSD.Mount;
using CloudSSD.SimCloud;
using CloudSSD.TeraBox;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CloudSSD.IntegrationTests;

/// <summary>
/// Large files on TeraBox: rclone chunker over the TeraBox bridge (website transport, mock). The mock enforces
/// a small per-file plan limit; a file several times larger is written through the drive, must be stored as parts that
/// each respect the limit, must appear in Windows as one file of the original size, and must read back exactly on a cold
/// cache at random offsets and across part boundaries.
/// </summary>
public sealed class TeraBoxChunkedDriveTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const long PlanLimit = 20L * 1024 * 1024;
    private const string ChunkSize = "16Mi";
    private readonly string _root = Path.Combine(TestEnv.WorkRoot, "tbchunk-" + Guid.NewGuid().ToString("N")[..6]);
    private TeraBoxMockServer _mock = null!;
    private HttpClient _http = null!;
    private TeraBoxWebDavBridge _bridge = null!;
    private ILoggerFactory _lf = null!;
    private string _name = "";

    private string CloudDir(string rel) => _mock.DiskPath(TeraBoxWebApi.DefaultRoot + "/" + rel);

    public async Task InitializeAsync()
    {
        _mock = new TeraBoxMockServer(Path.Combine(_root, "cloud"), Path.Combine(_root, "state")) { MaxFileSize = PlanLimit, QuotaTotal = 1024L * ByteSize.GiB };
        var store = new TeraBoxWebSessionStore(Path.Combine(_root, "web.key"));
        store.Save(new TeraBoxWebSession(_mock.BaseUrl, _mock.WebCookieHeader, "Mozilla/5.0 (test)", null, DateTimeOffset.UtcNow));
        _lf = TestEnv.Logging(Path.Combine(_root, "logs"));
        _http = TeraBoxWebApi.CreateHttpClient();
        var secret = Guid.NewGuid().ToString("N");
        _bridge = new TeraBoxWebDavBridge(new TeraBoxWebApi(store, _http, _lf.CreateLogger("TeraBoxWeb")), "cloudssd-cli", secret, Path.Combine(_root, "staging"),
            _lf.CreateLogger("Bridge")) { ListingTtl = TimeSpan.FromSeconds(1) };
        var rclone = await TestEnv.RcloneAsync();
        var obscured = (await new RcloneRunner(rclone.Path, new Dictionary<string, string?>()).RunAsync(["obscure", secret], TimeSpan.FromSeconds(20))).StdOut.Trim();
        // Same layout the app creates: a webdav remote for the bridge and a chunker remote on top of it.
        _name = "tbchunk-" + Guid.NewGuid().ToString("N")[..4];
        var home = Path.Combine(TestEnv.WorkRoot, "home-" + _name);
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Combine(home, "rclone.conf"), $"""
            [tbw]
            type = webdav
            url = {_bridge.Url}
            vendor = other
            user = cloudssd-cli
            pass = {obscured}

            [tbc]
            type = chunker
            remote = tbw:
            chunk_size = {ChunkSize}
            hash_type = md5

            """);
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        await _mock.DisposeAsync();
        _http.Dispose();
        _lf.Dispose();
        TestEnv.Cleanup(_root);
        TestEnv.Cleanup(Path.Combine(TestEnv.WorkRoot, "home-" + _name));
    }

    private Task<MountHarness> MountAsync(string cache, TimeSpan? writeBack = null) => MountHarness.StartAsync(_name, Path.Combine(_root, "unused-sim"),
        definition: new MountDefinition { DisplayName = "TeraBox chunked", RemoteName = "tbc", DriveLetter = null },
        remoteType: "chunker", cache: c => { c.WriteBack = writeBack ?? TimeSpan.FromSeconds(1); c.BudgetBytes = ByteSize.GiB + 1234567; }, cacheDirOverride: cache);

    [Fact]
    public async Task Steam_cleanup_preserves_unique_dirty_ranges_before_archiving_the_cloud_version()
    {
        if (!TestEnv.WinFspAvailable) return;
        const string relative = "SteamLibrary/steamapps/downloading/3240220/dirty-delete.rpf";
        var original = new byte[512 * 1024]; new Random(441).NextBytes(original);
        var patch = new byte[64 * 1024]; new Random(442).NextBytes(patch);
        Directory.CreateDirectory(Path.GetDirectoryName(CloudDir(relative))!);
        await File.WriteAllBytesAsync(CloudDir(relative), original);
        var protection = Path.Combine(_root, "protect.txt");
        await File.WriteAllTextAsync(protection, "SteamLibrary/steamapps/downloading/3240220");
        _bridge.DeleteProtectionFile = protection;
        _bridge.ProtectedDeleteRecoveryRoot = ".cloudssd-recovery";
        _bridge.ProtectedDeleteJournalDirectory = Path.Combine(_root, "archive-journal");
        var cache = Path.Combine(_root, "dirty-delete-cache");
        await using var mount = await MountAsync(cache, TimeSpan.FromHours(1));
        using (var writer = File.OpenHandle(mount.P(relative), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            RandomAccess.Write(writer, patch, 64 * 1024);
            RandomAccess.FlushToDisk(writer);
        }
        // Writeback is delayed: the only copy of PATCH is the local dirty cache.
        Assert.Equal(original, await File.ReadAllBytesAsync(CloudDir(relative)));
        File.Delete(mount.P(relative));
        Assert.False(File.Exists(mount.P(relative)));
        var metadataPath = Assert.Single(Directory.GetFiles(cache, "recovery.json", SearchOption.AllDirectories));
        using var metadata = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
        var record = metadata.RootElement;
        Assert.Equal(relative, record.GetProperty("Path").GetString());
        Assert.True(record.GetProperty("Info").GetProperty("Dirty").GetBoolean());
        var payload = Path.Combine(Path.GetDirectoryName(metadataPath)!, "data.sparse");
        using (var saved = File.OpenHandle(payload))
        {
            var recovered = new byte[patch.Length];
            Assert.Equal(recovered.Length, RandomAccess.Read(saved, recovered, 64 * 1024));
            Assert.Equal(patch, recovered);
        }
        using var payloadStream = File.OpenRead(payload);
        foreach (var range in record.GetProperty("ValidRanges").EnumerateArray())
        {
            var position = range.GetProperty("Pos").GetInt64();
            var length = checked((int)range.GetProperty("Size").GetInt64());
            var data = new byte[length]; payloadStream.Position = position;
            await payloadStream.ReadExactlyAsync(data);
            Assert.Equal(range.GetProperty("SHA256").GetString(), Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
        }
        var cloudCopy = Assert.Single(Directory.GetFiles(CloudDir(".cloudssd-recovery"), "dirty-delete.rpf", SearchOption.AllDirectories));
        Assert.Equal(original, await File.ReadAllBytesAsync(cloudCopy));
        Assert.Single(Directory.GetFiles(_bridge.ProtectedDeleteJournalDirectory, "*.json"));
    }

    [Fact]
    public async Task Planned_unmount_preserves_open_download_handles_and_succeeds_after_they_close()
    {
        if (!TestEnv.WinFspAvailable) return;
        await using var mount = await MountAsync(Path.Combine(_root, "open-guard-cache"));
        Directory.CreateDirectory(mount.P("SteamLibrary/steamapps/downloading/123"));
        var name = mount.P("SteamLibrary/steamapps/downloading/123/game.bin");
        var bytes = new byte[65536]; new Random(913).NextBytes(bytes);
        var writer = File.OpenHandle(name, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            RandomAccess.Write(writer, bytes, 0);
            RandomAccess.FlushToDisk(writer);
            var stats = await mount.Service.StatsAsync(mount.Session.Id);
            Assert.True(stats?.OpenFiles > 0, "Native driver did not report open files.");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => mount.Service.UnmountAsync(mount.Session.Id, TimeSpan.Zero));
            Assert.Contains("Pause Steam", error.Message);
            var read = new byte[bytes.Length];
            Assert.Equal(read.Length, RandomAccess.Read(writer, read, 0));
            Assert.Equal(bytes, read);
            Assert.Equal(MountState.Mounted, mount.Session.State);
        }
        finally { writer.Dispose(); }
        var stopping = mount.Service.UnmountAsync(mount.Session.Id, TimeSpan.FromSeconds(10));
        var stopDeadline = DateTime.UtcNow.AddSeconds(5);
        while (mount.Session.State != MountState.Stopping && !stopping.IsCompleted && DateTime.UtcNow < stopDeadline) await Task.Delay(20);
        if (!stopping.IsCompleted)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => mount.Service.MountAsync(mount.Request));
            Assert.Contains("still stopping", error.Message);
        }
        await stopping;
        Assert.Equal(MountState.Stopped, mount.Session.State);
    }

    [Fact]
    public async Task Slow_cold_recovery_leaves_capacity_for_new_download_uploads()
    {
        if (!TestEnv.WinFspAvailable) return;
        var originals = Enumerable.Range(0, 4).Select(i =>
        {
            var bytes = new byte[256 * 1024]; new Random(100 + i).NextBytes(bytes); return bytes;
        }).ToArray();
        // A whole, aligned 64 KiB block: a smaller write makes the Windows cache manager read the surrounding block
        // first, which at the throttled rate below takes minutes per file (measured 7.5 min) and is not what is tested.
        const int patchAt = 64 * 1024;
        var patch = new byte[64 * 1024]; new Random(99).NextBytes(patch);
        Directory.CreateDirectory(CloudDir("recovery"));
        for (var i = 0; i < originals.Length; i++)
            await File.WriteAllBytesAsync(CloudDir($"recovery/cold-{i}.pak"), originals[i]);
        await using var mount = await MountAsync(Path.Combine(_root, "starvation-cache"));
        _mock.DownloadMbps = 0.01; // cold recovery is slow; uploads remain available
        try
        {
            for (var i = 0; i < originals.Length; i++)
            {
                // A raw handle: no FileStream buffer that could read the file before writing.
                using var writer = File.OpenHandle(mount.P($"recovery/cold-{i}.pak"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                RandomAccess.Write(writer, patch, patchAt);
                RandomAccess.FlushToDisk(writer);
            }
            await Task.Delay(3000); // every cold item is eligible for writeback
            var ready = new byte[4096]; new Random(111).NextBytes(ready);
            await File.WriteAllBytesAsync(mount.P("recovery/ready.pak"), ready);
            var until = DateTime.UtcNow.AddSeconds(15);
            var uploaded = false;
            while (DateTime.UtcNow < until)
            {
                // The simulated cloud replaces files in place; a read during the swap is refused and retried.
                try
                {
                    if (File.Exists(CloudDir("recovery/ready.pak")) &&
                        (await File.ReadAllBytesAsync(CloudDir("recovery/ready.pak"))).SequenceEqual(ready))
                    { uploaded = true; break; }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                await Task.Delay(100);
            }
            Assert.True(uploaded, "Cold recovery occupied every worker and blocked a completely downloaded file.");
        }
        finally { _mock.DownloadMbps = 0; }
        for (var i = 0; i < originals.Length; i++)
        {
            patch.CopyTo(originals[i], patchAt);
            var until = DateTime.UtcNow.AddSeconds(45);
            var exact = false;
            while (DateTime.UtcNow < until)
            {
                try { exact = (await File.ReadAllBytesAsync(CloudDir($"recovery/cold-{i}.pak"))).SequenceEqual(originals[i]); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                if (exact) break;
                await Task.Delay(100);
            }
            Assert.True(exact, $"Cold file {i} did not preserve its original and patched bytes.");
        }
    }

    [Fact]
    public async Task Closing_a_partial_write_does_not_wait_for_cloud_and_cold_readback_is_exact()
    {
        if (!TestEnv.WinFspAvailable) return;
        const string relative = "SteamLibrary/steamapps/downloading/3240220/partial.rpf";
        var bytes = new byte[8 * 1024 * 1024 + 19]; new Random(81).NextBytes(bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(CloudDir(relative))!);
        await File.WriteAllBytesAsync(CloudDir(relative), bytes);
        var patch = new byte[4096]; new Random(82).NextBytes(patch);
        await using (var m = await MountAsync(Path.Combine(_root, "deferred-write-cache")))
        {
            var writer = new FileStream(m.P(relative), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            writer.Position = ByteSize.MiB; writer.Write(patch); writer.Flush(true);
            _mock.Latency = TimeSpan.FromSeconds(8);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var close = Task.Run(writer.Dispose);
            try { await close.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { _mock.Latency = TimeSpan.Zero; }
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), "File close waited for cloud hydration.");
            patch.CopyTo(bytes, ByteSize.MiB);
            var until = DateTime.UtcNow.AddSeconds(45);
            bool stored = false;
            while (DateTime.UtcNow < until)
            {
                try
                {
                    if ((await File.ReadAllBytesAsync(CloudDir(relative))).SequenceEqual(bytes)) { stored = true; break; }
                }
                catch (IOException) { } // finalizing a chunk can briefly replace the destination
                await Task.Delay(200);
            }
            Assert.True(stored, "The background write did not preserve every original and patched byte.");
        }
        await using (var cold = await MountAsync(Path.Combine(_root, "deferred-cold-cache")))
            Assert.Equal(bytes, await File.ReadAllBytesAsync(cold.P(relative)));
    }

    [Fact]
    public async Task Paused_sparse_Steam_write_restores_exact_bytes_after_a_worker_crash()
    {
        if (!TestEnv.WinFspAvailable) return;
        const string relative = "SteamLibrary/steamapps/downloading/3240220/archive.rpf";
        var original = new byte[8 * 1024 * 1024]; new Random(61).NextBytes(original);
        Directory.CreateDirectory(Path.GetDirectoryName(CloudDir(relative))!);
        await File.WriteAllBytesAsync(CloudDir(relative), original);
        var cache = Path.Combine(_root, "paused-cache");
        var patch = new byte[4096]; new Random(62).NextBytes(patch);
        await using (var first = await MountAsync(cache))
        {
            first.Service.MaxRestarts = 0;
            var writer = new FileStream(first.P(relative), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            try
            {
                writer.Position = 1024 * 1024; writer.Write(patch); writer.Flush(true);
                using var worker = System.Diagnostics.Process.GetProcessById(first.Session.ProcessId!.Value);
                worker.Kill(); await worker.WaitForExitAsync();
            }
            finally { try { writer.Dispose(); } catch (IOException) { } }
        }
        var saved = new PausedSteamWrites(cache);
        var pending = Assert.Single(saved.PauseIncompleteDownloads());
        Assert.True(pending.CachedBytes < pending.Size);
        // Repair exposes the existing cloud drive, while keeping the interrupted change separate and resumable.
        await using (var repaired = await MountAsync(cache))
        {
            Assert.Equal(1, repaired.Session.PausedSteamWrites);
            Assert.Equal(original, await File.ReadAllBytesAsync(repaired.P(relative)));
        }
        saved.Restore(); Assert.Empty(saved.All());
        var expected = (byte[])original.Clone(); patch.CopyTo(expected, 1024 * 1024);
        await using (var resumed = await MountAsync(cache))
            Assert.Equal(expected, await File.ReadAllBytesAsync(resumed.P(relative)));
    }

    [Fact]
    public async Task File_larger_than_the_plan_limit_is_stored_as_parts_and_reads_back_exactly()
    {
        if (!TestEnv.WinFspAvailable) return;
        var big = new byte[50 * 1024 * 1024 + 4321];
        new Random(77).NextBytes(big);
        var small = new byte[3 * 1024 * 1024];
        new Random(78).NextBytes(small);

        await using (var m = await MountAsync(Path.Combine(_root, "cache-1")))
        {
            var actual = await m.Session.Rc!.CallAsync("vfs/stats");
            Assert.Equal(ByteSize.GiB + 1234567, m.Request.EffectiveCacheMaxBytes);
            // rclone copies SizeSuffix options through a three-decimal binary-unit string. Allow that
            // upstream rounding, but reject the 1024-fold expansion caused by a missing B suffix.
            Assert.InRange(actual["opt"]!["CacheMaxSize"]!.GetValue<long>(), m.Request.EffectiveCacheMaxBytes - ByteSize.MiB, m.Request.EffectiveCacheMaxBytes + ByteSize.MiB);
            Directory.CreateDirectory(m.P("Game/Paks"));
            await File.WriteAllBytesAsync(m.P("Game/Paks/pakchunk0.pak"), big);
            await File.WriteAllBytesAsync(m.P("Game/Paks/small.pak"), small);
            // Wait until rclone has uploaded everything (write-back), including the chunker metadata object.
            var until = DateTime.UtcNow.AddSeconds(180);
            while (DateTime.UtcNow < until)
            {
                var v = await m.Service.StatsAsync(m.Session.Id);
                var dir = CloudDir("Game/Paks");
                // Final part names end in the 3-digit number; parts still being written carry a temporary suffix.
                var parts = Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "pakchunk0.pak.rclone_chunk.*").Where(f => System.Text.RegularExpressions.Regex.IsMatch(f, @"\.rclone_chunk\.\d{3}$")).ToArray()
                    : [];
                if (v is { UploadsInProgress: 0, UploadsQueued: 0 } && parts.Length == 4 && File.Exists(Path.Combine(dir, "pakchunk0.pak")) && File.Exists(Path.Combine(dir, "small.pak"))) break;
                await Task.Delay(500);
            }
        }

        var cloud = Directory.GetFiles(CloudDir("Game/Paks"));
        foreach (var f in cloud) output.WriteLine($"stored: {Path.GetFileName(f)} {new FileInfo(f).Length:N0} bytes");
        Assert.All(cloud, f => Assert.True(new FileInfo(f).Length <= PlanLimit, $"{Path.GetFileName(f)} exceeds the plan limit"));
        Assert.Equal(4, cloud.Count(f => Path.GetFileName(f).StartsWith("pakchunk0.pak.rclone_chunk.")));
        Assert.True(new FileInfo(Path.Combine(CloudDir("Game/Paks"), "pakchunk0.pak")).Length < 4096, "metadata object, not data");
        Assert.Equal(small, File.ReadAllBytes(Path.Combine(CloudDir("Game/Paks"), "small.pak"))); // below the part size: stored unchanged

        // Fresh mount with an empty cache: everything comes from the cloud parts.
        await using (var m = await MountAsync(Path.Combine(_root, "cache-2")))
        {
            var path = m.P("Game/Paks/pakchunk0.pak");
            Assert.Equal(big.Length, new FileInfo(path).Length);
            Assert.Equal(new[] { "pakchunk0.pak", "small.pak" }, Directory.GetFiles(m.P("Game/Paks")).Select(Path.GetFileName).Order().ToArray());
            var part = 16L * 1024 * 1024;
            var offsets = new List<long> { 0, part - 10, part, 2 * part - 1, 3 * part - 5000, big.Length - 100 };
            var rnd = new Random(5);
            for (int i = 0; i < 20; i++) offsets.Add((long)(rnd.NextDouble() * (big.Length - 70_000)));
            foreach (var off in offsets)
            {
                var len = (int)Math.Min(65_536, big.Length - off);
                Assert.Equal(big.AsSpan((int)off, len).ToArray(), Bytes.ReadRange(path, off, len));
            }
            Assert.Equal(Convert.ToHexString(SHA256.HashData(big)), Bytes.HashFile(path));
            Assert.Equal(small, File.ReadAllBytes(m.P("Game/Paks/small.pak")));
        }

        // Another empty VFS cache: use the original local bytes only after each cloud chunk's checksum matches.
        var originals = Path.Combine(_root, "originals");
        Directory.CreateDirectory(originals);
        await File.WriteAllBytesAsync(Path.Combine(originals, "pakchunk0.pak"), big);
        await File.WriteAllBytesAsync(Path.Combine(originals, "small.pak"), small);
        var sources = new VerifiedLocalSources([new("Game/Paks", originals, 16 * ByteSize.MiB)]);
        _bridge.LocalSources = sources;
        await using (var m = await MountAsync(Path.Combine(_root, "cache-local")))
        {
            var path = m.P("Game/Paks/pakchunk0.pak");
            const int off = 16 * 1024 * 1024 - 10;
            Assert.Equal(big.AsSpan(off, 65_536).ToArray(), Bytes.ReadRange(path, off, 65_536));
            Assert.Equal(small, File.ReadAllBytes(m.P("Game/Paks/small.pak")));
            Assert.True(sources.Hits >= 3);
            Assert.True(sources.Bytes >= small.Length + 65_536);
        }
    }

    /// <summary>
    /// Steam's pattern: reserve the file at full size (zeros), close it, then write the real bytes in later sessions. The
    /// reservation must cost almost nothing in TeraBox (markers), and the finished file must read back exactly from a cold
    /// cache, with the untouched parts still zeros.
    /// </summary>
    [Fact]
    public async Task Space_reservation_is_stored_as_markers_and_filled_parts_replace_them()
    {
        if (!TestEnv.WinFspAvailable) return;
        const int part = 16 * 1024 * 1024;
        var fill = new byte[2 * 1024 * 1024];
        new Random(9).NextBytes(fill);
        const long fillAt = part + 3 * 1024 * 1024; // inside the second part
        var dir = CloudDir("steamapps/downloading");
        string[] Stored() => Directory.Exists(dir) ? Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order().ToArray() : [];
        bool Final(string n) => System.Text.RegularExpressions.Regex.IsMatch(n, @"\.rclone_chunk\.\d{3}(\.cloudssdzero-\d+)?$");

        await using (var m = await MountAsync(Path.Combine(_root, "cache-z")))
        {
            Directory.CreateDirectory(m.P("steamapps/downloading"));
            var path = m.P("steamapps/downloading/x64a.rpf");
            await using (var fs = new FileStream(path, FileMode.CreateNew)) fs.SetLength(3L * part);
            async Task WaitFor(Func<string[], bool> done)
            {
                var until = DateTime.UtcNow.AddSeconds(180);
                while (DateTime.UtcNow < until)
                {
                    var v = await m.Service.StatsAsync(m.Session.Id);
                    if (v is { UploadsInProgress: 0, UploadsQueued: 0 } && done(Stored())) return;
                    await Task.Delay(500);
                }
                throw new TimeoutException("upload did not finish: " + string.Join(", ", Stored()));
            }
            await WaitFor(s => s.Count(n => Final(n) && n.Contains(".cloudssdzero-")) == 3);
            foreach (var n in Stored()) output.WriteLine($"after reservation: {n} {new FileInfo(Path.Combine(dir, n)).Length:N0} bytes");
            Assert.All(Stored().Where(Final), n => Assert.True(new FileInfo(Path.Combine(dir, n)).Length < 200, n + " should be a marker"));

            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write)) { fs.Position = fillAt; await fs.WriteAsync(fill); }
            await WaitFor(s => s.Count(n => Final(n) && n.Contains(".cloudssdzero-")) == 2 && s.Count(n => Final(n) && !n.Contains(".cloudssdzero-")) == 1);
            foreach (var n in Stored()) output.WriteLine($"after filling: {n} {new FileInfo(Path.Combine(dir, n)).Length:N0} bytes");
        }

        await using (var m = await MountAsync(Path.Combine(_root, "cache-z2")))
        {
            var path = m.P("steamapps/downloading/x64a.rpf");
            Assert.Equal(3L * part, new FileInfo(path).Length);
            var expected = new byte[3 * part];
            fill.CopyTo(expected, fillAt);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), Bytes.HashFile(path));
        }
    }
}
