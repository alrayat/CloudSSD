using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CloudSSD.SimCloud;
using CloudSSD.TeraBox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudSSD.Tests;

/// <summary>TeraBox Open Platform client + WebDAV bridge against the documented-API mock (no network, no credentials).</summary>
public sealed class TeraBoxTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudssd-cli-tb-" + Guid.NewGuid().ToString("N")[..8]);
    private TeraBoxMockServer _mock = null!;
    private TeraBoxApi _api = null!;
    private TeraBoxWebDavBridge _bridge = null!;
    private HttpClient _dav = null!;
    private readonly HttpClient _http = new();

    public async Task InitializeAsync()
    {
        _mock = new TeraBoxMockServer(Path.Combine(_root, "cloud"), Path.Combine(_root, "state"));
        var client = TeraBoxClientFile.Parse($$"""
            {"client_id":"{{_mock.ClientId}}","client_secret":"{{_mock.ClientSecret}}","private_secret":"{{_mock.PrivateSecret}}",
             "app_name":"CloudSSD","auth_base_url":"{{_mock.BaseUrl}}"}
            """);
        var store = new TeraBoxTokenStore(Path.Combine(_root, "tokens.key"));
        var auth = new TeraBoxAuth(client, _http);
        var dev = await auth.StartDeviceAsync(CancellationToken.None);
        Assert.NotEmpty(dev.QrPng);
        store.Save(await auth.WaitForDeviceApprovalAsync(dev, CancellationToken.None));
        _api = new TeraBoxApi(client, store, _http, NullLogger.Instance);
        _bridge = new TeraBoxWebDavBridge(_api, "cloudssd-cli", "pw-123", Path.Combine(_root, "staging"), NullLogger.Instance);
        _dav = new HttpClient { BaseAddress = new Uri(_bridge.Url) };
        _dav.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String("cloudssd-cli:pw-123"u8.ToArray()));
    }

    public async Task DisposeAsync()
    {
        _dav.Dispose();
        await _bridge.DisposeAsync();
        await _mock.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static byte[] Random(int size, int seed) { var b = new byte[size]; new Random(seed).NextBytes(b); return b; }

    private Task<HttpResponseMessage> Send(string method, string path, HttpContent? body = null, Action<HttpRequestMessage>? edit = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
        edit?.Invoke(req);
        return _dav.SendAsync(req);
    }

    [Fact]
    public void Sign_matches_documented_rule()
    {
        var c = TeraBoxClientFile.Parse("""{"client_id":"a","client_secret":"b","private_secret":"c"}""");
        Assert.Equal(Convert.ToHexString(MD5.HashData("a_1705570481_b_c"u8)).ToLowerInvariant(), c.Sign(1705570481));
        Assert.Equal("/From: Other Applications/CloudSSD", c.AppRoot);
        Assert.Throws<FormatException>(() => TeraBoxClientFile.Parse("""{"client_id":"a","client_secret":"b","private_secret":"c","auth_base_url":"http://evil.example"}"""));
    }

    [Fact]
    public async Task Rejects_requests_without_bridge_password()
    {
        using var anon = new HttpClient();
        var r = await anon.SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), _bridge.Url));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Upload_multi_shard_then_read_full_and_ranges()
    {
        var data = Random(10 * 1024 * 1024 + 12345, 7); // 3 shards: 4 MiB, 4 MiB, rest
        var mk = await Send("MKCOL", "Games/"); Assert.True(mk.StatusCode == HttpStatusCode.Created, await mk.Content.ReadAsStringAsync());
        var put = await Send("PUT", "Games/data.pak", new ByteArrayContent(data)); Assert.True(put.StatusCode == HttpStatusCode.Created, await put.Content.ReadAsStringAsync());
        Assert.Equal(data, File.ReadAllBytes(_mock.DiskPath("/From: Other Applications/CloudSSD/Games/data.pak")));

        Assert.Equal(data, await (await Send("GET", "Games/data.pak")).Content.ReadAsByteArrayAsync());
        foreach (var (off, len) in new[] { (0L, 100), (4L * 1024 * 1024 - 10, 20), (data.Length - 7L, 7), (5_000_000L, 1_000_000) })
        {
            var r = await Send("GET", "Games/data.pak", edit: q => q.Headers.Range = new RangeHeaderValue(off, off + len - 1));
            Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode);
            Assert.Equal(data.AsSpan((int)off, len).ToArray(), await r.Content.ReadAsByteArrayAsync());
        }
        Assert.True(_bridge.UpstreamRangeSupported);
    }

    [Fact]
    public async Task Range_reads_still_correct_when_dlinks_ignore_Range()
    {
        _mock.DlinkSupportsRange = false;
        var data = Random(3 * 1024 * 1024, 3);
        await Send("PUT", "r.bin", new ByteArrayContent(data));
        var r = await Send("GET", "r.bin", edit: q => q.Headers.Range = new RangeHeaderValue(2_000_000, 2_000_099));
        Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode);
        Assert.Equal(data.AsSpan(2_000_000, 100).ToArray(), await r.Content.ReadAsByteArrayAsync());
        Assert.False(_bridge.UpstreamRangeSupported);
    }

    [Fact]
    public async Task Propfind_lists_entries_and_reports_quota()
    {
        _mock.QuotaTotal = 1024L * 1024 * 1024 * 1024;
        await Send("MKCOL", "dir/");
        await Send("PUT", "dir/a.txt", new StringContent("hello"));
        await Send("PUT", "dir/ñ b+c.txt", new StringContent("unicode"));
        var root = await (await Send("PROPFIND", "", edit: q => q.Headers.Add("Depth", "0"))).Content.ReadAsStringAsync();
        Assert.Contains("quota-available-bytes", root);
        Assert.Contains("quota-used-bytes", root);
        var r = await Send("PROPFIND", "dir/", edit: q => q.Headers.Add("Depth", "1"));
        Assert.Equal((HttpStatusCode)207, r.StatusCode);
        var xml = await r.Content.ReadAsStringAsync();
        Assert.Contains("<d:href>/dir/a.txt</d:href>", xml);
        Assert.Contains(Uri.EscapeDataString("ñ b+c.txt"), xml);
        Assert.Contains("<d:getcontentlength>5</d:getcontentlength>", xml);
        Assert.Equal(HttpStatusCode.NotFound, (await Send("PROPFIND", "missing.txt")).StatusCode);
    }

    [Fact]
    public async Task Rename_move_copy_overwrite_and_delete()
    {
        await Send("MKCOL", "a/");
        await Send("MKCOL", "b/");
        await Send("PUT", "a/x.txt", new StringContent("one"));
        await Send("PUT", "b/y.txt", new StringContent("two"));
        void Dest(HttpRequestMessage q, string d, bool overwrite = true) { q.Headers.Add("Destination", _bridge.Url + d); q.Headers.Add("Overwrite", overwrite ? "T" : "F"); }

        Assert.Equal(HttpStatusCode.Created, (await Send("MOVE", "a/x.txt", edit: q => Dest(q, "a/x2.txt"))).StatusCode);       // rename
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Send("MOVE", "a/x2.txt", edit: q => Dest(q, "b/y.txt", false))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send("MOVE", "a/x2.txt", edit: q => Dest(q, "b/y.txt"))).StatusCode);     // atomic-replace pattern
        Assert.Equal("one", await (await Send("GET", "b/y.txt")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await Send("COPY", "b/y.txt", edit: q => Dest(q, "a/copy.txt"))).StatusCode);
        Assert.Equal("one", await (await Send("GET", "a/copy.txt")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await Send("DELETE", "b/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send("GET", "b/y.txt")).StatusCode);
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_transparently_and_persisted()
    {
        await Send("PUT", "t.txt", new StringContent("token test"));
        _mock.ExpireAccessTokens();
        _bridge.ListingTtl = TimeSpan.Zero;
        Assert.Equal("token test", await (await Send("GET", "t.txt")).Content.ReadAsStringAsync());
        Assert.True(_mock.RefreshCount >= 1);
        Assert.True(_api.Refreshes >= 1);
    }

    [Fact]
    public async Task File_larger_than_plan_limit_fails_clearly_and_is_not_split()
    {
        _mock.MaxFileSize = 5 * 1024 * 1024; // stands in for the free plan's per-file limit
        var r = await Send("PUT", "big.bin", new ByteArrayContent(Random(6 * 1024 * 1024, 1)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.False(File.Exists(_mock.DiskPath("/From: Other Applications/CloudSSD/big.bin")));
    }

    [Fact]
    public async Task Paths_cannot_escape_the_application_folder()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _api.ListAsync(_api.Abs("../secret"), CancellationToken.None));
        var e = await Assert.ThrowsAsync<TeraBoxException>(() => _api.ListAsync("/", CancellationToken.None));
        Assert.Equal(31064, e.Errno);
    }

    [Fact]
    public async Task Throttling_is_retried_with_backoff()
    {
        await Send("PUT", "th.txt", new StringContent("x"));
        _mock.ThrottleNextCalls = 2;
        _bridge.ListingTtl = TimeSpan.Zero;
        for (int i = 0; i < 5; i++) Assert.Equal("x", await (await Send("GET", "th.txt")).Content.ReadAsStringAsync());
        Assert.True(_api.Throttled > 0);
    }

    [Fact]
    public async Task Rclone_webdav_sees_folders_created_directly_in_the_cloud()
    {
        var rclone = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "rclone", "rclone.exe");
        if (!File.Exists(rclone)) return;
        await Send("MKCOL", "Apps/");
        Directory.CreateDirectory(_mock.DiskPath("/From: Other Applications/CloudSSD/Games/CloudSSD Demo Game"));
        File.WriteAllText(_mock.DiskPath("/From: Other Applications/CloudSSD/Games/CloudSSD Demo Game/a.txt"), "x");
        var obscure = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(rclone, "obscure pw-123") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var ob = (await obscure.StandardOutput.ReadToEndAsync()).Trim();
        var psi = new System.Diagnostics.ProcessStartInfo(rclone) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "lsjson", "-R", ":webdav:", "--webdav-url", _bridge.Url, "--webdav-vendor", "other", "--webdav-user", "cloudssd-cli", "--webdav-pass", ob, "--config", "NUL" }) psi.ArgumentList.Add(a);
        var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(stdout.Contains("CloudSSD Demo Game"), stdout + "\n" + stderr);
    }

    [Fact]
    public async Task Mkcol_with_missing_parent_returns_409_so_clients_create_parents_first()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await Send("MKCOL", "x/y/")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send("PUT", "x/y/z.txt", new StringContent("z"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send("MKCOL", "x/")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send("MKCOL", "x/y/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send("PROPFIND", "nope/deeper/file.txt")).StatusCode);
    }
}
