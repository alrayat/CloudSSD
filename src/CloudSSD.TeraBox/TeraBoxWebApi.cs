using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CloudSSD.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CloudSSD.TeraBox;

/// <summary>
/// A TeraBox website session the user created by signing in inside CloudSSD (WebView2). <see cref="Cookie"/> is the
/// session's cookie header for <see cref="BaseUrl"/>; it is stored only DPAPI-protected and never logged.
/// </summary>
public sealed record TeraBoxWebSession(string BaseUrl, string Cookie, string UserAgent, string? JsToken, DateTimeOffset SignedIn,
    string? UploadHost = null, DateTimeOffset? UploadHostExpires = null)
{
    public override string ToString() => $"TeraBoxWebSession({BaseUrl}, signed in {SignedIn:u})"; // never print the cookie
}

public sealed class TeraBoxWebSessionStore(string path)
{
    private readonly object _gate = new();
    public string Path { get; } = path;
    public bool Exists => File.Exists(Path);

    public TeraBoxWebSession? Load()
    {
        lock (_gate)
        {
            var json = SecretStore.Load(Path);
            try { return json is null ? null : JsonSerializer.Deserialize<TeraBoxWebSession>(json); }
            catch (JsonException) { return null; }
        }
    }

    public void Save(TeraBoxWebSession s) { lock (_gate) SecretStore.Save(Path, JsonSerializer.Serialize(s)); }

    public void Delete() { lock (_gate) if (File.Exists(Path)) File.Delete(Path); }
}

/// <summary>
/// UNOFFICIAL TeraBox transport: the TeraBox website's own (undocumented) interface, used with the session of a user who
/// signed in themselves. TeraBox's terms forbid non-public interfaces; this transport exists only because the account
/// owner chose it after being told that TeraBox may restrict accounts that use it. It does not solve captchas, does not
/// evade rate limits or plan limits, and stops with a clear error when TeraBox asks for verification.
///
/// Endpoints, parameters and response fields were taken from reviewed source, not invented: the TeraBox backend of
/// rclone-extra (derrundetisch/rclone-extra, backend/terabox, MIT; originally x1arch's rclone PR #8508), reviewed
/// 2026-10-19. Everything is checked by <c>cloudssd-cli terabox-probe</c> against the real account before it is relied on.
/// </summary>
public sealed class TeraBoxWebApi : ITeraBoxTransport
{
    // Request constants used by the website (as in the reviewed source).
    private const string AppId = "250528";
    private const string Channel = "dubox";
    private const string ClientType = "0";

    /// <summary>Errnos meaning "the page token (jsToken) is missing or stale" (reviewed source).</summary>
    private static readonly int[] JsTokenErrnos = [4000023, 450016];

    private readonly TeraBoxWebSessionStore _store;
    private readonly HttpClient _http; // must not follow redirects itself (see OpenDownloadAsync) and must not use a cookie container
    private readonly ILogger _log;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private TeraBoxWebSession _session;
    private bool? _premium;
    private long _calls, _refreshes, _throttled;

    public TeraBoxWebApi(TeraBoxWebSessionStore store, HttpClient http, ILogger log, string root = DefaultRoot)
    {
        _store = store;
        _http = http;
        _log = log;
        _session = store.Load() ?? throw new TeraBoxException(WebSessionExpired, "Not signed in to TeraBox. Sign in again in Cloud Drives → Connect TeraBox.");
        var baseUri = new Uri(_session.BaseUrl);
        if (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback) throw new InvalidOperationException("The TeraBox session must use HTTPS.");
        AppRoot = "/" + root.Trim('/');
    }

    /// <summary>Folder used as the drive root on TeraBox (everything CloudSSD writes stays inside it).</summary>
    public const string DefaultRoot = "/CloudSSD";

    /// <summary>Errno the website returns when the session is not valid ("Failed to login").</summary>
    public const int WebSessionExpired = -6;

