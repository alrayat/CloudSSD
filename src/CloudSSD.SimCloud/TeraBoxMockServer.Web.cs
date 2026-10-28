using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CloudSSD.Core.Net;

namespace CloudSSD.SimCloud;

/// <summary>
/// Stand-in for the TeraBox WEBSITE interface (unofficial), as the reviewed rclone-extra TeraBox backend uses it:
/// a session cookie (ndus) instead of tokens, a page token (jsToken) embedded URL-encoded in the /main page and required by
/// changing calls (errno 4000023 when missing or stale), /api/* paths, an upload host from locateupload, placeholder MD5s
/// at precreate and the real list at create, the website's scrambled MD5 in create responses, async filemanager tasks
/// polled through /share/taskquery, per-item errno in filemetas, signed /api/download, and dlinks that redirect.
/// This encodes our reading of that source, not TeraBox documentation: the real account probe is what proves it.
/// The whole drive is reachable ("/"), unlike the official app folder.
/// </summary>
public sealed partial class TeraBoxMockServer
{
    /// <summary>Value of the "ndus" cookie the mock accepts as a signed-in website session.</summary>
    public string WebSessionCookie { get; set; } = "mock-ndus-" + Guid.NewGuid().ToString("N");
    public string WebJsToken { get; private set; } = "JT" + Guid.NewGuid().ToString("N").ToUpperInvariant();
    public bool WebSessionExpired { get; set; }
    /// <summary>When true, dlinks answer 302 to the real file URL (exercises redirect handling and Range forwarding).</summary>
    public bool WebDlinkRedirects { get; set; } = true;
    /// <summary>When true, filemetas never includes a dlink, forcing the signed /api/download fallback.</summary>
    public bool WebNoMetaDlink { get; set; }
    public int WebJsTokenRejections;
    public int WebSignedDownloads;
    private readonly ConcurrentDictionaryCounter _taskPolls = new();
    private readonly string _sign1 = "s1" + Guid.NewGuid().ToString("N"), _sign3 = "s3" + Guid.NewGuid().ToString("N");

    /// <summary>Makes the current page token stale (as TeraBox does over time); the client must fetch /main again.</summary>
    public void RotateJsToken() => WebJsToken = "JT" + Guid.NewGuid().ToString("N").ToUpperInvariant();

    public string WebCookieHeader => $"ndus={WebSessionCookie}; lang=en";

    private static bool IsWebRoute(HttpContext ctx)
    {
        var p = ctx.Path;
        return p.StartsWith("/api/", StringComparison.Ordinal) || p.StartsWith("/share/", StringComparison.Ordinal) || p == "/main"
               || p.StartsWith("/rest/2.0/membership/", StringComparison.Ordinal) || p == "/rest/2.0/pcs/file" || p.StartsWith("/webdl/", StringComparison.Ordinal)
               || (p == "/rest/2.0/pcs/superfile2" && !ctx.Query.ContainsKey("access_tokens"));
    }

    private bool WebSessionValid(HttpContext ctx)
    {
        if (WebSessionExpired) return false;
        var cookie = ctx.Header("Cookie") ?? "";
        return cookie.Split(';').Select(c => c.Trim()).Any(c => c == "ndus=" + WebSessionCookie);
    }

