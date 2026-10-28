using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CloudSSD.SimCloud;
using CloudSSD.TeraBox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudSSD.Tests;

/// <summary>
/// The unofficial website transport (<see cref="TeraBoxWebApi"/>) through the real WebDAV bridge, against the mock's
/// website surface (session cookie, page token, redirecting dlinks, scrambled MD5s, async tasks). Plus the bridge's
/// upstream validation with a transport that returns wrong, truncated or changed data. No network, no account.
/// </summary>
public sealed class TeraBoxWebTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudssd-cli-tbweb-" + Guid.NewGuid().ToString("N")[..8]);
    private TeraBoxMockServer _mock = null!;
    private TeraBoxWebSessionStore _store = null!;
    private TeraBoxWebApi _api = null!;
    private HttpClient _http = null!;
    private TeraBoxWebDavBridge _bridge = null!;
    private HttpClient _dav = null!;

    public async Task InitializeAsync()
    {
        _mock = new TeraBoxMockServer(Path.Combine(_root, "cloud"), Path.Combine(_root, "state"));
        _store = new TeraBoxWebSessionStore(Path.Combine(_root, "web.key"));
        // No page token yet: the first changing call must fetch it from /main, as after a fresh sign-in.
        _store.Save(new TeraBoxWebSession(_mock.BaseUrl, _mock.WebCookieHeader, "Mozilla/5.0 (test)", null, DateTimeOffset.UtcNow));
        _http = TeraBoxWebApi.CreateHttpClient();
        _api = new TeraBoxWebApi(_store, _http, NullLogger.Instance);
        await StartBridge(_api);
    }

    private async Task StartBridge(ITeraBoxTransport t, Func<string, long>? freeSpace = null)
    {
        if (_bridge != null) await _bridge.DisposeAsync();
        _dav?.Dispose();
        _bridge = new TeraBoxWebDavBridge(t, "cloudssd-cli", "pw-web", Path.Combine(_root, "staging"), NullLogger.Instance, freeSpaceBytes: freeSpace) { ListingTtl = TimeSpan.FromSeconds(2) };
        _dav = new HttpClient { BaseAddress = new Uri(_bridge.Url) };
        _dav.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String("cloudssd-cli:pw-web"u8.ToArray()));
    }

    public async Task DisposeAsync()
    {
        _dav.Dispose();
        await _bridge.DisposeAsync();
        await _mock.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static byte[] Rnd(int size, int seed) { var b = new byte[size]; new Random(seed).NextBytes(b); return b; }

    private Task<HttpResponseMessage> Send(string method, string path, HttpContent? body = null, Action<HttpRequestMessage>? edit = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
        edit?.Invoke(req);
        return _dav.SendAsync(req);
    }

    private string Cloud(string rel) => _mock.DiskPath(TeraBoxWebApi.DefaultRoot + "/" + rel);

    [Fact]
    public async Task Concurrent_uploads_reserve_their_remaining_bytes_instead_of_overcommitting_disk()
    {
        const int kib = 1024;
        var staging = Path.Combine(_root, "staging");
        await StartBridge(_api, _ => 160 * kib - Directory.GetFiles(staging, "*.upload").Sum(f => new FileInfo(f).Length));
        _bridge.StagingFreeSpaceReserveBytes = 64 * kib;
        var bytes = Rnd(64 * kib, 401);
        using var slow = new GatedUpload(bytes);
        var first = Send("PUT", "first.bin", slow);
        try
        {
            await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && Directory.GetFiles(staging, "*.upload").Sum(f => new FileInfo(f).Length) == 0)
                await Task.Delay(10);
            Assert.True(Directory.GetFiles(staging, "*.upload").Sum(f => new FileInfo(f).Length) > 0);
            using var rejected = await Send("PUT", "second.bin", new ByteArrayContent(bytes));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.False(File.Exists(Cloud("second.bin")));
        }
        finally { slow.Release.TrySetResult(); }
        using var completed = await first;
        Assert.True(completed.IsSuccessStatusCode);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Cloud("first.bin")));
        using var retried = await Send("PUT", "second.bin", new ByteArrayContent(bytes));
        Assert.True(retried.IsSuccessStatusCode);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Cloud("second.bin")));
        Assert.Empty(Directory.GetFiles(staging, "*.upload"));
    }

    private sealed class GatedUpload(byte[] bytes) : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
            await stream.FlushAsync();
            Started.TrySetResult();
            await Release.Task;
            await stream.WriteAsync(bytes.AsMemory(bytes.Length / 2));
        }
    }

    [Fact]
    public async Task Upload_respects_disk_reserve_and_retries_without_losing_the_previous_file()
    {
        long free = 1024;
        await StartBridge(_api, _ => free);
        _bridge.StagingFreeSpaceReserveBytes = 64;
        using var created = await Send("PUT", "safe.bin", new ByteArrayContent([1, 2, 3]));
        Assert.True(created.IsSuccessStatusCode);
        free = 65;
        // More than four refusals also verify that staging slots are released on early return.
        for (int i = 0; i < 6; i++)
        {
            using var blocked = await Send("PUT", "safe.bin", new ByteArrayContent([4, 5, 6]));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Cloud("safe.bin")));
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "staging"), "*.upload"));
        }
        free = 1024;
        using var retried = await Send("PUT", "safe.bin", new ByteArrayContent([4, 5, 6]));
        Assert.True(retried.IsSuccessStatusCode);
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Cloud("safe.bin")));
    }

    [Fact]
    public async Task Space_lost_during_staging_discards_only_the_temporary_copy()
    {
        int checks = 0;
        await StartBridge(_api, _ => Interlocked.Increment(ref checks) == 1 ? 1024 : 0);
        _bridge.StagingFreeSpaceReserveBytes = 64;
        using var blocked = await Send("PUT", "uncommitted.bin", new ByteArrayContent([7, 8, 9]));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.False(File.Exists(Cloud("uncommitted.bin")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "staging"), "*.upload"));
    }

    [Fact]
    public async Task Listing_started_before_an_upload_cannot_hide_the_acknowledged_file()
    {
        var transport = new FaultyTransport(_api);
        await StartBridge(transport);
        using var mkdir = await Send("MKCOL", "Games");
        transport.DelayedListDirectory = _api.Abs("Games");
        var oldListing = Send("PROPFIND", "Games", edit: r => r.Headers.Add("Depth", "1"));
        await transport.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var upload = await Send("PUT", "Games/new.bin", new ByteArrayContent([7, 8, 9]));
            Assert.True(upload.IsSuccessStatusCode);
        }
        finally { transport.ReleaseList.TrySetResult(); }
        using var staleResponse = await oldListing;
        using var read = await Send("GET", "Games/new.bin");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(new byte[] { 7, 8, 9 }, await read.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Protected_cleanup_archives_bytes_and_does_not_break_store_updates()
    {
        using var mkdir = await Send("MKCOL", "download");
        using var put = await Send("PUT", "download/asset.bin", new ByteArrayContent([1, 2, 3, 4]));
        Assert.True(put.IsSuccessStatusCode);
        var protection = Path.Combine(_root, "protected.txt");
        await File.WriteAllTextAsync(protection, "download/asset.bin");
        _bridge.DeleteProtectionFile = protection;
        // Deleting an ancestor must not bypass protection.
        using var blocked = await Send("DELETE", "download");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        _bridge.ProtectedDeleteRecoveryRoot = ".cloudssd-recovery";
        _bridge.ProtectedDeleteJournalDirectory = Path.Combine(_root, "archive-journal");
        using var removed = await Send("DELETE", "download/asset.bin");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var saved = Assert.Single(Directory.GetFiles(Cloud(".cloudssd-recovery"), "asset.bin", SearchOption.AllDirectories));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(saved));
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_bridge.ProtectedDeleteJournalDirectory, "*.json"))));
        Assert.Equal("download/asset.bin", journal.RootElement.GetProperty("Original").GetString());
        Assert.Equal("archived", journal.RootElement.GetProperty("State").GetString());
        Assert.Equal(4, journal.RootElement.GetProperty("Size").GetInt64());
        using var missing = await Send("GET", "download/asset.bin");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var replace = await Send("PUT", "download/asset.bin", new ByteArrayContent([5, 6, 7, 8]));
        Assert.True(replace.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Lost_archive_move_reply_still_has_a_durable_recovery_mapping()
    {
        await Send("PUT", "asset.bin", new ByteArrayContent([7, 8, 9]));
        await StartBridge(new FaultyTransport(_api) { Mode = "move-reply-lost" });
        var protection = Path.Combine(_root, "protected.txt"); await File.WriteAllTextAsync(protection, "asset.bin");
        _bridge.DeleteProtectionFile = protection; _bridge.ProtectedDeleteRecoveryRoot = ".cloudssd-recovery";
        _bridge.ProtectedDeleteJournalDirectory = Path.Combine(_root, "archive-journal");
        using var response = await Send("DELETE", "asset.bin");
        Assert.False(response.IsSuccessStatusCode);
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_bridge.ProtectedDeleteJournalDirectory, "*.json"))));
        Assert.Equal("prepared", journal.RootElement.GetProperty("State").GetString());
        Assert.Equal("asset.bin", journal.RootElement.GetProperty("StoredOriginal").GetString());
        var recovery = journal.RootElement.GetProperty("Recovery").GetString()!;
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(Cloud(recovery)));
        Assert.False(File.Exists(Cloud("asset.bin")));
    }

    [Fact]
    public void Md5_unscrambling_inverts_the_websites_scrambling_and_page_token_is_found()
    {
        for (int i = 0; i < 200; i++)
        {
            var md5 = Convert.ToHexString(MD5.HashData(BitConverter.GetBytes(i))).ToLowerInvariant();
            var scrambled = TeraBoxMockServer.ScrambleMd5(md5);
            Assert.NotEqual(md5, scrambled);
            Assert.Equal(md5, TeraBoxWebApi.DecodeMd5(scrambled));
            Assert.Equal(scrambled, TeraBoxWebApi.EncodeMd5(md5));
        }
        Assert.Equal("abc123", TeraBoxWebApi.ExtractJsToken("x `function%20fn%28a%29%7Bwindow.jsToken%20%3D%20a%7D%3Bfn%28%22abc123%22%29` y"));
        Assert.Null(TeraBoxWebApi.ExtractJsToken("<html>Please log in</html>"));
        Assert.Equal(4, TeraBoxWebApi.ShardSizeFor(10L << 30, premium: false) >> 20);
        Assert.Equal(16, TeraBoxWebApi.ShardSizeFor(10L << 30, premium: true) >> 20);
        Assert.DoesNotContain(_mock.WebSessionCookie, new TeraBoxWebSession(_mock.BaseUrl, _mock.WebCookieHeader, "ua", null, DateTimeOffset.UtcNow).ToString());
    }

    [Fact]
    public async Task Web_upload_multi_shard_then_full_and_range_reads_through_redirecting_links()
    {
        var data = Rnd(10 * 1024 * 1024 + 4321, 11);
        Assert.Equal(HttpStatusCode.Created, (await Send("MKCOL", "Games/")).StatusCode);
        var put = await Send("PUT", "Games/data.pak", new ByteArrayContent(data));
        Assert.True(put.StatusCode == HttpStatusCode.Created, await put.Content.ReadAsStringAsync());
        Assert.Equal(data, File.ReadAllBytes(Cloud("Games/data.pak")));
        Assert.True(_mock.WebJsTokenRejections >= 1, "the first changing call had no page token and must have fetched it");
        Assert.NotNull(_store.Load()!.JsToken);

        Assert.Equal(data, await (await Send("GET", "Games/data.pak")).Content.ReadAsByteArrayAsync());
        foreach (var (off, len) in new[] { (0L, 1), (4L * 1024 * 1024 - 3, 7), (data.Length - 9L, 9), (6_000_000L, 2_000_000) })
        {
            var r = await Send("GET", "Games/data.pak", edit: q => q.Headers.Range = new RangeHeaderValue(off, off + len - 1));
            Assert.Equal(HttpStatusCode.PartialContent, r.StatusCode);
            Assert.Equal(data.AsSpan((int)off, len).ToArray(), await r.Content.ReadAsByteArrayAsync());
        }
        Assert.True(_bridge.UpstreamRangeSupported);
        Assert.True(_mock.RangeRequests >= 4);
    }

    [Fact]
    public async Task Files_of_zeros_are_stored_as_markers_and_read_back_as_zeros()
    {
        const int size = 5 * 1024 * 1024 + 7;
        var marker = "Games/z.bin" + TeraBoxWebDavBridge.ZeroMarkerTag + size;
        void Dest(HttpRequestMessage q, string d) { q.Headers.Add("Destination", _bridge.Url + d); q.Headers.Add("Overwrite", "T"); }
        async Task<string> Listing(string dir) => await (await Send("PROPFIND", dir, edit: q => q.Headers.Add("Depth", "1"))).Content.ReadAsStringAsync();

        await Send("MKCOL", "Games/");
        // A space reservation (all zeros) uploads only a tiny marker...
        Assert.Equal(HttpStatusCode.Created, (await Send("PUT", "Games/z.bin", new ByteArrayContent(new byte[size]))).StatusCode);
        Assert.True(File.Exists(Cloud(marker)));
        Assert.False(File.Exists(Cloud("Games/z.bin")));
        Assert.True(new FileInfo(Cloud(marker)).Length < 200);
        // ...but the client sees the real file and reads exact zeros, whole and by range.
        var list = await Listing("Games/");
        Assert.Contains("z.bin", list);
        Assert.Contains($"<d:getcontentlength>{size}</d:getcontentlength>", list);
        Assert.DoesNotContain("cloudssdzero", list);
        var head = await Send("HEAD", "Games/z.bin");
        Assert.Equal(size, head.Content.Headers.ContentLength);
        Assert.Equal(new byte[size], await (await Send("GET", "Games/z.bin")).Content.ReadAsByteArrayAsync());
        var part = await Send("GET", "Games/z.bin", edit: q => q.Headers.Range = new RangeHeaderValue(size - 10, size - 1));
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(new byte[10], await part.Content.ReadAsByteArrayAsync());

        // Real bytes replace the marker.
        var data = Rnd(size, 3);
        Assert.Equal(HttpStatusCode.NoContent, (await Send("PUT", "Games/z.bin", new ByteArrayContent(data))).StatusCode);
        Assert.Equal(data, File.ReadAllBytes(Cloud("Games/z.bin")));
        Assert.False(File.Exists(Cloud(marker)));
        Assert.Equal(data, await (await Send("GET", "Games/z.bin")).Content.ReadAsByteArrayAsync());

        // Zeros again (a different size) replace the real file; move, copy and delete follow the marker.
        Assert.Equal(HttpStatusCode.NoContent, (await Send("PUT", "Games/z.bin", new ByteArrayContent(new byte[size + 1]))).StatusCode);
        Assert.False(File.Exists(Cloud("Games/z.bin")));
        await Send("MKCOL", "Other/");
        Assert.Equal(HttpStatusCode.Created, (await Send("MOVE", "Games/z.bin", edit: q => Dest(q, "Other/moved.bin"))).StatusCode);
        Assert.True(File.Exists(Cloud("Other/moved.bin" + TeraBoxWebDavBridge.ZeroMarkerTag + (size + 1))));
        Assert.Equal(HttpStatusCode.Created, (await Send("COPY", "Other/moved.bin", edit: q => Dest(q, "Other/copy.bin"))).StatusCode);
        Assert.Equal(new byte[size + 1], await (await Send("GET", "Other/copy.bin")).Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await Send("DELETE", "Other/moved.bin")).StatusCode);
        Assert.Empty(Directory.GetFiles(Cloud("Other"), "moved.bin*"));
        Assert.Equal(HttpStatusCode.NotFound, (await Send("GET", "Other/moved.bin")).StatusCode);

        // Small zero files are ordinary uploads.
        Assert.Equal(HttpStatusCode.Created, (await Send("PUT", "Games/small.bin", new ByteArrayContent(new byte[1000]))).StatusCode);
        Assert.Equal(1000, new FileInfo(Cloud("Games/small.bin")).Length);
    }

    [Fact]
    public async Task Empty_Steam_staging_files_survive_upload_move_and_readback()
    {
        Assert.Equal(HttpStatusCode.Created, (await Send("PUT", "state.tmp", new ByteArrayContent([]))).StatusCode);
        Assert.True(File.Exists(Cloud("state.tmp" + TeraBoxWebDavBridge.ZeroMarkerTag + "0")));
        Assert.Equal(0, (await Send("HEAD", "state.tmp")).Content.Headers.ContentLength);
        Assert.Empty(await (await Send("GET", "state.tmp")).Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Created, (await Send("MOVE", "state.tmp", edit: q => q.Headers.Add("Destination", _bridge.Url + "state.dat"))).StatusCode);
        Assert.Equal(0, (await Send("HEAD", "state.dat")).Content.Headers.ContentLength);
        Assert.Equal(HttpStatusCode.NotFound, (await Send("HEAD", "state.tmp")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send("DELETE", "state.dat")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send("HEAD", "state.dat")).StatusCode);
    }

    [Fact]
    public async Task Busy_file_manager_is_retried_and_operations_run_one_at_a_time()
    {
        TeraBoxWebApi.FileManagerBusyDelay = TimeSpan.FromMilliseconds(20);
        void Dest(HttpRequestMessage q, string d) { q.Headers.Add("Destination", _bridge.Url + d); q.Headers.Add("Overwrite", "T"); }
        for (int i = 0; i < 4; i++) await Send("PUT", $"r{i}.txt", new StringContent("x" + i));
        _mock.WebFileManagerBusy = 3;
        var moves = Enumerable.Range(0, 4).Select(i => Send("MOVE", $"r{i}.txt", edit: q => Dest(q, $"renamed{i}.txt"))).ToList();
        foreach (var m in moves) Assert.Equal(HttpStatusCode.Created, (await m).StatusCode);
        Assert.Equal(3, _mock.WebFileManagerBusyRefusals);
        for (int i = 0; i < 4; i++) Assert.Equal("x" + i, File.ReadAllText(Cloud($"renamed{i}.txt")));
    }

    [Fact]
    public async Task A_retried_part_cancels_the_abandoned_upload_of_the_same_part()
    {
        Assert.Equal("a/x.rpf.rclone_chunk.001", TeraBoxWebDavBridge.UploadKey("a/x.rpf.rclone_chunk.001_6zjkj9"));
        Assert.Equal("a/x.rpf.rclone_chunk.001", TeraBoxWebDavBridge.UploadKey("a/x.rpf.rclone_chunk.001"));
        Assert.Equal("a/plain_file.bin", TeraBoxWebDavBridge.UploadKey("a/plain_file.bin"));

        _mock.WebPartDelay = TimeSpan.FromMilliseconds(400);
        var first = Rnd(6 * 1024 * 1024, 1);
        var second = Rnd(6 * 1024 * 1024, 2);
        var old = Send("PUT", "big.rpf.rclone_chunk.001_aaaa1", new ByteArrayContent(first));
        await Task.Delay(300); // the first attempt is uploading shards
        var retry = await Send("PUT", "big.rpf.rclone_chunk.001_bbbb2", new ByteArrayContent(second));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.NotEqual(HttpStatusCode.Created, (await old).StatusCode);
        Assert.Equal(second, File.ReadAllBytes(Cloud("big.rpf.rclone_chunk.001_bbbb2")));
        Assert.False(File.Exists(Cloud("big.rpf.rclone_chunk.001_aaaa1")));
    }

    [Fact]
    public async Task Same_content_retries_keep_both_acknowledged_transaction_names_valid()
    {
        _mock.WebPartDelay = TimeSpan.FromMilliseconds(400);
        var data = Rnd(6 * 1024 * 1024, 5);
        var first = Send("PUT", "same.rpf.rclone_chunk.001_aaaa1", new ByteArrayContent(data));
        await Task.Delay(300);
        var shardsBefore = _mock.WebMaxPartsInFlight;
        var retry = await Send("PUT", "same.rpf.rclone_chunk.001_bbbb2", new ByteArrayContent(data));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await first).StatusCode);
        // Uploaded once. Both successful callers can still finalize the transaction they were promised.
        Assert.Equal(data, File.ReadAllBytes(Cloud("same.rpf.rclone_chunk.001_bbbb2")));
        Assert.Equal(data, File.ReadAllBytes(Cloud("same.rpf.rclone_chunk.001_aaaa1")));
        void Dest(HttpRequestMessage q) { q.Headers.Add("Destination", _bridge.Url + "same.rpf.rclone_chunk.001"); q.Headers.Add("Overwrite", "T"); }
        var moves = new[] { Send("MOVE", "same.rpf.rclone_chunk.001_aaaa1", edit: Dest), Send("MOVE", "same.rpf.rclone_chunk.001_bbbb2", edit: Dest) };
        foreach (var move in moves) Assert.True((await move).IsSuccessStatusCode);
        Assert.Equal(data, File.ReadAllBytes(Cloud("same.rpf.rclone_chunk.001")));
        var status = await (await Send("GET", ".cloudssd/status")).Content.ReadAsStringAsync();
        Assert.Contains("\"joinedUploads\":1", status);
        Assert.Contains("\"uploads\":1", status);
    }

    [Fact]
    public async Task A_retry_reuses_completed_content_even_after_the_first_transaction_was_finalized()
    {
        var data = Rnd(5 * 1024 * 1024, 8);
        Assert.Equal(HttpStatusCode.Created, (await Send("PUT", "late.rpf.rclone_chunk.001_aaaa1", new ByteArrayContent(data))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send("MOVE", "late.rpf.rclone_chunk.001_aaaa1", edit: q => q.Headers.Add("Destination", _bridge.Url + "late.rpf.rclone_chunk.001"))).StatusCode);
        // rclone had given up on that request and sends the same part again under a new transaction name.
        Assert.Equal(HttpStatusCode.Created, (await Send("PUT", "late.rpf.rclone_chunk.001_cccc3", new ByteArrayContent(data))).StatusCode);
        Assert.Equal(data, File.ReadAllBytes(Cloud("late.rpf.rclone_chunk.001_cccc3")));
        Assert.False(File.Exists(Cloud("late.rpf.rclone_chunk.001_aaaa1")));
        Assert.Equal(data, File.ReadAllBytes(Cloud("late.rpf.rclone_chunk.001")));
        var status = await (await Send("GET", ".cloudssd/status")).Content.ReadAsStringAsync();
        Assert.Contains("\"uploads\":1", status);
        Assert.Contains("\"reusedUploads\":1", status);
        // Different bytes are a new version: uploaded normally.
        var changed = Rnd(5 * 1024 * 1024, 9);
        Assert.Equal(HttpStatusCode.Created, (await Send("PUT", "late.rpf.rclone_chunk.001_dddd4", new ByteArrayContent(changed))).StatusCode);
        Assert.Equal(changed, File.ReadAllBytes(Cloud("late.rpf.rclone_chunk.001_dddd4")));
    }

    [Fact]
    public void Marker_names_parse_only_when_well_formed()
    {
        Assert.True(TeraBoxWebDavBridge.TryParseMarker("a.rpf.rclone_chunk.001_x.cloudssdzero-1073741824", out var n, out var s));
        Assert.Equal(("a.rpf.rclone_chunk.001_x", 1073741824L), (n, s));
        Assert.False(TeraBoxWebDavBridge.TryParseMarker(".cloudssdzero-5", out _, out _));
        Assert.False(TeraBoxWebDavBridge.TryParseMarker("a.cloudssdzero-", out _, out _));
        Assert.False(TeraBoxWebDavBridge.TryParseMarker("a.cloudssdzero-12x", out _, out _));
        Assert.False(TeraBoxWebDavBridge.TryParseMarker("a.cloudssdzero--1", out _, out _));
    }

    [Fact]
    public async Task Shards_of_one_file_upload_in_parallel_and_assemble_exactly()
    {
        _mock.WebPartDelay = TimeSpan.FromMilliseconds(150);
        var data = Rnd(5 * 4 * 1024 * 1024 + 99, 21); // 6 shards of 4 MiB
        Assert.Equal(HttpStatusCode.Created, (await Send("PUT", "par.bin", new ByteArrayContent(data))).StatusCode);
        Assert.Equal(data, File.ReadAllBytes(Cloud("par.bin")));
        Assert.True(_mock.WebMaxPartsInFlight >= 2, $"shards were sent one at a time (max in flight {_mock.WebMaxPartsInFlight})");
        Assert.True(_mock.WebMaxPartsInFlight <= TeraBoxWebApi.UploadParallelShards);
    }

    [Fact]
    public async Task Stale_page_token_is_refreshed_and_signed_download_fallback_works()
    {
        var data = Rnd(300_000, 5);
        Assert.Equal(HttpStatusCode.Created, (await Send("PUT", "a.bin", new ByteArrayContent(data))).StatusCode);
        var rejections = _mock.WebJsTokenRejections;
        _mock.RotateJsToken();
        Assert.Equal(HttpStatusCode.Created, (await Send("PUT", "b.bin", new ByteArrayContent(data))).StatusCode);
        Assert.Equal(rejections + 1, _mock.WebJsTokenRejections);

        _mock.WebNoMetaDlink = true; // no dlink in filemetas: use /api/home/info + signed /api/download
        var r = await Send("GET", "b.bin", edit: q => q.Headers.Range = new RangeHeaderValue(1000, 1999));
        Assert.Equal(data.AsSpan(1000, 1000).ToArray(), await r.Content.ReadAsByteArrayAsync());
        Assert.True(_mock.WebSignedDownloads > 0);
    }

    [Fact]
    public async Task Rename_move_copy_with_async_task_and_delete()
    {
        await Send("MKCOL", "a/");
        await Send("MKCOL", "b/");
        await Send("PUT", "a/x.txt", new StringContent("one"));
        void Dest(HttpRequestMessage q, string d) { q.Headers.Add("Destination", _bridge.Url + d); q.Headers.Add("Overwrite", "T"); }
        Assert.Equal(HttpStatusCode.Created, (await Send("MOVE", "a/x.txt", edit: q => Dest(q, "a/renamed.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send("COPY", "a/renamed.txt", edit: q => Dest(q, "b/copy.txt"))).StatusCode); // copy runs as an async task
        Assert.Equal(HttpStatusCode.Created, (await Send("MOVE", "a/renamed.txt", edit: q => Dest(q, "b/moved.txt"))).StatusCode);
        Assert.Equal("one", File.ReadAllText(Cloud("b/copy.txt")));
        Assert.Equal("one", File.ReadAllText(Cloud("b/moved.txt")));
        Assert.False(File.Exists(Cloud("a/renamed.txt")));
        Assert.Equal(HttpStatusCode.NoContent, (await Send("DELETE", "b/")).StatusCode);
        Assert.False(Directory.Exists(Cloud("b")));
    }

    [Fact]
    public async Task Refusals_and_expired_session_never_fake_success()
    {
        var empty = await Send("PUT", "empty.txt", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.Created, empty.StatusCode);
        Assert.True(File.Exists(Cloud("empty.txt" + TeraBoxWebDavBridge.ZeroMarkerTag + "0")));

        _mock.MaxFileSize = 1024 * 1024; // plan limit enforced by TeraBox at precreate
        var big = await Send("PUT", "big.bin", new ByteArrayContent(Rnd(2 * 1024 * 1024, 2)));
        Assert.Equal((HttpStatusCode)413, big.StatusCode);
        Assert.False(File.Exists(Cloud("big.bin")));
        _mock.MaxFileSize = null;

        await Send("PUT", "ok.bin", new ByteArrayContent(Rnd(1000, 3)));
        _mock.WebSessionExpired = true;
        await Task.Delay(2100); // bridge listing cache
        var r = await Send("GET", "ok.bin");
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Sign in", await r.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------ bridge validation of upstream responses

    private sealed class FaultyTransport(ITeraBoxTransport inner) : ITeraBoxTransport
    {
        public string Mode = "";
        public int Opens;
        public string? DelayedListDirectory;
        public TaskCompletionSource ListStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseList = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _listDelayed;
        public string Kind => inner.Kind;
        public string AppRoot => inner.AppRoot;
        public string Abs(string rel) => inner.Abs(rel);
        public long Calls => inner.Calls;
        public long Refreshes => inner.Refreshes;
        public long Throttled => inner.Throttled;
        public long? MaxFileSize => inner.MaxFileSize;
        public Task<TeraBoxUser> UserAsync(CancellationToken ct) => inner.UserAsync(ct);
        public Task<TeraBoxQuota> QuotaAsync(CancellationToken ct) => inner.QuotaAsync(ct);
        public async Task<IReadOnlyList<TeraBoxEntry>> ListAsync(string absDir, CancellationToken ct)
        {
            var entries = await inner.ListAsync(absDir, ct);
            if (absDir == DelayedListDirectory && Interlocked.Exchange(ref _listDelayed, 1) == 0)
            {
                ListStarted.TrySetResult();
                await ReleaseList.Task.WaitAsync(ct);
            }
            return entries;
        }
        public Task<TeraBoxEntry?> StatAsync(string absPath, CancellationToken ct) => inner.StatAsync(absPath, ct);
        public Task<string> DownloadUrlAsync(TeraBoxEntry file, CancellationToken ct) => inner.DownloadUrlAsync(file, ct);
        public Task MkdirAsync(string absPath, CancellationToken ct) => inner.MkdirAsync(absPath, ct);
        public Task DeleteAsync(string absPath, CancellationToken ct) => inner.DeleteAsync(absPath, ct);
        public async Task MoveAsync(string a, string b, string c, CancellationToken ct)
        {
            await inner.MoveAsync(a, b, c, ct);
            if (Mode == "move-reply-lost") throw new IOException("Injected lost reply after move");
        }
        public Task RenameAsync(string a, string b, CancellationToken ct) => inner.RenameAsync(a, b, ct);
        public Task CopyAsync(string a, string b, string c, CancellationToken ct) => inner.CopyAsync(a, b, c, ct);
        public Task<TeraBoxEntry> UploadAsync(string l, string a, bool o, IProgress<long>? p, CancellationToken ct) => inner.UploadAsync(l, a, o, p, ct);

        public async Task<HttpResponseMessage> OpenDownloadAsync(string url, long? start, long? end, CancellationToken ct)
        {
            Opens++;
            var real = await inner.OpenDownloadAsync(url, start, end, ct);
            switch (Mode)
            {
                case "wrong-start": // 206 for a different offset than requested
                {
                    real.Dispose();
                    var shifted = await inner.OpenDownloadAsync(url, start + 1, end, ct);
                    return shifted;
                }
                case "no-content-range":
                {
                    var body = await real.Content.ReadAsByteArrayAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
                }
                case "changed-size":
                {
                    var body = await real.Content.ReadAsByteArrayAsync(ct);
                    var m = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
                    m.Content.Headers.ContentRange = new ContentRangeHeaderValue(start!.Value, end!.Value, 999_999_999);
                    return m;
                }
                case "truncate-once" when Opens == 1:
                {
                    // Headers promise the whole range, the body stops after a third (a dropped connection).
                    var body = await real.Content.ReadAsByteArrayAsync(ct);
                    var m = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(new MemoryStream(body[..(body.Length / 3)])) };
                    m.Content.Headers.ContentRange = real.Content.Headers.ContentRange;
                    m.Content.Headers.ContentLength = body.Length;
                    return m;
                }
                case "trickle-always":
                case "trickle-once" when Opens == 1:
                {
                    // Headers promise the whole range; after 64 KiB the body crawls at ~2 KB/s (a throttled connection).
                    var body = await real.Content.ReadAsByteArrayAsync(ct);
                    var m = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(new TrickleStream(body, 64 * 1024)) };
                    m.Content.Headers.ContentRange = real.Content.Headers.ContentRange;
                    m.Content.Headers.ContentLength = body.Length;
                    return m;
                }
                case "503-once" when Opens == 1:
                    real.Dispose();
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                default:
                    return real;
            }
        }
    }

    private sealed class TrickleStream(byte[] data, int fastBytes) : Stream
    {
        private int _pos;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= data.Length) return 0;
            if (_pos >= fastBytes) await Task.Delay(500, ct);
            var n = Math.Min(buffer.Length, _pos >= fastBytes ? 1024 : fastBytes - _pos);
            n = Math.Min(n, data.Length - _pos);
            data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Bridge_replaces_a_throttled_connection_and_still_returns_exact_bytes()
    {
        var oldWindow = TeraBoxWebDavBridge.SlowWindow;
        TeraBoxWebDavBridge.SlowWindow = TimeSpan.FromSeconds(2);
        try { await Bridge_validates_upstream_ranges_and_resumes_only_with_correct_bytes("trickle-once", true); }
        finally { TeraBoxWebDavBridge.SlowWindow = oldWindow; }
        Assert.Contains("\"slowConnections\":1", System.Text.Json.JsonSerializer.Serialize(_bridge.Status()));
    }

    [Theory]
    [InlineData("wrong-start", false)]
    [InlineData("no-content-range", false)]
    [InlineData("changed-size", false)]
    [InlineData("truncate-once", true)]
    [InlineData("503-once", true)]
    public async Task Bridge_validates_upstream_ranges_and_resumes_only_with_correct_bytes(string mode, bool expectCorrectData)
    {
        var data = Rnd(3 * 1024 * 1024, 21);
        Assert.Equal(HttpStatusCode.Created, (await Send("PUT", "v.bin", new ByteArrayContent(data))).StatusCode);
        var faulty = new FaultyTransport(_api);
        await StartBridge(faulty);
        faulty.Mode = mode;
        const int off = 1_234_567, len = 600_000;
        byte[]? got = null;
        HttpStatusCode? status = null;
        try
        {
            var r = await Send("GET", "v.bin", edit: q => q.Headers.Range = new RangeHeaderValue(off, off + len - 1));
            status = r.StatusCode;
            got = await r.Content.ReadAsByteArrayAsync();
        }
        catch (HttpRequestException) { /* connection cut after headers: the client sees an error, never wrong bytes */ }
        var expected = data.AsSpan(off, len).ToArray();
        if (expectCorrectData)
        {
            Assert.Equal(HttpStatusCode.PartialContent, status);
            Assert.Equal(expected, got);
        }
        else Assert.Equal(HttpStatusCode.BadGateway, status); // refused before any byte was sent
        var st = System.Text.Json.JsonSerializer.Serialize(_bridge.Status());
        if (mode == "truncate-once") Assert.Contains("\"resumes\":1", st);
        if (mode is "wrong-start" or "no-content-range") Assert.Contains("\"rangeMismatches\":1", st);
        if (mode == "changed-size") Assert.Contains("\"objectChanged\":1", st);
    }

    [Fact]
    public async Task Persistent_trickle_opens_circuit_and_metadata_and_local_sources_remain_available()
    {
        var data = Rnd(600_000, 22);
        await Send("MKCOL", "Games/");
        await Send("PUT", "Games/slow.bin", new ByteArrayContent(data));
        var healthy = Rnd(4096, 23);
        await Send("PUT", "Games/healthy.bin", new ByteArrayContent(healthy));
        var faulty = new FaultyTransport(_api) { Mode = "trickle-always" };
        await StartBridge(faulty);
        var oldWindow = TeraBoxWebDavBridge.SlowWindow;
        TeraBoxWebDavBridge.SlowWindow = TimeSpan.FromSeconds(2);
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<HttpRequestException>(() => Send("GET", "Games/slow.bin",
                edit: q => q.Headers.Range = new RangeHeaderValue(0, data.Length - 1)));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15));
            var opens = faulty.Opens;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send("GET", "Games/slow.bin")).StatusCode);
            Assert.Equal(opens, faulty.Opens);
            Assert.Equal(HttpStatusCode.OK, (await Send("HEAD", "Games/slow.bin")).StatusCode);
            Assert.Contains("\"providerThrottled\":true", await (await Send("GET", ".cloudssd/status")).Content.ReadAsStringAsync());
            // One stalled object must not put unrelated healthy cloud reads into cooldown.
            faulty.Mode = "";
            using var healthyResponse = await Send("GET", "Games/healthy.bin");
            Assert.Equal(HttpStatusCode.OK, healthyResponse.StatusCode);
            Assert.Equal(healthy, await healthyResponse.Content.ReadAsByteArrayAsync());
            faulty.Mode = "trickle-always";
            opens = faulty.Opens;
            var local = Path.Combine(_root, "local");
            Directory.CreateDirectory(local);
            await File.WriteAllBytesAsync(Path.Combine(local, "slow.bin"), data);
            _bridge.LocalSources = new VerifiedLocalSources([new("Games", local)]);
            var response = await Send("GET", "Games/slow.bin", edit: q => q.Headers.Range = new RangeHeaderValue(123, 456));
            Assert.Equal(data[123..457], await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(opens, faulty.Opens);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send("GET", "Games/slow.bin",
                edit: q => q.Headers.Add("X-CloudSSD-Cloud-Only", "1"))).StatusCode);
        }
        finally { TeraBoxWebDavBridge.SlowWindow = oldWindow; }
    }

    [Fact]
    public async Task Read_deadline_cuts_off_started_response_and_cooldown_recovers()
    {
        var data = Rnd(600_000, 23);
        await Send("PUT", "deadline.bin", new ByteArrayContent(data));
        var faulty = new FaultyTransport(_api) { Mode = "trickle-always" };
        await StartBridge(faulty);
        _bridge.ReadProgressTimeout = TimeSpan.FromMilliseconds(250);
        _bridge.ThrottleCooldown = TimeSpan.FromMilliseconds(100);
        await Assert.ThrowsAsync<HttpRequestException>(() => Send("GET", "deadline.bin",
            edit: q => q.Headers.Range = new RangeHeaderValue(0, data.Length - 1)));
        Assert.Contains("\"providerThrottled\":true", System.Text.Json.JsonSerializer.Serialize(_bridge.Status()));
        await Task.Delay(150);
        faulty.Mode = "";
        _bridge.ReadProgressTimeout = TimeSpan.FromSeconds(10);
        Assert.Equal(data, await (await Send("GET", "deadline.bin")).Content.ReadAsByteArrayAsync());
        Assert.Contains("\"providerThrottled\":false", System.Text.Json.JsonSerializer.Serialize(_bridge.Status()));
    }
}
