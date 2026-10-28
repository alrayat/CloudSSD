using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace CloudSSD.TeraBox;

public sealed record TeraBoxEntry(long FsId, string Path, string Name, long Size, bool IsDir, DateTimeOffset Modified, string? Md5);

public sealed record TeraBoxQuota(long Total, long Used);

public sealed record TeraBoxUser(string Name, int VipType)
{
    public string Plan => VipType switch { 0 => "Free", 1 => "Premium", 2 => "Super Premium", _ => $"type {VipType}" };
}

/// <summary>
/// Client for the documented TeraBox Open Platform file APIs. Owns the access token: refreshes it before expiry and on
/// auth errors (one refresh at a time, because each refresh_token works only once), resolves the per-user API and upload
/// domains through /oauth/tokeninfo (cached for at most 1 hour, as the document recommends), and backs off on
/// frequency-control errors.
/// </summary>
public sealed class TeraBoxApi : ITeraBoxTransport
{
    /// <summary>Shard size for uploads. The document requires every shard except the last to be larger than 4 MB.</summary>
    public const int ShardSize = 4 * 1024 * 1024;

    private readonly TeraBoxClientFile _client;
    private readonly TeraBoxTokenStore _store;
    private readonly HttpClient _http;
    private readonly TeraBoxAuth _auth;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TeraBoxTokens? _tokens;
    private (string Api, string Upload, DateTime Fetched)? _domains;
    private long _calls, _refreshes, _throttled;

    public TeraBoxApi(TeraBoxClientFile client, TeraBoxTokenStore store, HttpClient http, ILogger log)
    {
        _client = client;
        _store = store;
        _http = http;
        _log = log;
        _auth = new TeraBoxAuth(client, http);
    }

    public TeraBoxClientFile Client => _client;
    public string Kind => "official";
    public string AppRoot => _client.AppRoot;

    /// <summary>Set from the account's plan once known (the Open Platform reports vip_type, not the limit itself).</summary>
    public long? MaxFileSize { get; set; }
    public long Calls => Interlocked.Read(ref _calls);
    public long Refreshes => Interlocked.Read(ref _refreshes);
    public long Throttled => Interlocked.Read(ref _throttled);