    private async Task WebAsync(HttpContext ctx)
    {
        Interlocked.Increment(ref _apiCalls);
        var p = ctx.Path;
        if (p == "/main")
        {
            var html = WebSessionValid(ctx)
                ? "<html><head><script>eval(decodeURIComponent(`function%20fn%28a%29%7Bwindow.jsToken%20%3D%20a%7D%3Bfn%28%22" + WebJsToken + "%22%29`))</script></head><body>TeraBox</body></html>"
                : "<html><body>Please log in</body></html>";
            await ctx.RespondAsync(200, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
            return;
        }
        if (p.StartsWith("/webdl/", StringComparison.Ordinal))
        {
            var key = p["/webdl/".Length..];
            await ctx.RespondAsync(302, headers: new Dictionary<string, string> { ["Location"] = $"{BaseUrl}/file/{key}?dl=1" });
            return;
        }
        JsonObject result;
        try
        {
            if (!WebSessionValid(ctx)) throw new ApiError(-6, "Failed to login, please try again later");
            if (ShouldThrottle()) throw new ApiError(31034, "hit frequency control");
            foreach (var (k, v) in new[] { ("app_id", "250528"), ("channel", "dubox"), ("clienttype", "0") })
                if (p != "/rest/2.0/pcs/file" && ctx.Query.GetValueOrDefault(k) != v) throw new ApiError(2, $"parameter {k} missing");
            if (p is "/api/filemanager" or "/api/precreate" or "/api/create" or "/rest/2.0/pcs/superfile2" && ctx.Query.GetValueOrDefault("jsToken") != WebJsToken)
            {
                Interlocked.Increment(ref WebJsTokenRejections);
                throw new ApiError(4000023, "need verify");
            }
            result = p switch
            {
                "/api/check/login" => Ok(new JsonObject { ["uk"] = 4400012345L }),
                "/rest/2.0/membership/proxy/user" => Ok(new JsonObject { ["data"] = new JsonObject { ["member_info"] = new JsonObject { ["is_vip"] = VipType > 0 ? 1 : 0, ["vip_left_time"] = 0 } } }),
                "/api/quota" => WebQuota(),
                "/api/list" => List(ctx.Query, webPaths: true),
                "/api/filemetas" => WebFileMetas(ctx.Query),
                "/api/home/info" => Ok(new JsonObject { ["data"] = new JsonObject { ["sign1"] = _sign1, ["sign3"] = _sign3, ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() } }),
                "/api/download" => WebDownload(ctx.Query),
                "/rest/2.0/pcs/file" when ctx.Query.GetValueOrDefault("method") == "locateupload" => Ok(new JsonObject { ["host"] = Domain, ["expire"] = 60 }),
                "/api/precreate" => WebPrecreate(MultipartFields(await ctx.ReadBodyAsync(), ctx.Header("Content-Type") ?? "")),
                "/rest/2.0/pcs/superfile2" => await WebUploadPartAsync(ctx),
                "/api/create" => WebCreate(ctx.Query, MultipartFields(await ctx.ReadBodyAsync(), ctx.Header("Content-Type") ?? "")),
                "/api/filemanager" => WebFileManager(ctx.Query, await ctx.ReadFormAsync()),
                "/share/taskquery" => WebTask(ctx.Query),
                _ => throw new ApiError(3, "unknown method " + p)
            };
        }
        catch (ApiError e) { result = Err(e.Errno, e.Message); }
        result["request_id"] = Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999);
        await ctx.RespondJsonAsync(result.ToJsonString());
    }

