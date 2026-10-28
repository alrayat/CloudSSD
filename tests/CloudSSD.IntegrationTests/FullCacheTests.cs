using CloudSSD.Cache;
using CloudSSD.Games;
using Xunit.Abstractions;

namespace CloudSSD.IntegrationTests;

/// <summary>Mode C "Fully cached": a whole game folder is downloaded through the drive and verified in rclone's cache index.</summary>
public sealed class FullCacheTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Whole_game_folder_is_cached_and_verified_then_read_without_network()
    {
        if (!TestEnv.WinFspAvailable) return;
        var ds = Dataset.Create(Path.Combine(TestEnv.WorkRoot, "dataset"));
        var cache = Path.Combine(TestEnv.WorkRoot, "cache-fullcache-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            await using var h = await MountHarness.StartAsync("fullcache-" + Guid.NewGuid().ToString("N")[..4], ds.Root, mbps: 200, cacheDirOverride: cache);
            var reports = 0;
            var r = await CacheHydrator.HydrateAsync(h.P("Game Data"), h.Session.CacheDir, "", "Game Data", 4,
                new SyncProgress<HydrateProgress>(_ => reports++), CancellationToken.None);
            output.WriteLine(r.Message);
            Assert.True(r.Complete, r.Message);
            Assert.Equal(3 * Dataset.PakSize, r.TotalBytes);
            Assert.Equal(r.TotalBytes, r.VerifiedBytes);
            Assert.True(reports > 0);
            Assert.All(CacheInspector.Enumerate(h.Session.CacheDir).Where(f => f.RemotePath.StartsWith("Game Data/")), f => Assert.True(f.Covers(0, f.LogicalSize)));

            // Everything is local now: reads succeed with the cloud offline.
            h.Server.Offline = true;
            Assert.Equal(ds.Hashes["Game Data/level 2.pak"], Bytes.HashFile(h.P("Game Data/level 2.pak")));
        }
        finally { TestEnv.Cleanup(cache); }
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T>
    {
        public void Report(T value) => a(value);
    }
}
