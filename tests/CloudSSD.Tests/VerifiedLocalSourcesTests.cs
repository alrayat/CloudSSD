using System.Security.Cryptography;
using CloudSSD.TeraBox;

namespace CloudSSD.Tests;

public sealed class VerifiedLocalSourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudssd-cli-local-" + Guid.NewGuid().ToString("N"));
    public VerifiedLocalSourcesTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static TeraBoxEntry Entry(string name, byte[] bytes, bool scrambled = false)
    {
        var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        return new(1, "/" + name, name, bytes.Length, false, DateTimeOffset.UtcNow, scrambled ? TeraBoxWebApi.EncodeMd5(md5) : md5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_file_and_chunk_bytes_are_verified_and_originals_are_read_only(bool scrambled)
    {
        byte[] bytes = Enumerable.Range(0, 251).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_root, "game.bin"), bytes);
        var sources = new VerifiedLocalSources([new("Games/Test", _root, 100)]);
        await using (var full = await sources.TryOpenAsync("Games/Test/game.bin", Entry("game.bin", bytes, scrambled), default))
        {
            Assert.NotNull(full);
            Assert.Throws<IOException>(() => File.OpenWrite(Path.Combine(_root, "game.bin")));
            var received = new byte[bytes.Length];
            await full.ReadExactlyAsync(received);
            Assert.Equal(bytes, received);
        }
        await using var chunk = await sources.TryOpenAsync("Games/Test/game.bin.rclone_chunk.003_ab12", Entry("chunk", bytes[200..], scrambled), default);
        Assert.NotNull(chunk);
        Assert.Equal(200, chunk.Position);
        var tail = new byte[51];
        await chunk.ReadExactlyAsync(tail);
        Assert.Equal(bytes[200..], tail);
    }

    [Fact]
    public async Task Website_multipart_checksum_is_checked_against_shards_not_whole_file_md5()
    {
        var bytes = new byte[(4 << 20) + 345];
        new Random(123).NextBytes(bytes);
        await File.WriteAllBytesAsync(Path.Combine(_root, "game.bin"), bytes);
        var hashes = new[]
        {
            Convert.ToHexString(MD5.HashData(bytes.AsSpan(0, 4 << 20))).ToLowerInvariant(),
            Convert.ToHexString(MD5.HashData(bytes.AsSpan(4 << 20))).ToLowerInvariant()
        };
        var control = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(hashes)))).ToLowerInvariant();
        var sources = new VerifiedLocalSources([new("Games/Test", _root)]);
        await using var file = await sources.TryOpenAsync("Games/Test/game.bin", Entry("game.bin", bytes) with { Md5 = control }, default);
        Assert.NotNull(file);
        var received = new byte[bytes.Length];
        await file.ReadExactlyAsync(received);
        Assert.Equal(bytes, received);
    }

    [Fact]
    public async Task Verified_reads_do_not_wait_for_other_asset_verification_including_after_restart()
    {
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(Path.Combine(_root, "game.bin"), bytes);
        var cache = Path.Combine(_root, "verified.json");
        var sources = new VerifiedLocalSources([new("Games/Test", _root)], cache);
        var entry = Entry("game.bin", bytes);
        await using (var first = await sources.TryOpenAsync("Games/Test/game.bin", entry, default))
            Assert.NotNull(first);
        foreach (var instance in new[] { sources, new VerifiedLocalSources([new("Games/Test", _root)], cache) })
        {
            // Hold the verification gate as if another large asset were being hashed.
            var gate = (SemaphoreSlim)typeof(VerifiedLocalSources)
                .GetField("_hashGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(instance)!;
            await gate.WaitAsync();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await using var hit = await instance.TryOpenAsync("Games/Test/game.bin", entry, deadline.Token);
                Assert.NotNull(hit);
                Assert.Equal(1, hit.ReadByte());
            }
            finally { gate.Release(); }
        }
        await File.WriteAllBytesAsync(Path.Combine(_root, "game.bin"), [4, 3, 2, 1]);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "game.bin"), DateTime.UtcNow.AddSeconds(2));
        var restarted = new VerifiedLocalSources([new("Games/Test", _root)], cache);
        Assert.Null(await restarted.TryOpenAsync("Games/Test/game.bin", entry, default));
    }

    [Fact]
    public async Task Corrupt_verification_cache_is_ignored_and_cloud_checksum_is_still_required()
    {
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(Path.Combine(_root, "game.bin"), bytes);
        var cache = Path.Combine(_root, "verified.json");
        await File.WriteAllTextAsync(cache, "{incomplete");
        var sources = new VerifiedLocalSources([new("Games/Test", _root)], cache);
        await using var valid = await sources.TryOpenAsync("Games/Test/game.bin", Entry("game.bin", bytes), default);
        Assert.NotNull(valid);
        Assert.Null(await sources.TryOpenAsync("Games/Test/game.bin", Entry("game.bin", [4, 3, 2, 1]), default));
    }

    [Fact]
    public async Task Missing_hash_mismatch_and_changed_source_fall_back_to_cloud()
    {
        var path = Path.Combine(_root, "game.bin");
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(path, bytes);
        var sources = new VerifiedLocalSources([new("Games/Test", _root)]);
        var e = Entry("game.bin", bytes);
        await using (var valid = await sources.TryOpenAsync("Games/Test/game.bin", e, default)) Assert.NotNull(valid);
        await File.WriteAllBytesAsync(path, [4, 3, 2, 1]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        Assert.Null(await sources.TryOpenAsync("Games/Test/game.bin", e, default));
        Assert.Null(await sources.TryOpenAsync("Games/Test/missing.bin", e, default));
        Assert.Null(await sources.TryOpenAsync("Games/Test/game.bin", e with { Md5 = null }, default));
        Assert.Null(await sources.TryOpenAsync("Games/Test/game.bin", e with { Size = 5 }, default));
    }

    [Theory]
    [InlineData("Games/Test/../game.bin")]
    [InlineData("Games/Test/C:/game.bin")]
    [InlineData("Games/Test/game.bin:stream")]
    [InlineData("Games/Testing/game.bin")]
    [InlineData("Games/Test/game.bin.rclone_chunk.000")]
    [InlineData("Games/Test/game.bin.rclone_chunk.99999999999999999999")]
    public async Task Rejects_traversal_prefix_collisions_and_invalid_chunks(string path)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "game.bin"), [1, 2, 3]);
        var sources = new VerifiedLocalSources([new("Games/Test", _root)]);
        Assert.Null(await sources.TryOpenAsync(path, Entry("game.bin", [1, 2, 3]), default));
    }
}
