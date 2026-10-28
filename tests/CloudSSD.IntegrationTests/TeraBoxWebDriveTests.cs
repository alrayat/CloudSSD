using System.Security.Cryptography;
using CloudSSD.Core;
using CloudSSD.Mount;
using CloudSSD.SimCloud;
using CloudSSD.TeraBox;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CloudSSD.IntegrationTests;

/// <summary>
/// The unofficial website transport as a real drive: mock website surface → TeraBoxWebApi → WebDAV bridge → rclone →
/// WinFsp drive letter. Only TeraBox's servers are replaced; verification against the real account is the probe's job.
/// </summary>
public sealed class TeraBoxWebDriveTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _root = Path.Combine(TestEnv.WorkRoot, "tbweb-" + Guid.NewGuid().ToString("N")[..6]);
    private TeraBoxMockServer _mock = null!;
    private HttpClient _http = null!;
    private TeraBoxWebApi _api = null!;
    private TeraBoxWebDavBridge _bridge = null!;
    private MountHarness _mount = null!;
    private ILoggerFactory _lf = null!;

    private string Cloud(string rel) => _mock.DiskPath(TeraBoxWebApi.DefaultRoot + "/" + rel.Replace('\\', '/'));

    public async Task InitializeAsync()
    {
        _mock = new TeraBoxMockServer(Path.Combine(_root, "cloud"), Path.Combine(_root, "state")) { QuotaTotal = 1024L * ByteSize.GiB };
        var store = new TeraBoxWebSessionStore(Path.Combine(_root, "web.key"));
        store.Save(new TeraBoxWebSession(_mock.BaseUrl, _mock.WebCookieHeader, "Mozilla/5.0 (test)", null, DateTimeOffset.UtcNow));
        _lf = TestEnv.Logging(Path.Combine(_root, "logs"));
        _http = TeraBoxWebApi.CreateHttpClient();
        _api = new TeraBoxWebApi(store, _http, _lf.CreateLogger("TeraBoxWeb"));
        var secret = Guid.NewGuid().ToString("N");
        _bridge = new TeraBoxWebDavBridge(_api, "cloudssd-cli", secret, Path.Combine(_root, "staging"), _lf.CreateLogger("Bridge")) { ListingTtl = TimeSpan.FromSeconds(2) };
        var rclone = await TestEnv.RcloneAsync();
        var obscured = (await new RcloneRunner(rclone.Path, new Dictionary<string, string?>()).RunAsync(["obscure", secret], TimeSpan.FromSeconds(20))).StdOut.Trim();
        var def = new MountDefinition
        {
            DisplayName = "TeraBox web", RemoteName = ":webdav", DriveLetter = null,
            ExtraFlags = { ["webdav-url"] = _bridge.Url, ["webdav-vendor"] = "other", ["webdav-user"] = "cloudssd-cli", ["webdav-pass"] = obscured }
        };
        _mount = await MountHarness.StartAsync("tbweb-" + Guid.NewGuid().ToString("N")[..4], Path.Combine(_root, "unused-sim"), definition: def, remoteType: "webdav",
            cache: c => c.WriteBack = TimeSpan.FromSeconds(1), cacheDirOverride: Path.Combine(_root, "cache"));
    }

    public async Task DisposeAsync()
    {
        await _mount.DisposeAsync();
        await _bridge.DisposeAsync();
        await _mock.DisposeAsync();
        _http.Dispose();
        _lf.Dispose();
        TestEnv.Cleanup(_root);
    }

    private static async Task WaitForAsync(Func<bool> cond, TimeSpan timeout, string what)
    {
        var until = DateTime.UtcNow + timeout;
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(what);
            await Task.Delay(250);
        }
    }

    [Fact]
    public async Task Website_session_drive_writes_reads_ranges_renames_and_survives_page_token_rotation()
    {
        if (!TestEnv.WinFspAvailable) return;
        var drive = _mount.Root;
        output.WriteLine($"Mounted website-session drive at {drive}");

        // Write through the drive: uploaded via precreate/superfile2/create with the page token fetched on demand.
        var data = new byte[13 * 1024 * 1024 + 777];
        new Random(42).NextBytes(data);
        Directory.CreateDirectory(Path.Combine(drive, "Games"));
        await File.WriteAllBytesAsync(Path.Combine(drive, "Games", "pak0.pak"), data);
        await WaitForAsync(() => File.Exists(Cloud("Games/pak0.pak")) && new FileInfo(Cloud("Games/pak0.pak")).Length == data.Length, TimeSpan.FromSeconds(90), "upload");
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(File.ReadAllBytes(Cloud("Games/pak0.pak"))));

        // A file placed in the cloud by someone else, read cold at random offsets through the drive.
        var cloudOnly = new byte[20 * 1024 * 1024];
        new Random(7).NextBytes(cloudOnly);
        Directory.CreateDirectory(Path.GetDirectoryName(Cloud("Games/level1.pak"))!);
        await File.WriteAllBytesAsync(Cloud("Games/level1.pak"), cloudOnly);
        await Task.Delay(_bridge.ListingTtl + TimeSpan.FromMilliseconds(500)); // the bridge caches folder listings briefly
        await _mount.Service.RefreshDirAsync(_mount.Session.Id, "Games");
        var rnd = new Random(3);
        for (int i = 0; i < 25; i++)
        {
            var off = (long)(rnd.NextDouble() * (cloudOnly.Length - 70_000));
            var got = Bytes.ReadRange(Path.Combine(drive, "Games", "level1.pak"), off, 65_536);
            Assert.Equal(cloudOnly.AsSpan((int)off, 65_536).ToArray(), got);
        }
        Assert.True(_bridge.UpstreamRangeSupported);

        // TeraBox makes the page token stale mid-session: the next change fetches a new one transparently.
        _mock.RotateJsToken();
        File.Move(Path.Combine(drive, "Games", "pak0.pak"), Path.Combine(drive, "Games", "pak0-renamed.pak"));
        await WaitForAsync(() => File.Exists(Cloud("Games/pak0-renamed.pak")) && !File.Exists(Cloud("Games/pak0.pak")), TimeSpan.FromSeconds(60), "rename");
        Assert.True(_api.Refreshes >= 2, $"page token fetched {_api.Refreshes} times");

        Assert.Equal(_mock.QuotaTotal, new DriveInfo(drive).TotalSize);
        output.WriteLine($"API calls {_api.Calls}, page-token fetches {_api.Refreshes}, range requests {_mock.RangeRequests}, redirects followed by the transport");
    }
}
