using System.Text.Json;
using CloudSSD.Mount;

namespace CloudSSD.Tests;

public sealed class PausedSteamWritesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudssd-cli-paused-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(_root, "cache", "m-test");
    private const string Rel = "remote/SteamLibrary/steamapps/downloading/3240220/game.rpf";
    private string Data => Path.Combine(Cache, "vfs", Rel);
    private string Meta => Path.Combine(Cache, "vfsMeta", Rel);
    private byte[] Seed(bool dirty = true, bool complete = false)
    {
        var bytes = new byte[8192]; new Random(8).NextBytes(bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(Data)!); Directory.CreateDirectory(Path.GetDirectoryName(Meta)!);
        File.WriteAllBytes(Data, bytes);
        File.WriteAllText(Meta, JsonSerializer.Serialize(new { Size = 8192, Dirty = dirty, Rs = new[] { new { Pos = 0, Size = complete ? 8192 : 1024 } } }));
        return bytes;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Pause_and_restore_keep_exact_data_and_range_metadata_and_skip_clean_files()
    {
        var bytes = Seed(); var metadata = File.ReadAllText(Meta);
        var store = new PausedSteamWrites(Cache);
        Assert.Empty(store.Pause(123));
        Assert.Equal(1024, Assert.Single(store.Pause(3240220)).CachedBytes);
        Assert.False(File.Exists(Data)); Assert.False(File.Exists(Meta));
        store.Restore(); Assert.Empty(store.All());
        Assert.Equal(bytes, File.ReadAllBytes(Data)); Assert.Equal(metadata, File.ReadAllText(Meta));
        Seed(dirty: false); Assert.Empty(store.Pause(3240220));
        Seed(complete: true); Assert.Empty(store.Pause(3240220));
    }

    [Fact]
    public void Newer_dirty_writes_are_never_overwritten_by_an_older_paused_transaction()
    {
        Seed(); var store = new PausedSteamWrites(Cache); store.Pause(3240220);
        var newer = Seed(); newer[0] ^= 1; File.WriteAllBytes(Data, newer); // same range written again
        store.Restore();
        Assert.Equal(newer, File.ReadAllBytes(Data)); Assert.Empty(store.All());
        using var info = JsonDocument.Parse(File.ReadAllText(Meta));
        var r = Assert.Single(info.RootElement.GetProperty("Rs").EnumerateArray());
        Assert.Equal((0, 1024), (r.GetProperty("Pos").GetInt64(), r.GetProperty("Size").GetInt64()));
    }

    [Fact]
    public void Paused_bytes_are_merged_into_a_newer_write_of_other_ranges()
    {
        var old = Seed(); var store = new PausedSteamWrites(Cache); store.Pause(3240220); // paused: bytes 0..1023
        // Steam later writes bytes 4096..6143 of the same file; the rest is still only in the cloud.
        var later = new byte[8192]; new Random(9).NextBytes(later);
        File.WriteAllBytes(Data, later);
        File.WriteAllText(Meta, JsonSerializer.Serialize(new { Size = 8192, Dirty = true, Fingerprint = "8192", Rs = new[] { new { Pos = 4096, Size = 2048 } } }));
        store.Restore();
        var merged = File.ReadAllBytes(Data);
        Assert.Equal(old[..1024], merged[..1024]);          // the paused write survives
        Assert.Equal(later[4096..6144], merged[4096..6144]); // the newer write is untouched
        using var info = JsonDocument.Parse(File.ReadAllText(Meta));
        Assert.True(info.RootElement.GetProperty("Dirty").GetBoolean());
        Assert.Equal("8192", info.RootElement.GetProperty("Fingerprint").GetString());
        Assert.Equal(new[] { (0L, 1024L), (4096L, 2048L) },
            info.RootElement.GetProperty("Rs").EnumerateArray().Select(r => (r.GetProperty("Pos").GetInt64(), r.GetProperty("Size").GetInt64())).ToArray());
        Assert.Empty(store.All());
        // The paused copy is kept as a backup.
        Assert.Single(Directory.GetFiles(store.Root, "data", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("Pausing")]
    [InlineData("Restoring")]
    public void Interrupted_moves_roll_forward_before_the_native_cache_is_opened(string phase)
    {
        var bytes = Seed(); var metadata = File.ReadAllText(Meta);
        var store = new PausedSteamWrites(Cache); store.Pause(3240220);
        var record = Directory.GetFiles(store.Root, "entry.json", SearchOption.AllDirectories).Single();
        var entry = JsonSerializer.Deserialize<PausedSteamWrite>(File.ReadAllText(record))!;
        File.WriteAllText(record, JsonSerializer.Serialize(entry with { State = phase }));
        if (phase == "Pausing") File.WriteAllText(Meta, metadata); // crash after data moved, before source metadata removed
        else { Directory.CreateDirectory(Path.GetDirectoryName(Data)!); File.Move(Path.Combine(Path.GetDirectoryName(record)!, "data"), Data); }
        store.RecoverTransactions();
        if (phase == "Pausing") { Assert.False(File.Exists(Meta)); store.Restore(); }
        Assert.Empty(store.All()); Assert.Equal(bytes, File.ReadAllBytes(Data)); Assert.Equal(metadata, File.ReadAllText(Meta));
    }
}