    /// <summary>HttpClient settings this transport needs: no automatic redirects, no cookie container.</summary>
    public static HttpClient CreateHttpClient(TimeSpan? timeout = null) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = timeout ?? TimeSpan.FromMinutes(10) };

    public string Kind => "web (unofficial)";
    public string AppRoot { get; }
    public string Abs(string rel) => TeraBoxPaths.Abs(AppRoot, rel);
    public long Calls => Interlocked.Read(ref _calls);
    public long Refreshes => Interlocked.Read(ref _refreshes);
    public long Throttled => Interlocked.Read(ref _throttled);
    public string BaseUrl => _session.BaseUrl;

    /// <summary>TeraBox's published per-file upload limits: 4 GB free, 128 GB Premium (pricing page, 2026-10-19).</summary>
    public long? MaxFileSize => _premium switch { true => 128L * 1000 * 1000 * 1000, false => 4L * 1000 * 1000 * 1000, null => null };

    // ------------------------------------------------------------------ transport

    private HttpRequestMessage NewRequest(HttpMethod method, string url, HttpContent? content)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Cookie", _session.Cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", _session.UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", _session.BaseUrl.TrimEnd('/') + "/");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        return req;
    }

    private string BuildUrl(string pathOrUrl, IEnumerable<KeyValuePair<string, string>>? query, bool common)
    {
        var q = new List<KeyValuePair<string, string>>(query ?? []);
        if (common)
        {
            q.Add(new("app_id", AppId));
            q.Add(new("channel", Channel));
            q.Add(new("clienttype", ClientType));
            if (!string.IsNullOrEmpty(_session.JsToken)) q.Add(new("jsToken", _session.JsToken));
        }
        var baseUrl = pathOrUrl.StartsWith("https://", StringComparison.Ordinal) || pathOrUrl.StartsWith("http://", StringComparison.Ordinal)
            ? pathOrUrl : _session.BaseUrl.TrimEnd('/') + pathOrUrl;
        if (q.Count == 0) return baseUrl;
        return baseUrl + (baseUrl.Contains('?') ? "&" : "?") + string.Join('&', q.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>errno 12: a batch call where some items failed; the per-item errno says which and why (reviewed source).</summary>
    private static readonly int[] BatchPartial = [12];

    private async Task<JsonObject> CallAsync(HttpMethod method, string path, Dictionary<string, string>? query, Func<HttpContent?>? body, CancellationToken ct,
        bool common = true, IReadOnlyCollection<int>? tolerate = null)
    {
        var jsTokenRefreshed = false;
        var domainSwitched = false;
        for (int attempt = 0; ; attempt++)
        {
            var url = BuildUrl(path, query, common);
            Interlocked.Increment(ref _calls);
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(NewRequest(method, url, body?.Invoke()), ct);
            }
            catch (HttpRequestException) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                await Backoff(attempt, ct);
                continue;
            }
            if ((int)resp.StatusCode is 429 or >= 500 && attempt < 4)
            {
                if ((int)resp.StatusCode == 429) Interlocked.Increment(ref _throttled);
                resp.Dispose();
                await Backoff(attempt, ct);
                continue;
            }
            if ((int)resp.StatusCode is >= 300 and < 400)
            {
                resp.Dispose(); // API calls are not redirected while the session is valid; a redirect means the login page
                throw new TeraBoxException(WebSessionExpired, "TeraBox redirected the request (the website session has ended). Sign in to TeraBox again.");
            }
            var prefix = resp.Headers.TryGetValues("Url-Domain-Prefix", out var v) ? v.FirstOrDefault() : null;
            try
            {
                return await TeraBoxApi.ReadAsync(resp, ct, tolerate);
            }
            catch (TeraBoxException e) when (JsTokenErrnos.Contains(e.Errno) && !jsTokenRefreshed)
            {
                jsTokenRefreshed = true;
                await RefreshJsTokenAsync(ct);
            }
            catch (TeraBoxException e) when (e.Errno == WebSessionExpired && prefix is { Length: > 0 } && !domainSwitched)
            {
                // Some accounts are served from another host; the website tells the client which one (reviewed source).
                domainSwitched = true;
                SwitchHost(prefix);
            }
            catch (TeraBoxException e) when (e.Errno == WebSessionExpired)
            {
                throw new TeraBoxException(WebSessionExpired, "The TeraBox website session has ended. Sign in to TeraBox again in Cloud Drives (unofficial connection).");
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

    private void SwitchHost(string prefix)
    {
        var host = new Uri(_session.BaseUrl).Host.Split('.');
        if (host.Length < 2 || !Regex.IsMatch(prefix, "^[a-z0-9-]{1,40}$", RegexOptions.IgnoreCase)) return;
        var newBase = $"https://{prefix}.{host[^2]}.{host[^1]}";
        _log.LogInformation("TeraBox serves this account from {Host}; switching", new Uri(newBase).Host);
        Update(_session with { BaseUrl = newBase });
    }

    private void Update(TeraBoxWebSession s)
    {
        _session = s;
        try { _store.Save(s); } catch (IOException e) { _log.LogWarning("Could not save the TeraBox session: {Error}", e.Message); }
    }

    /// <summary>
    /// The website embeds a page token in its HTML (window.jsToken, URL-encoded inside a script). Fetched with the user's
    /// session exactly like the browser does; if the page asks for verification instead, the user must sign in again.
    /// </summary>
    public async Task RefreshJsTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            using var resp = await _http.SendAsync(NewRequest(HttpMethod.Get, _session.BaseUrl.TrimEnd('/') + "/main", null), ct);
            var html = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync(ct));
            var token = ExtractJsToken(html) ?? throw new TeraBoxException(WebSessionExpired,
                "TeraBox did not provide a page token (the session may have ended or TeraBox asks for verification). Sign in to TeraBox again.");
            Update(_session with { JsToken = token });
            Interlocked.Increment(ref _refreshes);
            _log.LogInformation("TeraBox page token refreshed");
        }
        finally { _tokenGate.Release(); }
    }

    /// <summary>Same markers as the reviewed source: <c>function fn(a){window.jsToken = a};fn("…")</c>, URL-encoded.</summary>
    public static string? ExtractJsToken(string html)
    {
        var m = Regex.Match(html, Regex.Escape("function%20fn%28a%29%7Bwindow.jsToken%20%3D%20a%7D%3Bfn%28%22") + "(.*?)" + Regex.Escape("%22%29"));
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
    }

    // ------------------------------------------------------------------ account

    public async Task<TeraBoxUser> UserAsync(CancellationToken ct)
    {
        var login = await CallAsync(HttpMethod.Get, "/api/check/login", null, null, ct);
        var vip = 0;
        try
        {
            var m = await CallAsync(HttpMethod.Get, "/rest/2.0/membership/proxy/user", new() { ["method"] = "query", ["membership_version"] = "1.0" }, null, ct);
            vip = (int)(m["data"]?["member_info"]?["is_vip"]?.GetValue<long>() ?? 0);
            _premium = vip > 0;
        }
        catch (TeraBoxException e) when (e.Errno != WebSessionExpired) { _log.LogWarning("TeraBox plan query failed: {Error}", e.Message); }
        var uk = login["uk"]?.ToString();
        return new TeraBoxUser(uk is { Length: > 0 } ? $"TeraBox user …{uk[^Math.Min(4, uk.Length)..]}" : "TeraBox web account", vip);
    }

    public async Task<TeraBoxQuota> QuotaAsync(CancellationToken ct)
    {
        var o = await CallAsync(HttpMethod.Get, "/api/quota", new() { ["checkexpire"] = "1", ["checkfree"] = "1" }, null, ct);
        return new TeraBoxQuota(o["total"]!.GetValue<long>(), o["used"]!.GetValue<long>());
    }

    // ------------------------------------------------------------------ metadata

    public async Task<IReadOnlyList<TeraBoxEntry>> ListAsync(string absDir, CancellationToken ct)
    {
        const int num = 100; // page size used by the reviewed source
        var all = new List<TeraBoxEntry>();
        for (int page = 1; ; page++)
        {
            var o = await CallAsync(HttpMethod.Get, "/api/list", new() { ["dir"] = absDir, ["page"] = page.ToString(CultureInfo.InvariantCulture), ["num"] = num.ToString(CultureInfo.InvariantCulture) }, null, ct);
            var list = (o["list"] as JsonArray ?? []).Where(n => n != null).Select(n => TeraBoxApi.ParseEntry(n!)).ToList();
            all.AddRange(list);
            if (list.Count < num) return all;
        }
    }

    private async Task<JsonNode?> FileMetaAsync(string absPath, bool dlink, CancellationToken ct)
    {
        JsonObject o;
        try
        {
            o = await CallAsync(HttpMethod.Get, "/api/filemetas", new() { ["target"] = JsonSerializer.Serialize(new[] { absPath }), ["dlink"] = dlink ? "1" : "0" }, null, ct,
                tolerate: BatchPartial);
        }
        catch (TeraBoxException e) when (e.IsNotFound) { return null; }
        var item = (o["info"] as JsonArray)?.FirstOrDefault();
        var errno = item?["errno"]?.GetValue<int>() ?? 0;
        if (errno == -9) return null;
        if (errno != 0) throw new TeraBoxException(errno, $"TeraBox error {errno} for {absPath}");
        return item;
    }

    public async Task<TeraBoxEntry?> StatAsync(string absPath, CancellationToken ct) =>
        await FileMetaAsync(absPath, false, ct) is { } n ? TeraBoxApi.ParseEntry(n) : null;

    // ------------------------------------------------------------------ reads

    public async Task<string> DownloadUrlAsync(TeraBoxEntry file, CancellationToken ct)
    {
        if (await FileMetaAsync(file.Path, true, ct) is { } n && n["dlink"]?.GetValue<string>() is { Length: > 0 } link) return link;
        // Fallback used by the reviewed source: sign values from /api/home/info, then /api/download.
        var home = await CallAsync(HttpMethod.Get, "/api/home/info", null, null, ct);
        var sign = Sign(home["data"]?["sign3"]?.GetValue<string>() ?? "", home["data"]?["sign1"]?.GetValue<string>() ?? "");
        var o = await CallAsync(HttpMethod.Get, "/api/download", new()
        {
            ["type"] = "dlink", ["vip"] = "2", ["sign"] = sign, ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["need_speed"] = "1", ["fidlist"] = $"[{file.FsId}]"
        }, null, ct);
        return (o["dlink"] as JsonArray)?.FirstOrDefault()?["dlink"]?.GetValue<string>() ?? throw new TeraBoxException(-9, "TeraBox returned no download link");
    }

    /// <summary>
    /// Opens a download link. Redirects are followed here (up to 5) so the Range header is kept on every hop and the
    /// session cookie is only sent to hosts of the same site (never to a foreign host a redirect might name).
    /// </summary>
    public async Task<HttpResponseMessage> OpenDownloadAsync(string url, long? start, long? end, CancellationToken ct)
    {
        var current = new Uri(url);
        if (current.Scheme != Uri.UriSchemeHttps && !current.IsLoopback) throw new HttpRequestException("TeraBox returned a non-HTTPS download link; refused.");
        for (int hop = 0; ; hop++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.TryAddWithoutValidation("User-Agent", _session.UserAgent);
            req.Headers.TryAddWithoutValidation("Referer", _session.BaseUrl.TrimEnd('/') + "/");
            // The link itself was issued by TeraBox's API, so the session goes with the first request (as in the reviewed
            // source; measured 2026-10-19: without it the download host answered 403). Redirect targets only get it when
            // they belong to TeraBox's own domains.
            if (hop == 0 || IsTeraBoxFamily(current.Host) || current.IsLoopback) req.Headers.TryAddWithoutValidation("Cookie", _session.Cookie);
            if (start is { } s) req.Headers.Range = new RangeHeaderValue(s, end);
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is { } loc && hop < 5)
            {
                current = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                resp.Dispose();
                if (current.Scheme != Uri.UriSchemeHttps && !current.IsLoopback) throw new HttpRequestException("TeraBox redirected a download to a non-HTTPS address; refused.");
                continue;
            }
            return resp;
        }
    }

    /// <summary>Registrable domains TeraBox serves its site, API and downloads from.</summary>
    public static readonly string[] TeraBoxDomains = ["terabox.com", "1024terabox.com", "terabox.app", "teraboxapp.com", "1024tera.com", "terabox1024.com", "teraboxcdn.com", "freeterabox.com", "nephobox.com"];

    public static bool IsTeraBoxFamily(string host) =>
        TeraBoxDomains.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ changes

    private static MultipartFormDataContent Form(IEnumerable<KeyValuePair<string, string>> fields)
    {
        var mp = new MultipartFormDataContent();
        foreach (var (k, v) in fields) mp.Add(new StringContent(v, Encoding.UTF8), $"\"{k}\"");
        return mp;
    }

    public async Task MkdirAsync(string absPath, CancellationToken ct)
    {
        try
        {
            await CallAsync(HttpMethod.Post, "/api/create", null, () => Form(new Dictionary<string, string> { ["path"] = absPath, ["isdir"] = "1", ["rtype"] = "0" }), ct);
        }
        catch (TeraBoxException e) when (e.IsExists) { }
    }

    /// <summary>"Another file operation is still running" (measured 2026-10-20: parallel renames during a Steam download).</summary>
    public const int FileManagerBusy = 111;
    private readonly SemaphoreSlim _fileManagerGate = new(1, 1);
    public static TimeSpan FileManagerBusyDelay { get; set; } = TimeSpan.FromSeconds(1);

    private async Task FileManagerAsync(string opera, object filelist, CancellationToken ct)
    {
        // TeraBox runs one rename/move/copy/delete task per account at a time and refuses others with error 111. Send them
        // one by one, each waiting for its task to finish, and retry a refusal after a short pause.
        await _fileManagerGate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var o = await CallAsync(HttpMethod.Post, "/api/filemanager",
                        new() { ["opera"] = opera, ["async"] = opera == "copy" ? "2" : "1", ["onnest"] = "fail" },
                        () => new FormUrlEncodedContent(new Dictionary<string, string> { ["filelist"] = JsonSerializer.Serialize(filelist) }), ct, tolerate: BatchPartial);
                    foreach (var item in o["info"] as JsonArray ?? [])
                        if (item?["errno"]?.GetValue<int>() is { } e and not 0) throw new TeraBoxException(e, $"TeraBox {opera} failed (error {e})");
                    if (o["errno"]?.GetValue<int>() is { } top and not 0) throw new TeraBoxException(top, $"TeraBox {opera} failed (error {top})");
                    if (o["taskid"]?.GetValue<long>() is { } task and > 0) await WaitTaskAsync(opera, task, ct);
                    return;
                }
                catch (TeraBoxException e) when (e.Errno == FileManagerBusy && attempt < 8)
                {
                    _log.LogInformation("TeraBox {Opera}: another file operation is still running; retrying", opera);
                    await Task.Delay(FileManagerBusyDelay * Math.Min(15, 1 << attempt), ct);
                }
            }
        }
        finally { _fileManagerGate.Release(); }
    }

    private async Task WaitTaskAsync(string opera, long taskId, CancellationToken ct)
    {
        for (int i = 0; i < 60; i++)
        {
            var o = await CallAsync(HttpMethod.Get, "/share/taskquery", new() { ["taskid"] = taskId.ToString(CultureInfo.InvariantCulture) }, null, ct);
            var status = o["status"]?.GetValue<string>();
            if (status is "running" or "pending") { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 250 * (i + 1))), ct); continue; }
            foreach (var item in o["list"] as JsonArray ?? [])
                if (item?["errno"]?.GetValue<int>() is { } e and not 0) throw new TeraBoxException(e, $"TeraBox {opera} failed (error {e})");
            if (status == "failed") throw new TeraBoxException(15, $"TeraBox {opera} failed");
            return;
        }
        throw new TimeoutException($"TeraBox {opera} did not finish within the expected time");
    }

    public Task DeleteAsync(string absPath, CancellationToken ct) => FileManagerAsync("delete", new[] { absPath }, ct);

    public Task MoveAsync(string absFrom, string absToDir, string newName, CancellationToken ct) =>
        FileManagerAsync("move", new[] { new { path = absFrom, dest = absToDir, newname = newName } }, ct);

    public Task RenameAsync(string absPath, string newName, CancellationToken ct) =>
        FileManagerAsync("rename", new[] { new { path = absPath, newname = newName } }, ct);

    public Task CopyAsync(string absFrom, string absToDir, string newName, CancellationToken ct) =>
        FileManagerAsync("copy", new[] { new { path = absFrom, dest = absToDir, newname = newName } }, ct);

    // ------------------------------------------------------------------ upload

    /// <summary>Shard size the website uses: 4 MiB on free accounts; larger on Premium, growing with the file size.</summary>
    public static int ShardSizeFor(long fileSize, bool premium)
    {
        const long MiB = 1024 * 1024, GiB = 1024 * MiB;
        if (!premium) return (int)(4 * MiB);
        foreach (var limit in new long[] { 4, 8, 16, 32, 64, 128 })
            if (fileSize <= limit * GiB) return (int)(limit * MiB);
        return (int)(128 * MiB);
    }

    private async Task<string> UploadHostAsync(CancellationToken ct)
    {
        if (_session.UploadHost is { Length: > 0 } h && _session.UploadHostExpires > DateTimeOffset.UtcNow) return h;
        var o = await CallAsync(HttpMethod.Get, "/rest/2.0/pcs/file?method=locateupload", null, null, ct, common: false);
        var host = o["host"]?.GetValue<string>() ?? throw new TeraBoxException(1, "TeraBox did not name an upload server");
        if (!Regex.IsMatch(host, @"^[a-z0-9.-]+(:\d{1,5})?$", RegexOptions.IgnoreCase)) throw new TeraBoxException(1, "TeraBox named an invalid upload server");
        var expire = o["expire"]?.GetValue<long>() ?? 0;
        Update(_session with { UploadHost = host, UploadHostExpires = DateTimeOffset.UtcNow.AddSeconds(expire > 0 ? Math.Min(expire, 86_400) : 600) });
        return host;
    }

    /// <summary>
    /// precreate → superfile2 shards (each shard's MD5 confirmed by the server) → create; the MD5 TeraBox reports for the
    /// finished file (encoded by the website, decoded with <see cref="DecodeMd5"/>) must match the shard list, so a
    /// corrupted upload is never accepted. Files over the plan limit and empty files are refused before any transfer.
    /// </summary>
    /// <summary>Shards of one file uploaded at the same time, and the most shard bytes held in memory for that.</summary>
    public static int UploadParallelShards { get; set; } = 4;
    public static long MaxShardBytesInFlight { get; set; } = 64L * 1024 * 1024;

    public async Task<TeraBoxEntry> UploadAsync(string localFile, string absPath, bool overwrite, IProgress<long>? progress, CancellationToken ct)
    {
        var info = new FileInfo(localFile);
        var size = info.Length;
        if (size == 0) throw new TeraBoxException(EmptyFileUnsupported, "TeraBox's website interface does not accept empty (0-byte) files.");
        if (_premium is null) { try { await UserAsync(ct); } catch (TeraBoxException e) when (e.Errno != WebSessionExpired) { } }
        if (MaxFileSize is { } max && size > max)
            throw new TeraBoxException(PlanFileLimit, $"{Path.GetFileName(absPath)} is larger than your TeraBox plan allows per file ({Core.ByteSize.Format(max)}).");

        var shard = ShardSizeFor(size, _premium == true);
        var md5s = new List<string>();
        var buf = new byte[shard];
        await using (var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        {
            int n;
            while ((n = await ReadFullAsync(fs, buf, ct)) > 0) md5s.Add(Convert.ToHexString(MD5.HashData(buf.AsSpan(0, n))).ToLowerInvariant());
        }
        var dir = absPath[..(absPath.LastIndexOf('/') + 1)];
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sizeText = size.ToString(CultureInfo.InvariantCulture);
        var host = await UploadHostAsync(ct);

        // The website sends placeholder MD5s at precreate (one or two, as in the reviewed source) and the real list at create.
        string[] placeholder = size > shard ? ["5910a591dd8fc18c32a8f3df4fdc1761", "a5fc157d78e6ad1c7e114b056c92821e"] : ["5910a591dd8fc18c32a8f3df4fdc1761"];
        var pre = await CallAsync(HttpMethod.Post, "/api/precreate", null, () => Form(new Dictionary<string, string>
        {
            ["path"] = absPath, ["autoinit"] = "1", ["local_mtime"] = mtime, ["file_limit_switch_v34"] = "true", ["size"] = sizeText,
            ["target_path"] = dir, ["block_list"] = JsonSerializer.Serialize(placeholder)
        }), ct);
        if (pre["return_type"]?.GetValue<int>() == 2) throw new TeraBoxException(-8, $"{absPath} already exists on TeraBox");
        var uploadId = pre["uploadid"]?.GetValue<string>() ?? throw new TeraBoxException(1, "TeraBox did not start the upload");

        // Shards go up several at a time, as on the website: one file at a time was the upload bottleneck while Steam
        // finished files one by one (measured 2026-10-20: ~1 MB/s into TeraBox). Each shard's MD5 is still confirmed.
        var parallel = Math.Max(1, Math.Min(UploadParallelShards, (int)Math.Max(1, MaxShardBytesInFlight / shard)));
        long sent = 0;
        using (var handle = File.OpenHandle(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous))
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, md5s.Count), new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (i, token) =>
            {
                var offset = (long)i * shard;
                var part = new byte[(int)Math.Min(shard, size - offset)];
                for (int read = 0; read < part.Length;)
                {
                    var n = await RandomAccess.ReadAsync(handle, part.AsMemory(read), offset + read, token);
                    if (n == 0) throw new IOException($"{localFile} became shorter during the upload.");
                    read += n;
                }
                for (int attempt = 0; ; attempt++)
                {
                    // Same scheme as the site (always https for TeraBox; plain http only for the loopback test mock).
                    var r = await CallAsync(HttpMethod.Post, $"{new Uri(_session.BaseUrl).Scheme}://{host}/rest/2.0/pcs/superfile2", new()
                    {
                        ["method"] = "upload", ["path"] = absPath, ["uploadid"] = uploadId, ["partseq"] = i.ToString(CultureInfo.InvariantCulture), ["uploadsign"] = "0"
                    }, () =>
                    {
                        var mp = new MultipartFormDataContent();
                        var bc = new ByteArrayContent(part);
                        bc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        mp.Add(bc, "\"file\"", "\"blob\"");
                        return mp;
                    }, token);
                    if (string.Equals(r["md5"]?.GetValue<string>(), md5s[i], StringComparison.OrdinalIgnoreCase)) break;
                    if (attempt >= 3) throw new IOException($"TeraBox received shard {i} of {absPath} corrupted (MD5 mismatch); the upload was not committed.");
                }
                progress?.Report(Interlocked.Add(ref sent, part.Length));
            });
        }

        var blockList = JsonSerializer.Serialize(md5s);
        var created = await CallAsync(HttpMethod.Post, "/api/create", new() { ["isdir"] = "0", ["rtype"] = overwrite ? "3" : "0" }, () => Form(new Dictionary<string, string>
        {
            ["path"] = absPath, ["local_mtime"] = mtime, ["uploadid"] = uploadId, ["size"] = sizeText, ["target_path"] = dir, ["block_list"] = blockList
        }), ct);
        var control = md5s.Count == 1 ? md5s[0] : Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(blockList))).ToLowerInvariant();
        if (created["md5"]?.GetValue<string>() is { } reported && !string.Equals(DecodeMd5(reported), control, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"TeraBox's checksum for {absPath} does not match what was uploaded; treat the cloud copy as damaged.");
        // The create reply's size is not trusted (measured: 0 for a multi-shard file that was stored correctly); the stored
        // entry is checked instead, giving TeraBox a few seconds to finish assembling the shards.
        TeraBoxEntry? entry = null;
        for (int i = 0; i < 8; i++)
        {
            entry = await StatAsync(absPath, ct);
            if (entry is { Size: var s2 } && s2 == size) return entry;
            await Task.Delay(TimeSpan.FromMilliseconds(500 * (i + 1)), ct);
        }
        throw new IOException(entry is null
            ? $"{absPath} is not visible on TeraBox after upload."
            : $"TeraBox stored {entry.Size} bytes for {absPath}, expected {size}.");
    }

    /// <summary>Local error codes (not TeraBox's) for refusals made before any transfer.</summary>
    public const int EmptyFileUnsupported = -100_001, PlanFileLimit = 58;

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

    // ------------------------------------------------------------------ website encodings (ported from the reviewed source)

    /// <summary>The website returns file MD5s in a scrambled form; this restores the real MD5 (port of decodeMD5).</summary>
    public static string DecodeMd5(string md5)
    {
        if (md5.Length != 32) return md5;
        var restored = ((byte)(md5[9] - 'g')).ToString("x");
        var o = md5[..9] + restored + md5[10..];
        if (o.Length != 32) return md5;
        var n = new char[32];
        for (int i = 0; i < 32; i++)
        {
            if (!int.TryParse(o[i].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var orig)) return md5;
            n[i] = (orig ^ (i & 15)).ToString("x")[0];
        }
        var s = new string(n);
        return s[8..16] + s[..8] + s[24..32] + s[16..24];
    }

    /// <summary>Inverse of <see cref="DecodeMd5"/> (used by the test mock).</summary>
    public static string EncodeMd5(string md5)
    {
        var n = md5[8..16] + md5[..8] + md5[24..32] + md5[16..24];
        var o = new char[32];
        for (int i = 0; i < 32; i++) o[i] = (Convert.ToInt32(n[i].ToString(), 16) ^ (i & 15)).ToString("x")[0];
        return new string(o, 0, 9) + (char)('g' + Convert.ToInt32(o[9].ToString(), 16)) + new string(o, 10, 22);
    }

    /// <summary>Download-request signature the website computes from /api/home/info's sign3 and sign1 (port of sign()).</summary>
    public static string Sign(string s1, string s2)
    {
        if (s1.Length == 0) return "";
        var a = new int[256];
        var p = new int[256];
        for (int q = 0; q < 256; q++) { a[q] = s1[q % s1.Length]; p[q] = q; }
        for (int u = 0, q = 0; q < 256; q++)
        {
            u = (u + p[q] + a[q]) % 256;
            (p[q], p[u]) = (p[u], p[q]);
        }
        var o = new byte[s2.Length];
        for (int i = 0, u = 0, q = 0; q < s2.Length; q++)
        {
            i = (i + 1) % 256;
            u = (u + p[i]) % 256;
            (p[i], p[u]) = (p[u], p[i]);
            var k = p[(p[i] + p[u]) % 256];
            o[q] = (byte)(s2[q] ^ k);
        }
        return Convert.ToBase64String(o);
    }
}
