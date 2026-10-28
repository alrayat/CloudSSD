using System.Security.Cryptography;
using CloudSSD.Core;
using Xunit.Abstractions;

namespace CloudSSD.IntegrationTests;

/// <summary>
/// Large-file virtualization: one logical file stored as several smaller remote objects (rclone "chunker" overlay),
/// mounted through the normal CloudSSD mount path. Verifies that applications see one ordinary file and that random
/// reads across chunk boundaries return the original bytes. The backing store is a local folder so the stored chunk
/// objects can be inspected. (CloudSSD does not use this to get around a provider's plan limits; see
/// LARGE_FILE_VIRTUALIZATION.md.)
/// </summary>
public sealed class LargeFileVirtualizationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Logical_file_split_into_chunks_reads_back_exactly_at_random_offsets()
    {
        var store = Path.Combine(TestEnv.WorkRoot, "chunk-store-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(store);
        var def = new MountDefinition
        {
            DisplayName = "Chunked", RemoteName = ":chunker",
            ExtraFlags = { ["chunker-remote"] = store, ["chunker-chunk-size"] = "8Mi", ["chunker-hash-type"] = "sha1all" }
        };
        await using var h = await MountHarness.StartAsync("chunker", Path.Combine(store, "..", "unused-sim"), definition: def, remoteType: "chunker",
            cache: c => c.WriteBack = TimeSpan.FromSeconds(1));

        var data = new byte[50 * 1024 * 1024 + 12345];
        new Random(42).NextBytes(data);
        Directory.CreateDirectory(h.P("Game"));
        await File.WriteAllBytesAsync(h.P("Game/GameData.pak"), data);

        // Wait for the upload to the backing store: 7 chunk objects of at most 8 MiB.
        var until = DateTime.UtcNow.AddSeconds(60);
        string[] chunks = [];
        while (DateTime.UtcNow < until)
        {
            chunks = Directory.Exists(Path.Combine(store, "Game")) ? Directory.GetFiles(Path.Combine(store, "Game"), "GameData.pak*") : [];
            // Chunks are written under temporary names (…_xxxxxx) and renamed when the upload completes.
            chunks = chunks.Where(c => !System.Text.RegularExpressions.Regex.IsMatch(c, @"\.rclone_chunk\.\d+_\w+$")).ToArray();
            if (chunks.Count(c => c.Contains(".rclone_chunk.")) == 7) break;
            await Task.Delay(300);
        }
        output.WriteLine("Stored objects: " + string.Join(", ", chunks.Select(Path.GetFileName)));
        Assert.Equal(7, chunks.Count(c => c.Contains(".rclone_chunk.")));
        Assert.All(chunks.Where(c => c.Contains(".rclone_chunk.")), c => Assert.True(new FileInfo(c).Length <= 8 * 1024 * 1024));

        // Applications see one ordinary file of the original size.
        Assert.Equal(data.Length, new FileInfo(h.P("Game/GameData.pak")).Length);
        Assert.Single(Directory.GetFiles(h.P("Game")));

        // Cold reads (cache cleared by remounting on a fresh cache) across chunk boundaries.
        await h.Service.UnmountAllAsync(TimeSpan.FromSeconds(30));
        var cold = await MountHarness.StartAsync("chunker-cold", Path.Combine(store, "..", "unused-sim"), definition: new MountDefinition
        {
            DisplayName = "Chunked", RemoteName = ":chunker", ExtraFlags = new(def.ExtraFlags)
        }, remoteType: "chunker", cacheDirOverride: Path.Combine(TestEnv.WorkRoot, "cache-chunker-cold-" + Guid.NewGuid().ToString("N")[..6]));
        var rng = new Random(7);
        var offsets = new List<long> { 8L * 1024 * 1024 - 100, 16L * 1024 * 1024 - 1, data.Length - 50 };
        for (int i = 0; i < 40; i++) offsets.Add((long)(rng.NextDouble() * (data.Length - 300_000)));
        foreach (var off in offsets)
        {
            var len = (int)Math.Min(200_000, data.Length - off);
            Assert.Equal(data.AsSpan((int)off, len).ToArray(), Bytes.ReadRange(cold.P("Game/GameData.pak"), off, len));
        }
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Bytes.HashFile(cold.P("Game/GameData.pak")));
        output.WriteLine($"{offsets.Count} random/boundary reads and a full SHA-256 match the original {ByteSize.Format(data.Length)} file");
        await cold.DisposeAsync();
        TestEnv.Cleanup(store);
    }
}