    // ------------------------------------------------------------------ token handling

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        _tokens ??= _store.Load() ?? throw new TeraBoxException(200002, "Not connected to TeraBox. Connect the account in Cloud Drives.");
        if (_tokens.AccessExpires - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5)) await RefreshAsync(_tokens.AccessToken, ct);
        return _tokens!.AccessToken;
    }

    /// <summary>Refreshes unless another caller already replaced <paramref name="failedToken"/>.</summary>
    private async Task RefreshAsync(string failedToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var current = _store.Load() ?? _tokens;
            if (current is null) throw new TeraBoxException(200002, "Not connected to TeraBox.");
            if (current.AccessToken != failedToken && current.AccessExpires - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(5))
            {
                _tokens = current; // someone else (another process) refreshed already
                return;
            }
            if (current.RefreshExpires < DateTimeOffset.UtcNow)
                throw new TeraBoxException(200005, "The TeraBox login expired (refresh tokens last 30 days). Reconnect TeraBox in Cloud Drives.");
            var fresh = await _auth.RefreshAsync(current, ct);
            _store.Save(fresh); // persist first: the old refresh token is now spent
            _tokens = fresh;
            _domains = null;
            Interlocked.Increment(ref _refreshes);
            _log.LogInformation("TeraBox access token refreshed (valid until {Until:u})", fresh.AccessExpires);
        }
        finally { _refreshGate.Release(); }
    }

    private async Task<(string Api, string Upload)> DomainsAsync(CancellationToken ct)
    {
        if (_domains is { } d && DateTime.UtcNow - d.Fetched < TimeSpan.FromHours(1)) return (d.Api, d.Upload);
        var info = await _auth.TokenInfoAsync(await TokenAsync(ct), ct);
        _domains = (info.ApiDomain, info.UploadDomain, DateTime.UtcNow);
        return (info.ApiDomain, info.UploadDomain);
    }

    // ------------------------------------------------------------------ transport

    /// <param name="tolerate">Top-level errnos returned to the caller instead of thrown (e.g. 12 = batch with per-item errors).</param>
    internal static async Task<JsonObject> ReadAsync(HttpResponseMessage resp, CancellationToken ct, IReadOnlyCollection<int>? tolerate = null)
    {
        using var _ = resp;
        // Decode as UTF-8 ourselves: measured on the real account (2026-10-19), a TeraBox upload endpoint labels its JSON
        // with a charset .NET rejects ("The character set provided in ContentType is invalid").
        var text = System.Text.Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync(ct));
        JsonObject? o;
        try { o = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { throw new HttpRequestException($"TeraBox returned HTTP {(int)resp.StatusCode} with a non-JSON body", null, resp.StatusCode); }
        if (o is null) throw new HttpRequestException($"TeraBox returned HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
        var errno = o["errno"]?.GetValue<int>() ?? 0;
        if (errno != 0 && tolerate?.Contains(errno) != true)
        {
            var msg = o["show_msg"]?.GetValue<string>() is { Length: > 0 } m ? m : o["errmsg"]?.GetValue<string>() ?? "";
            throw new TeraBoxException(errno, $"TeraBox error {errno}{(msg.Length > 0 ? ": " + msg : "")}");
        }
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"TeraBox returned HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
        return o;
    }

    private async Task<JsonObject> CallAsync(bool upload, string path, Dictionary<string, string>? query, Func<HttpContent?>? body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(ct);
            var (api, up) = await DomainsAsync(ct);
            var q = new Dictionary<string, string>(query ?? new()) { ["access_tokens"] = token };
            var url = $"{_client.Scheme}://{(upload ? up : api)}{path}?{string.Join('&', q.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)))}";
            Interlocked.Increment(ref _calls);
            try
            {
                var content = body?.Invoke();
                var resp = content is null ? await _http.GetAsync(url, ct) : await _http.PostAsync(url, content, ct);
                if ((int)resp.StatusCode is 429 or >= 500 && attempt < 5) { resp.Dispose(); await Backoff(attempt, ct); continue; }
                return await ReadAsync(resp, ct);
            }
            catch (TeraBoxException e) when (e.IsAuth && attempt < 2)
            {
                await RefreshAsync(token, ct);
            }
            catch (TeraBoxException e) when (e.IsThrottle && attempt < 6)
            {
                Interlocked.Increment(ref _throttled);
                await Backoff(attempt, ct);
            }
            catch (HttpRequestException) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                await Backoff(attempt, ct);
            }
        }
    }

    private static Task Backoff(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(Math.Min(16_000, 500 * Math.Pow(2, attempt)) * (0.75 + Random.Shared.NextDouble() * 0.5)), ct);

    private Task<JsonObject> GetAsync(string path, Dictionary<string, string> q, CancellationToken ct) => CallAsync(false, path, q, null, ct);

    private Task<JsonObject> PostFormAsync(string path, Dictionary<string, string>? q, Dictionary<string, string> form, CancellationToken ct) =>
        CallAsync(false, path, q, () => new FormUrlEncodedContent(form), ct);

    // ------------------------------------------------------------------ paths

    /// <summary>Absolute TeraBox path for a path relative to the application folder ("" = the folder itself).</summary>
    public string Abs(string rel) => TeraBoxPaths.Abs(AppRoot, rel);

    internal static TeraBoxEntry ParseEntry(JsonNode n)
    {
        var path = n["path"]!.GetValue<string>();
        var mtime = n["server_mtime"]?.GetValue<long>() ?? n["mtime"]?.GetValue<long>() ?? 0;
        return new TeraBoxEntry(
            n["fs_id"]!.GetValue<long>(), path, n["server_filename"]?.GetValue<string>() ?? path[(path.LastIndexOf('/') + 1)..],
            n["size"]?.GetValueKind() == JsonValueKind.String ? long.Parse(n["size"]!.GetValue<string>(), CultureInfo.InvariantCulture) : n["size"]?.GetValue<long>() ?? 0,
            (n["isdir"]?.GetValue<int>() ?? 0) == 1, DateTimeOffset.FromUnixTimeSeconds(mtime), n["md5"]?.GetValue<string>());
    }

    // ------------------------------------------------------------------ API calls

    public async Task<TeraBoxUser> UserAsync(CancellationToken ct)
    {
        var o = await GetAsync("/openapi/uinfo", new(), ct);
        return new TeraBoxUser(o["uname"]?.GetValue<string>() ?? "", o["vip_type"]?.GetValue<int>() ?? 0);
    }

    public async Task<TeraBoxQuota> QuotaAsync(CancellationToken ct)
    {
        var o = await GetAsync("/openapi/api/quota", new(), ct);
        return new TeraBoxQuota(o["total"]!.GetValue<long>(), o["used"]!.GetValue<long>());
    }

    /// <summary>All entries of a folder (absolute path), following pagination.</summary>
    public async Task<IReadOnlyList<TeraBoxEntry>> ListAsync(string absDir, CancellationToken ct)
    {
        const int num = 1000;
        var all = new List<TeraBoxEntry>();
        for (int page = 1; ; page++)
        {
            var o = await GetAsync("/openapi/api/list", new() { ["dir"] = absDir, ["page"] = page.ToString(), ["num"] = num.ToString(), ["order"] = "name" }, ct);
            var list = (o["list"] as JsonArray ?? o["info"] as JsonArray ?? []).Where(n => n != null).Select(n => ParseEntry(n!)).ToList();
            all.AddRange(list);
            if (list.Count < num) return all;
        }
    }

    public async Task<TeraBoxEntry?> StatAsync(string absPath, CancellationToken ct)
    {
        try
        {
            var o = await GetAsync("/openapi/api/filemetas", new() { ["target"] = JsonSerializer.Serialize(new[] { absPath }) }, ct);
            return (o["info"] as JsonArray)?.FirstOrDefault() is { } n ? ParseEntry(n) : null;
        }
        catch (TeraBoxException e) when (e.IsNotFound) { return null; }
    }

    /// <summary>Download URL for a file. Per the document the access_tokens parameter must be appended to the dlink.</summary>
    public Task<string> DownloadUrlAsync(TeraBoxEntry file, CancellationToken ct) => DownloadUrlAsync(file.FsId, ct);

    public async Task<string> DownloadUrlAsync(long fsId, CancellationToken ct)
    {
        var o = await GetAsync("/openapi/api/download", new() { ["fidlist"] = $"[{fsId}]", ["type"] = "dlink" }, ct);
        var link = (o["dlink"] as JsonArray)?.FirstOrDefault()?["dlink"]?.GetValue<string>() ?? throw new TeraBoxException(-9, "no download link returned");
        return link + (link.Contains('?') ? "&" : "?") + "access_tokens=" + Uri.EscapeDataString(await TokenAsync(ct));
    }

    public async Task<HttpResponseMessage> OpenDownloadAsync(string url, long? start, long? end, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (start is { } s) req.Headers.Range = new RangeHeaderValue(s, end);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Creates a folder (create with isdir=1; assumption, see the mock). Existing folders are not an error.</summary>
    public async Task MkdirAsync(string absPath, CancellationToken ct)
    {
        try { await PostFormAsync("/openapi/api/create", null, new() { ["path"] = absPath, ["isdir"] = "1", ["rtype"] = "0" }, ct); }
        catch (TeraBoxException e) when (e.IsExists) { }
    }

    public async Task FileManagerAsync(string opera, object filelist, CancellationToken ct)
    {
        var o = await PostFormAsync("/openapi/api/filemanager", new() { ["opera"] = opera, ["async"] = "0" },
            new() { ["filelist"] = JsonSerializer.Serialize(filelist) }, ct);
        _ = o;
    }

    public Task DeleteAsync(string absPath, CancellationToken ct) => FileManagerAsync("delete", new[] { absPath }, ct);

    public Task MoveAsync(string absFrom, string absToDir, string newName, CancellationToken ct) =>
        FileManagerAsync("move", new[] { new { path = absFrom, dest = absToDir, newname = newName } }, ct);

    public Task RenameAsync(string absPath, string newName, CancellationToken ct) =>
        FileManagerAsync("rename", new[] { new { path = absPath, newname = newName } }, ct);

    public Task CopyAsync(string absFrom, string absToDir, string newName, CancellationToken ct) =>
        FileManagerAsync("copy", new[] { new { path = absFrom, dest = absToDir, newname = newName } }, ct);

    /// <summary>
    /// Uploads a local file with the documented three steps (precreate, superfile2 shards, create). Each shard's MD5
    /// returned by the server is compared with the locally computed MD5, so a corrupted transfer is never committed.
    /// </summary>
    public async Task<TeraBoxEntry> UploadAsync(string localFile, string absPath, bool overwrite, IProgress<long>? progress, CancellationToken ct)
    {
        var size = new FileInfo(localFile).Length;
        var md5s = new List<string>();
        var buf = new byte[ShardSize];
        await using (var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        {
            int n;
            while ((n = await ReadFullAsync(fs, buf, ct)) > 0) md5s.Add(Convert.ToHexString(MD5.HashData(buf.AsSpan(0, n))).ToLowerInvariant());
        }
        if (md5s.Count == 0) md5s.Add(Convert.ToHexString(MD5.HashData(ReadOnlySpan<byte>.Empty)).ToLowerInvariant());
        var blockList = JsonSerializer.Serialize(md5s);

        var pre = await PostFormAsync("/openapi/api/precreate", null, new() { ["path"] = absPath, ["autoinit"] = "1", ["block_list"] = blockList }, ct);
        if (pre["return_type"]?.GetValue<int>() == 2 && pre["info"] is { } done) return ParseEntry(done); // already in the cloud
        var uploadId = pre["uploadid"]!.GetValue<string>();
        var needed = (pre["block_list"] as JsonArray)?.Select(x => x!.GetValue<int>()).ToHashSet() ?? [];
        if (needed.Count == 0) needed.Add(0);

        await using (var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess))
        {
            long sent = 0;
            for (int i = 0; i < md5s.Count; i++)
            {
                fs.Seek((long)i * ShardSize, SeekOrigin.Begin);
                var n = await ReadFullAsync(fs, buf, ct);
                if (needed.Contains(i))
                {
                    var part = buf.AsMemory(0, n).ToArray();
                    var r = await CallAsync(true, "/rest/2.0/pcs/superfile2", new()
                    {
                        ["method"] = "upload", ["app_id"] = "250528", ["path"] = absPath, ["uploadid"] = uploadId, ["partseq"] = i.ToString()
                    }, () =>
                    {
                        var mp = new MultipartFormDataContent();
                        var bc = new ByteArrayContent(part);
                        bc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        mp.Add(bc, "\"file\"", "\"blob\""); // quoted like curl --form, which the document uses
                        return mp;
                    }, ct);
                    var serverMd5 = r["md5"]?.GetValue<string>();
                    if (serverMd5 != null && !serverMd5.Equals(md5s[i], StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"TeraBox received shard {i} of {absPath} corrupted (MD5 mismatch); the upload was not committed.");
                }
                sent += n;
                progress?.Report(sent);
            }
        }
        var created = await PostFormAsync("/openapi/api/create", null, new()
        {
            ["path"] = absPath, ["size"] = size.ToString(CultureInfo.InvariantCulture), ["uploadid"] = uploadId, ["block_list"] = blockList,
            ["rtype"] = overwrite ? "3" : "0"
        }, ct);
        var entry = ParseEntry(created);
        if (entry.Size != size) throw new IOException($"TeraBox stored {entry.Size} bytes for {absPath}, expected {size}.");
        return entry;
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
