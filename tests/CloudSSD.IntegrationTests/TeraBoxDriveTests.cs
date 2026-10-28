using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.Mount;
using CloudSSD.SimCloud;
using CloudSSD.TeraBox;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CloudSSD.IntegrationTests;

/// <summary>
/// One TeraBox Open Platform mock + the real WebDAV bridge + a real rclone/WinFsp mount, shared by the drive tests.
/// Everything except TeraBox's servers is the production code path.
/// </summary>
public sealed class TeraBoxDriveFixture : IAsyncLifetime
{
    public const string AppRoot = "/From: Other Applications/CloudSSD";
    public string Root { get; } = Path.Combine(TestEnv.WorkRoot, "terabox-" + Guid.NewGuid().ToString("N")[..6]);
    public TeraBoxMockServer Mock { get; private set; } = null!;
    public TeraBoxApi Api { get; private set; } = null!;
    public TeraBoxWebDavBridge Bridge { get; private set; } = null!;
    public MountHarness Mount { get; private set; } = null!;
    public string Secret { get; } = Guid.NewGuid().ToString("N");

    public async Task<string> BridgePropfindAsync(string rel)
    {
        var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), Bridge.Url + rel);
        req.Headers.Add("Depth", "1");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("cloudssd-cli:" + Secret)));
        using var r = await _http.SendAsync(req);
        var xml = await r.Content.ReadAsStringAsync();
        return string.Join(", ", System.Text.RegularExpressions.Regex.Matches(xml, "<d:href>([^<]*)</d:href>").Select(m => m.Groups[1].Value));
    }
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private ILoggerFactory _lf = null!;

    public string Cloud(string rel) => Mock.DiskPath(AppRoot + "/" + rel.Replace('\\', '/'));

    public async Task InitializeAsync()
    {
        Mock = new TeraBoxMockServer(Path.Combine(Root, "cloud"), Path.Combine(Root, "state")) { QuotaTotal = 1024L * ByteSize.GiB };
        var client = TeraBoxClientFile.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["client_id"] = Mock.ClientId, ["client_secret"] = Mock.ClientSecret, ["private_secret"] = Mock.PrivateSecret, ["auth_base_url"] = Mock.BaseUrl
        }));
        var store = new TeraBoxTokenStore(Path.Combine(Root, "tokens.key"));
        var auth = new TeraBoxAuth(client, _http);
        store.Save(await auth.WaitForDeviceApprovalAsync(await auth.StartDeviceAsync(default), default));
        _lf = TestEnv.Logging(Path.Combine(Root, "logs"));
        Api = new TeraBoxApi(client, store, _http, _lf.CreateLogger("TeraBox"));
        var secret = Secret;
        Bridge = new TeraBoxWebDavBridge(Api, "cloudssd-cli", secret, Path.Combine(Root, "staging"), _lf.CreateLogger("Bridge")) { ListingTtl = TimeSpan.FromSeconds(2) };
        var rclone = await TestEnv.RcloneAsync();
        var obscured = (await new RcloneRunner(rclone.Path, new Dictionary<string, string?>()).RunAsync(["obscure", secret], TimeSpan.FromSeconds(20))).StdOut.Trim();
        var def = new MountDefinition
        {
            DisplayName = "TeraBox Cloud SSD", RemoteName = ":webdav", DriveLetter = "T",
            ExtraFlags = { ["webdav-url"] = Bridge.Url, ["webdav-vendor"] = "other", ["webdav-user"] = "cloudssd-cli", ["webdav-pass"] = obscured }
        };
        Mount = await MountHarness.StartAsync("terabox", Path.Combine(Root, "unused-sim"), definition: def, remoteType: "webdav",
            cache: c => c.WriteBack = TimeSpan.FromSeconds(1));
    }

    public async Task DisposeAsync()
    {
        await Mount.DisposeAsync();
        await Bridge.DisposeAsync();
        await Mock.DisposeAsync();
        _http.Dispose();
        _lf.Dispose();
        TestEnv.Cleanup(Root);
        TestEnv.Cleanup(Path.Combine(TestEnv.WorkRoot, "cache-terabox"));
    }
}