    /// <summary>The website reaches the whole drive: only path syntax is checked (no application-folder restriction).</summary>
    private static string CheckWebPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) throw new ApiError(-7, "Invalid file name");
        path = "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (path.Split('/').Any(s => s is "." or "..")) throw new ApiError(-7, "Invalid file name");
        return path;
    }

    private JsonObject WebQuota()
    {
        var used = UsedBytesAll();
        return Ok(new JsonObject { ["total"] = QuotaTotal, ["used"] = used, ["free"] = QuotaTotal - used, ["expire"] = false });
    }

    private long UsedBytesAll()
    {
        lock (_fsGate) return Directory.EnumerateFiles(DataRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
    }

    private JsonObject WebFileMetas(IReadOnlyDictionary<string, string> q)
    {
        var targets = JsonSerializer.Deserialize<string[]>(q.GetValueOrDefault("target") ?? "[]") ?? [];
        var arr = new JsonArray();
        var anyMissing = false;
        foreach (var t in targets)
        {
            var path = CheckWebPath(t);
            if (!Exists(path)) { arr.Add(new JsonObject { ["errno"] = -9 }); anyMissing = true; continue; }
            var e = Entry(path);
            e["errno"] = 0;
            if (q.GetValueOrDefault("dlink") == "1" && e["isdir"]!.GetValue<int>() == 0 && !WebNoMetaDlink) e["dlink"] = NewWebDlink(path);
            arr.Add(e);
        }
        var res = Ok(new JsonObject { ["info"] = arr });
        if (anyMissing) res["errno"] = 12; // as in the reviewed source's example: {"errno":12,"info":[{"errno":-9}]}
        return res;
    }

    private string NewWebDlink(string path)
    {
        var key = Guid.NewGuid().ToString("N");
        _dlinks[key] = new DlinkGrant { Path = path, Expires = DateTime.UtcNow + DlinkLifetime };
        return WebDlinkRedirects ? $"{BaseUrl}/webdl/{key}" : $"{BaseUrl}/file/{key}?dl=1";
    }

    private JsonObject WebDownload(IReadOnlyDictionary<string, string> q)
    {
        if (q.GetValueOrDefault("type") != "dlink") throw new ApiError(2, "type must be dlink");
        if (q.GetValueOrDefault("sign") != MockSign(_sign3, _sign1)) throw new ApiError(2, "invalid sign");
        var ids = JsonSerializer.Deserialize<long[]>(q.GetValueOrDefault("fidlist") ?? "[]") ?? [];
        var arr = new JsonArray();
        foreach (var id in ids)
        {
            var path = PathForId(id);
            if (path is null || !File.Exists(DiskPath(path))) throw new ApiError(-9, "The file doesn't exist");
            arr.Add(new JsonObject { ["fs_id"] = id.ToString(CultureInfo.InvariantCulture), ["dlink"] = NewWebDlink(path) });
        }
        Interlocked.Increment(ref WebSignedDownloads);
        return Ok(new JsonObject { ["dlink"] = arr });
    }

    private sealed class WebUpload { public required string Path; public required long Size; public required string Dir; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WebUpload> _webUploads = new();

    private JsonObject WebPrecreate(IReadOnlyDictionary<string, string> f)
    {
        var path = CheckWebPath(f.GetValueOrDefault("path"));
        if (f.GetValueOrDefault("autoinit") != "1") throw new ApiError(2, "autoinit must be 1");
        if (!long.TryParse(f.GetValueOrDefault("size"), out var size) || size <= 0) throw new ApiError(2, "size is required");
        if (size > EffectiveMaxFileSize) throw new ApiError(58, "Upload single file vip limit");
        var id = "W1-" + Guid.NewGuid().ToString("N");
        var u = new WebUpload { Path = path, Size = size, Dir = Path.Combine(StateRoot, "webuploads", id) };
        Directory.CreateDirectory(u.Dir);
        _webUploads[id] = u;
        return Ok(new JsonObject { ["path"] = path, ["uploadid"] = id, ["return_type"] = 1, ["block_list"] = new JsonArray(0) });
    }

    private int _webPartsInFlight;
    /// <summary>Most shard uploads seen at the same time, and an optional delay per shard to make overlap observable.</summary>
    public int WebMaxPartsInFlight;
    public TimeSpan WebPartDelay = TimeSpan.Zero;

    private async Task<JsonObject> WebUploadPartAsync(HttpContext ctx)
    {
        var q = ctx.Query;
        if (q.GetValueOrDefault("method") != "upload") throw new ApiError(2, "method=upload is required");
        if (!_webUploads.TryGetValue(q.GetValueOrDefault("uploadid") ?? "", out var u)) throw new ApiError(31363, "upload id not found");
        if (CheckWebPath(q.GetValueOrDefault("path")) != u.Path) throw new ApiError(31363, "path does not match upload id");
        if (!int.TryParse(q.GetValueOrDefault("partseq"), out var seq) || seq < 0) throw new ApiError(2, "invalid partseq");
        var body = await ctx.ReadBodyAsync(160 * MiB);
        var data = MultipartFile(body, ctx.Header("Content-Type") ?? "") ?? throw new ApiError(2, "multipart field 'file' is required");
        var now = Interlocked.Increment(ref _webPartsInFlight);
        lock (_webUploads) WebMaxPartsInFlight = Math.Max(WebMaxPartsInFlight, now);
        try
        {
            if (WebPartDelay > TimeSpan.Zero) await Task.Delay(WebPartDelay);
            await File.WriteAllBytesAsync(Path.Combine(u.Dir, seq.ToString(CultureInfo.InvariantCulture)), data);
        }
        finally { Interlocked.Decrement(ref _webPartsInFlight); }
        // The real response carries no errno field (reviewed source: "response not compatible with error interface").
        return new JsonObject { ["md5"] = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant(), ["partseq"] = seq.ToString(CultureInfo.InvariantCulture), ["uploadid"] = q["uploadid"] };
    }

    private JsonObject WebCreate(IReadOnlyDictionary<string, string> q, IReadOnlyDictionary<string, string> f)
    {
        var path = CheckWebPath(f.GetValueOrDefault("path"));
        if (f.GetValueOrDefault("isdir") == "1")
        {
            lock (_fsGate)
            {
                if (Exists(path)) throw new ApiError(-8, "The file already exists");
                Directory.CreateDirectory(DiskPath(path));
                return Ok(Entry(path));
            }
        }
        if (q.GetValueOrDefault("isdir") != "0") throw new ApiError(2, "isdir must be in the query for files");
        var rtype = int.TryParse(q.GetValueOrDefault("rtype"), out var r) ? r : 1;
        if (!_webUploads.TryRemove(f.GetValueOrDefault("uploadid") ?? "", out var u) || u.Path != path) throw new ApiError(31363, "upload id not found");
        try
        {
            var blocks = JsonSerializer.Deserialize<string[]>(f.GetValueOrDefault("block_list") ?? "[]") ?? [];
            var size = long.Parse(f.GetValueOrDefault("size") ?? "-1", CultureInfo.InvariantCulture);
            if (size != u.Size) throw new ApiError(31363, "size differs from precreate");
            if (UsedBytesAll() + size > QuotaTotal) throw new ApiError(-10, "Your space is insufficient");
            var tmp = Path.Combine(u.Dir, "assembled");
            using (var outFs = File.Create(tmp))
            {
                for (int i = 0; i < blocks.Length; i++)
                {
                    var part = Path.Combine(u.Dir, i.ToString(CultureInfo.InvariantCulture));
                    if (!File.Exists(part)) throw new ApiError(31363, $"shard {i} missing");
                    var bytes = File.ReadAllBytes(part);
                    if (!string.Equals(Convert.ToHexString(MD5.HashData(bytes)), blocks[i], StringComparison.OrdinalIgnoreCase)) throw new ApiError(31352, $"shard {i} md5 mismatch");
                    outFs.Write(bytes);
                }
                if (outFs.Length != size) throw new ApiError(31363, $"size mismatch ({outFs.Length} != {size})");
            }
            lock (_fsGate)
            {
                if (Exists(path))
                {
                    if (rtype == 0) throw new ApiError(-8, "The file already exists");
                    if (rtype is 1 or 2) path = Renamed(path);
                    else if (Directory.Exists(DiskPath(path))) throw new ApiError(-8, "a folder with that name exists");
                }
                var disk = DiskPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
                ReplaceFile(tmp, disk);
                var control = blocks.Length == 1 ? blocks[0].ToLowerInvariant() : Md5Hex(JsonSerializer.Serialize(blocks));
                var e = Entry(path);
                e["md5"] = ScrambleMd5(control);
                return Ok(e);
            }
        }
        finally { try { Directory.Delete(u.Dir, true); } catch (IOException) { } }
    }

    /// <summary>Refuse this many file-manager calls with errno 111 ("another task is running"), as the website does.</summary>
    public int WebFileManagerBusy;
    public int WebFileManagerBusyRefusals;

    private JsonObject WebFileManager(IReadOnlyDictionary<string, string> q, IReadOnlyDictionary<string, string> f)
    {
        if (Volatile.Read(ref WebFileManagerBusy) > 0 && Interlocked.Decrement(ref WebFileManagerBusy) >= 0)
        {
            Interlocked.Increment(ref WebFileManagerBusyRefusals);
            return new JsonObject { ["errno"] = 111, ["request_id"] = 1 };
        }
        var opera = q.GetValueOrDefault("opera") ?? "";
        // The website validates against the whole drive; reuse the official handler with the path check relaxed.
        var list = JsonNode.Parse(f.GetValueOrDefault("filelist") ?? "[]") as JsonArray ?? [];
        foreach (var item in list)
        {
            CheckWebPath(item is JsonValue ? item.GetValue<string>() : item!["path"]!.GetValue<string>());
            if (item is JsonObject o && o["dest"] is { } d) CheckWebPath(d.GetValue<string>());
        }
        var res = FileManagerCore(opera, f, webPaths: true);
        if (q.GetValueOrDefault("async") == "2")
        {
            var task = Interlocked.Increment(ref _nextTask);
            _taskResults[task] = res;
            return Ok(new JsonObject { ["taskid"] = task });
        }
        return res;
    }

    private long _nextTask = 7_000_000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, JsonObject> _taskResults = new();

    private JsonObject WebTask(IReadOnlyDictionary<string, string> q)
    {
        var id = long.TryParse(q.GetValueOrDefault("taskid"), out var t) ? t : 0;
        if (!_taskResults.TryGetValue(id, out var res)) throw new ApiError(2, "unknown task");
        if (_taskPolls.Increment(id) == 1) return Ok(new JsonObject { ["status"] = "running" }); // first poll: still running
        var list = new JsonArray();
        foreach (var i in res["info"] as JsonArray ?? []) list.Add(new JsonObject { ["errno"] = i!["errno"]!.GetValue<int>() });
        return Ok(new JsonObject { ["status"] = res["errno"]!.GetValue<int>() == 0 ? "success" : "failed", ["list"] = list });
    }

    private sealed class ConcurrentDictionaryCounter
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> _d = new();
        public int Increment(long key) => _d.AddOrUpdate(key, 1, (_, v) => v + 1);
    }

    /// <summary>Every non-file field of a multipart/form-data body.</summary>
    private static Dictionary<string, string> MultipartFields(byte[] body, string contentType)
    {
        var fields = new Dictionary<string, string>();
        var m = Regex.Match(contentType, "boundary=\"?([^\";]+)\"?");
        if (!m.Success) return fields;
        var text = Encoding.UTF8.GetString(body);
        foreach (var part in text.Split("--" + m.Groups[1].Value))
        {
            var headerEnd = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) continue;
            var nm = Regex.Match(part[..headerEnd], "name=\"?([^\";\r\n]+)\"?");
            if (!nm.Success || part[..headerEnd].Contains("filename", StringComparison.OrdinalIgnoreCase)) continue;
            var value = part[(headerEnd + 4)..];
            if (value.EndsWith("\r\n", StringComparison.Ordinal)) value = value[..^2];
            fields[nm.Groups[1].Value] = value;
        }
        return fields;
    }

    /// <summary>The website's MD5 scrambling, written from the forward direction (the client implements the reverse).</summary>
    public static string ScrambleMd5(string md5)
    {
        md5 = md5.ToLowerInvariant();
        var swapped = md5.Substring(8, 8) + md5[..8] + md5.Substring(24, 8) + md5.Substring(16, 8);
        var x = new StringBuilder(32);
        for (int i = 0; i < 32; i++) x.Append((Convert.ToInt32(swapped[i].ToString(), 16) ^ (i & 15)).ToString("x"));
        var s = x.ToString();
        return s[..9] + (char)('g' + Convert.ToInt32(s[9].ToString(), 16)) + s[10..];
    }

    private static string MockSign(string key, string data)
    {
        var s = new int[256];
        for (int i = 0; i < 256; i++) s[i] = i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 255;
            j = (j + s[i]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 255]);
        }
        return Convert.ToBase64String(output);
    }
}
