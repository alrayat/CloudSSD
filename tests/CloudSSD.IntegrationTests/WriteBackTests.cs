using System.Diagnostics;
using CloudSSD.Cache;
using CloudSSD.Core;
using Xunit.Abstractions;

namespace CloudSSD.IntegrationTests;

/// <summary>Writable remote (rclone local backend standing in for a cloud folder): writes, renames and upload recovery.</summary>
public sealed class WriteBackTests(ITestOutputHelper output)
{
    private static MountDefinition LocalRemote(string backing) => new()
    {
        DisplayName = "rw",
        RemoteName = ":local",
        RemotePath = backing,
    };

    private static string NewBacking(string name)
    {
        var dir = Path.Combine(TestEnv.WorkRoot, $"backing-{name}-{Guid.NewGuid().ToString("N")[..10]}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "existing.txt"), "original");
        return dir;
    }

    private static async Task WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.Elapsed < timeout) await Task.Delay(200);
    }

    [Fact]
    public async Task Writes_renames_and_directory_operations_reach_the_remote_after_write_back()
    {
        var backing = NewBacking("rw");
        await using var h = await MountHarness.StartAsync("rw", backing, definition: LocalRemote(backing), remoteType: "local",
            cache: c => c.WriteBack = TimeSpan.FromSeconds(1), cacheDirOverride: Path.Combine(TestEnv.WorkRoot, "cache-rw-" + Guid.NewGuid().ToString("N")[..8]));

        var payload = new byte[3 * ByteSize.MiB];
        new Random(5).NextBytes(payload);
        Directory.CreateDirectory(h.P("saves"));
        File.WriteAllBytes(h.P("saves/slot1.sav"), payload);
        File.Move(h.P("saves/slot1.sav"), h.P("saves/slot1-renamed.sav"));
        File.WriteAllText(h.P("temp.txt"), "temp");
        File.Delete(h.P("temp.txt"));
        Assert.Equal("original", File.ReadAllText(h.P("existing.txt")));

        var target = Path.Combine(backing, "saves", "slot1-renamed.sav");
        await WaitUntil(() => File.Exists(target) && new FileInfo(target).Length == payload.Length, TimeSpan.FromSeconds(30));
        Assert.Equal(payload, File.ReadAllBytes(target));
        Assert.False(File.Exists(Path.Combine(backing, "saves", "slot1.sav")));
        await WaitUntil(() => !File.Exists(Path.Combine(backing, "temp.txt")), TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(Path.Combine(backing, "temp.txt")));
        output.WriteLine("Write, rename, mkdir and delete propagated to the remote.");
    }

    [Fact]
    public async Task Pending_uploads_survive_an_rclone_crash_and_are_uploaded_after_restart()
    {
        var backing = NewBacking("recover");
        var cache = Path.Combine(TestEnv.WorkRoot, "cache-recover-" + Guid.NewGuid().ToString("N")[..8]);
        var payload = new byte[2 * ByteSize.MiB];
        new Random(6).NextBytes(payload);

        await using (var h = await MountHarness.StartAsync("recover", backing, definition: LocalRemote(backing), remoteType: "local",
                         cache: c => c.WriteBack = TimeSpan.FromSeconds(60), cacheDirOverride: cache))
        {
            File.WriteAllBytes(h.P("progress.sav"), payload);
            await Task.Delay(1500);
            var dirty = CacheInspector.Enumerate(cache).Where(f => f.Dirty).Select(f => f.RemotePath).ToList();
            output.WriteLine("Dirty before crash: " + string.Join(", ", dirty));
            // For path-rooted remotes rclone nests the remote root inside the cache folder, so match on the suffix.
            Assert.Contains(dirty, p => p == "progress.sav" || p.EndsWith("/progress.sav", StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(Path.Combine(backing, "progress.sav")), "write-back delay not reached yet");
            h.Service.DetachAll(); // suppress auto-restart, then simulate a crash
            Process.GetProcessById(h.Session.ProcessId!.Value).Kill();
            await Task.Delay(2000);
        }

        await using var h2 = await MountHarness.StartAsync("recover2", backing, definition: LocalRemote(backing), remoteType: "local",
            cache: c => c.WriteBack = TimeSpan.FromSeconds(1), cacheDirOverride: cache);
        var target = Path.Combine(backing, "progress.sav");
        await WaitUntil(() => File.Exists(target) && new FileInfo(target).Length == payload.Length, TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(target), "pending upload should be recovered from the cache after restart");
        Assert.Equal(payload, File.ReadAllBytes(target));
        output.WriteLine("Pending upload recovered after crash.");
    }
}