public sealed class TeraBoxDriveTests(TeraBoxDriveFixture f, ITestOutputHelper output) : IClassFixture<TeraBoxDriveFixture>
{
    private static byte[] RandomBytes(int size, int seed) { var b = new byte[size]; new Random(seed).NextBytes(b); return b; }
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static async Task<bool> WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(250); }
        return cond();
    }

    private static string? HashOrNull(string path)
    {
        try { return File.Exists(path) ? Bytes.HashFile(path) : null; } catch (IOException) { return null; }
    }

    [Fact]
    public async Task Writes_through_T_reach_TeraBox_intact_and_cloud_files_read_back_exactly()
    {
        Assert.True(TestEnv.WinFspAvailable);
        output.WriteLine($"Mounted at {f.Mount.Root}");
        var dir = f.Mount.P("Games/Write Test");
        Directory.CreateDirectory(dir);
        var files = new Dictionary<string, byte[]>
        {
            ["small.txt"] = "hello terabox\n"u8.ToArray(),
            ["exact-4MiB.bin"] = RandomBytes(4 * 1024 * 1024, 1),
            ["three-shards.pak"] = RandomBytes(9 * 1024 * 1024 + 77, 2),
            ["twenty.bin"] = RandomBytes(20 * 1024 * 1024, 3),
        };
        foreach (var (name, data) in files) await File.WriteAllBytesAsync(Path.Combine(dir, name), data);
        foreach (var (name, data) in files)
        {
            var cloud = f.Cloud("Games/Write Test/" + name);
            Assert.True(await WaitUntil(() => HashOrNull(cloud) == Sha(data), TimeSpan.FromSeconds(90)), $"{name} should be uploaded to TeraBox intact");
        }
        output.WriteLine($"Uploaded {files.Count} files via superfile2 shards; bridge status: {JsonSerializer.Serialize(f.Bridge.Status())}");

        // A file that exists only in the cloud (uploaded elsewhere): cold read through T:, full and random ranges.
        var cold = RandomBytes(48 * 1024 * 1024 + 4321, 9);
        Directory.CreateDirectory(Path.GetDirectoryName(f.Cloud("Games/Cold/level.pak"))!);
        await File.WriteAllBytesAsync(f.Cloud("Games/Cold/level.pak"), cold);
        await Task.Delay(TimeSpan.FromSeconds(3)); // bridge listing TTL (2 s in tests)
        await f.Mount.Service.ForgetAsync(f.Mount.Session.Id);
        var rng = new Random(5);
        for (int i = 0; i < 40; i++)
        {
            var off = (long)(rng.NextDouble() * (cold.Length - 70_000));
            var len = rng.Next(1, 65_536);
            Assert.Equal(cold.AsSpan((int)off, len).ToArray(), Bytes.ReadRange(f.Mount.P("Games/Cold/level.pak"), off, len));
        }
        Assert.Equal(Sha(cold), Bytes.HashFile(f.Mount.P("Games/Cold/level.pak")));
        Assert.True(f.Mock.RangeRequests > 0, "reads should use HTTP Range against the dlink");
        output.WriteLine($"Cold reads OK: {f.Mock.RangeRequests} ranged dlink requests, {ByteSize.Format(f.Mock.DownloadBytes)} downloaded for a {ByteSize.Format(cold.Length)} file");
    }

    [Fact]
    public async Task Rename_move_delete_on_T_are_applied_in_TeraBox()
    {
        var a = f.Mount.P("Ops/a");
        var b = f.Mount.P("Ops/b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        await File.WriteAllTextAsync(Path.Combine(a, "one.txt"), "one");
        Assert.True(await WaitUntil(() => File.Exists(f.Cloud("Ops/a/one.txt")), TimeSpan.FromSeconds(60)));
        File.Move(Path.Combine(a, "one.txt"), Path.Combine(a, "renamed.txt"));
        Assert.True(await WaitUntil(() => File.Exists(f.Cloud("Ops/a/renamed.txt")) && !File.Exists(f.Cloud("Ops/a/one.txt")), TimeSpan.FromSeconds(60)));
        File.Move(Path.Combine(a, "renamed.txt"), Path.Combine(b, "moved.txt"));
        Assert.True(await WaitUntil(() => File.Exists(f.Cloud("Ops/b/moved.txt")) && !File.Exists(f.Cloud("Ops/a/renamed.txt")), TimeSpan.FromSeconds(60)));
        Assert.Equal("one", await File.ReadAllTextAsync(f.Cloud("Ops/b/moved.txt")));

        // Atomic replace pattern used by installers and games (write temp, then replace).
        await File.WriteAllTextAsync(Path.Combine(b, "settings.tmp"), "v2");
        File.Move(Path.Combine(b, "settings.tmp"), Path.Combine(b, "moved.txt"), overwrite: true);
        Assert.True(await WaitUntil(() => File.Exists(f.Cloud("Ops/b/moved.txt")) && File.ReadAllText(f.Cloud("Ops/b/moved.txt")) == "v2", TimeSpan.FromSeconds(60)));

        File.Delete(Path.Combine(b, "moved.txt"));
        Directory.Delete(a);
        Assert.True(await WaitUntil(() => !File.Exists(f.Cloud("Ops/b/moved.txt")) && !Directory.Exists(f.Cloud("Ops/a")), TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task Drive_reports_TeraBox_quota_as_capacity()
    {
        await Task.Delay(500);
        var drive = new DriveInfo(f.Mount.Root);
        output.WriteLine($"T: total {ByteSize.Format(drive.TotalSize)}, free {ByteSize.Format(drive.AvailableFreeSpace)} (mock quota {ByteSize.Format(f.Mock.QuotaTotal)})");
        Assert.Equal(f.Mock.QuotaTotal, drive.TotalSize);
        Assert.True(drive.AvailableFreeSpace <= f.Mock.QuotaTotal);
    }

    [Fact]
    public async Task Executable_and_DRM_free_demo_game_run_from_T()
    {
        // Small trusted executable copied into the cloud, launched from T:.
        Directory.CreateDirectory(Path.GetDirectoryName(f.Cloud("Apps/hostname.exe"))!);
        File.Copy(@"C:\Windows\System32\hostname.exe", f.Cloud("Apps/hostname.exe"), true);
        await Task.Delay(TimeSpan.FromSeconds(3));
        await f.Mount.Service.ForgetAsync(f.Mount.Session.Id);
        var p = Process.Start(new ProcessStartInfo(f.Mount.P("Apps/hostname.exe")) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var name = (await p.StandardOutput.ReadToEndAsync()).Trim();
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        Assert.Equal(Environment.MachineName, name, ignoreCase: true);

        // DRM-free CLOUDSSD demo game (384 MiB) stored in TeraBox, launched straight from T: (cold cache).
        var src = Path.Combine(TestEnv.WorkRoot, "dataset", "Games", "CloudSSD Demo Game");
        if (!File.Exists(Path.Combine(src, "CloudSSD.DemoGame.exe"))) { output.WriteLine("demo game not installed in the dataset; skipped"); return; }
        var dst = f.Cloud("Games/CloudSSD Demo Game");
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
        await Task.Delay(TimeSpan.FromSeconds(3));
        await f.Mount.Service.ForgetAsync(f.Mount.Session.Id);
        output.WriteLine("Bridge root: " + await f.BridgePropfindAsync(""));
        output.WriteLine("Listing of T:\\: " + string.Join(", ", Directory.GetFileSystemEntries(f.Mount.Root).Select(Path.GetFileName)));
        output.WriteLine("Listing of T:\\Games: " + string.Join(", ", Directory.GetFileSystemEntries(f.Mount.P("Games")).Select(Path.GetFileName)));
        output.WriteLine("Demo folder: " + string.Join(", ", Directory.GetFileSystemEntries(f.Mount.P("Games/CloudSSD Demo Game")).Select(Path.GetFileName)));
        f.Mock.DownloadMbps = 70;
        var results = Path.Combine(f.Root, "demo.jsonl");
        try
        {
            foreach (var (label, level) in new[] { ("cold", 0), ("warm", 0) })
            {
                var before = f.Mock.DownloadBytes;
                var sw = Stopwatch.StartNew();
                var game = Process.Start(new ProcessStartInfo(f.Mount.P("Games/CloudSSD Demo Game/CloudSSD.DemoGame.exe"), $"--level {level} --autoexit --results \"{results}\"")
                    { UseShellExecute = false, WorkingDirectory = f.Mount.P("Games/CloudSSD Demo Game") })!;
                await game.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
                Assert.Equal(0, game.ExitCode);
                var last = JsonDocument.Parse(File.ReadAllLines(results)[^1]).RootElement;
                output.WriteLine($"Demo game from T: ({label}, 70 Mbps mock): load {last.GetProperty("loadMs").GetDouble():0} ms, process {sw.Elapsed.TotalSeconds:0.0} s, downloaded {ByteSize.Format(f.Mock.DownloadBytes - before)}");
            }
        }
        finally { f.Mock.DownloadMbps = 0; }
    }

    [Fact]
    public async Task Network_outage_during_uncached_read_fails_then_recovers()
    {
        var data = RandomBytes(16 * 1024 * 1024, 11);
        Directory.CreateDirectory(Path.GetDirectoryName(f.Cloud("Net/outage.bin"))!);
        await File.WriteAllBytesAsync(f.Cloud("Net/outage.bin"), data);
        await Task.Delay(TimeSpan.FromSeconds(3));
        await f.Mount.Service.ForgetAsync(f.Mount.Session.Id);
        Assert.True(File.Exists(f.Mount.P("Net/outage.bin")));
        f.Mock.Offline = true;
        try
        {
            var sw = Stopwatch.StartNew();
            var failed = false;
            byte[]? got = null;
            try { got = await Task.Run(() => Bytes.ReadRange(f.Mount.P("Net/outage.bin"), 8 * 1024 * 1024, 4096)).WaitAsync(TimeSpan.FromMinutes(3)); }
            catch (IOException) { failed = true; }
            output.WriteLine($"Offline read: {(failed ? "failed with an I/O error" : $"returned {got!.Length} bytes, correct={got.AsSpan().SequenceEqual(data.AsSpan(8 * 1024 * 1024, 4096))}")} after {sw.Elapsed.TotalSeconds:0.0} s; mock downloads {f.Mock.DownloadBytes}");
            Assert.True(failed, "an uncached read must fail (not return wrong data) while TeraBox is unreachable");
        }
        finally { f.Mock.Offline = false; }
        Assert.Equal(data.AsSpan(8 * 1024 * 1024, 4096).ToArray(), Bytes.ReadRange(f.Mount.P("Net/outage.bin"), 8 * 1024 * 1024, 4096));
    }

    [Fact]
    public async Task Expired_token_mid_session_is_refreshed_without_errors()
    {
        var data = RandomBytes(2 * 1024 * 1024, 21);
        Directory.CreateDirectory(Path.GetDirectoryName(f.Cloud("Tok/t.bin"))!);
        await File.WriteAllBytesAsync(f.Cloud("Tok/t.bin"), data);
        await Task.Delay(TimeSpan.FromSeconds(3));
        await f.Mount.Service.ForgetAsync(f.Mount.Session.Id);
        var before = f.Mock.RefreshCount;
        f.Mock.ExpireAccessTokens();
        Assert.Equal(Sha(data), Bytes.HashFile(f.Mount.P("Tok/t.bin")));
        Assert.True(f.Mock.RefreshCount > before);
    }

    [Fact]
    public async Task File_over_plan_limit_is_kept_locally_and_not_split()
    {
        f.Mock.MaxFileSize = 6 * 1024 * 1024;
        try
        {
            var data = RandomBytes(8 * 1024 * 1024, 31);
            Directory.CreateDirectory(f.Mount.P("Limit"));
            await File.WriteAllBytesAsync(f.Mount.P("Limit/too-big.bin"), data);
            await Task.Delay(TimeSpan.FromSeconds(15));
            Assert.False(File.Exists(f.Cloud("Limit/too-big.bin")), "the upload must be refused, not split around the plan limit");
            Assert.Equal(Sha(data), Bytes.HashFile(f.Mount.P("Limit/too-big.bin"))); // still readable from the local cache, not lost
            output.WriteLine($"Bridge last error: {f.Bridge.Status()}");
        }
        finally { f.Mock.MaxFileSize = null; }
    }
}
