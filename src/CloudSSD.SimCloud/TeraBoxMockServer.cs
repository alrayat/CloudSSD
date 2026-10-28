using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudSSD.Core.Net;

namespace CloudSSD.SimCloud;

/// <summary>
/// Local stand-in for the TeraBox Open Platform, implemented from the official integration document
/// (https://www.terabox.com/integrations/docs, retrieved 2026-10-12): device-code OAuth with signed token requests,
/// /oauth/tokeninfo domains, uinfo, quota, precreate + superfile2 shard upload + create, filemanager, list, filemetas,
/// download (dlink) and the app-folder restriction. It lets CloudSSD's TeraBox client and drive be tested end to end
/// without TeraBox developer credentials. Behaviour the document does not specify (error codes on file APIs for expired
/// tokens, folder creation through create isdir=1, Range support on dlinks) is configurable and marked as assumptions.
/// </summary>
public sealed partial class TeraBoxMockServer : IAsyncDisposable
{
    public const long MiB = 1024 * 1024;

    private readonly MiniHttpServer _http;
    private readonly ConcurrentDictionary<string, DeviceGrant> _devices = new();
    private readonly ConcurrentDictionary<string, Token> _access = new();
    private readonly ConcurrentDictionary<string, Token> _refresh = new();
    private readonly ConcurrentDictionary<string, Upload> _uploads = new();
    private readonly ConcurrentDictionary<string, DlinkGrant> _dlinks = new();
    private readonly ConcurrentDictionary<string, long> _ids = new(StringComparer.Ordinal);
    private readonly object _fsGate = new();
    private readonly TokenBucket _bucket = new();
    private long _nextId = 900_000_000_000;
    private long _apiCalls, _downloadBytes, _rangeRequests, _fullRequests;

    public TeraBoxMockServer(string dataRoot, string stateRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        StateRoot = Path.GetFullPath(stateRoot);
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(StateRoot);
        _http = new MiniHttpServer(HandleAsync);
    }

    public string DataRoot { get; }
    public string StateRoot { get; }
    public int Port => _http.Port;
    public string Domain => $"127.0.0.1:{_http.Port}";
    public string BaseUrl => _http.BaseUrl;

    public string ClientId { get; init; } = "cloudssdTestClientId0001";
    public string ClientSecret { get; init; } = "cloudssdTestClientSecret0001";
    public string PrivateSecret { get; init; } = "cloudssdTestPrivateSecret0001";
    /// <summary>The per-application space all file paths must stay inside (document section 3.1, item 1.3).</summary>
    public string AppRoot { get; init; } = "/From: Other Applications/CloudSSD";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromDays(2);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan DlinkLifetime { get; set; } = TimeSpan.FromHours(1);
    public int DevicePollInterval { get; set; } = 1;
    /// <summary>Device codes are approved automatically after this many polls (0 = only via <see cref="ApproveAll"/>).</summary>
    public int AutoApproveAfterPolls { get; set; } = 1;

    public int VipType { get; set; }
    public long QuotaTotal { get; set; } = 1024L * 1024 * MiB;
    /// <summary>Per-file size limit enforced by create; null derives it from <see cref="VipType"/> (4 GB free, 128 GB premium as reported by TeraBox).</summary>
    public long? MaxFileSize { get; set; }
    public long EffectiveMaxFileSize => MaxFileSize ?? (VipType == 0 ? 4L * 1024 * MiB : 128L * 1024 * MiB);

    /// <summary>Assumption knob: whether dlink downloads honour HTTP Range (not stated in the document).</summary>
    public bool DlinkSupportsRange { get; set; } = true;
    public double DownloadMbps { get => _bucket.BytesPerSecond * 8 / 1e6; set => _bucket.BytesPerSecond = value <= 0 ? 0 : value * 1e6 / 8; }
    public TimeSpan Latency { get; set; }
    public bool Offline { get => _http.Offline; set => _http.Offline = value; }
    /// <summary>Probability that an API call answers errno 31034 (frequency control) - used to test backoff.</summary>
    public double ThrottleRate { get; set; }
    /// <summary>Deterministic one-shot failures for retry tests.</summary>
    public int ThrottleNextCalls;

    private bool ShouldThrottle()
    {
        while (true)
        {
            var remaining = Volatile.Read(ref ThrottleNextCalls);
            if (remaining <= 0) break;
            if (Interlocked.CompareExchange(ref ThrottleNextCalls, remaining - 1, remaining) == remaining) return true;
        }
        return ThrottleRate > 0 && Random.Shared.NextDouble() < ThrottleRate;
    }

    public long ApiCalls => Interlocked.Read(ref _apiCalls);
    public long DownloadBytes => Interlocked.Read(ref _downloadBytes);
    public long RangeRequests => Interlocked.Read(ref _rangeRequests);
    public long FullRequests => Interlocked.Read(ref _fullRequests);
    public int RefreshCount;

    public void ApproveAll() { foreach (var d in _devices.Values) d.Approved = true; }

    /// <summary>Expires every access token immediately (simulates the 2-day lifetime passing).</summary>
    public void ExpireAccessTokens() { foreach (var t in _access.Values) t.Expires = DateTime.UtcNow.AddSeconds(-1); }

    public string DiskPath(string teraPath)
    {
        var segs = teraPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(EncodeSegment);
        return Path.Combine([DataRoot, .. segs]);
    }

    private static string EncodeSegment(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
            sb.Append(ch is ':' or '<' or '>' or '"' or '|' or '?' or '*' or '\\' or '%' ? $"%{(int)ch:X2}" : ch.ToString());
        return sb.ToString();
    }

    private static string DecodeSegment(string s) => Uri.UnescapeDataString(s);

    // ------------------------------------------------------------------ dispatch

    private async Task HandleAsync(HttpContext ctx)
    {
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ctx.Aborted);
        var p = ctx.Path;
        if (p.StartsWith("/file/", StringComparison.Ordinal)) { await DownloadAsync(ctx); return; }
        if (IsWebRoute(ctx)) { await WebAsync(ctx); return; }
        Interlocked.Increment(ref _apiCalls);
        JsonObject result;
        try
        {
            result = p switch
            {
                "/oauth/devicecode" => DeviceCode(ctx),
                "/oauth/gettoken" => GetToken(await ctx.ReadFormAsync()),
                "/oauth/tokeninfo" => TokenInfo(await ctx.ReadFormAsync()),
                "/oauth/refreshtoken" => Refresh(await ctx.ReadFormAsync()),
                _ => await OpenApiAsync(ctx, p)
            };
        }
        catch (ApiError e) { result = Err(e.Errno, e.Message); }
        result["request_id"] = Random.Shared.NextInt64(1_000_000_000_000, 9_999_999_999_999);
        result["server_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await ctx.RespondJsonAsync(result.ToJsonString());
    }

    private async Task<JsonObject> OpenApiAsync(HttpContext ctx, string p)
    {
        var token = Authenticate(ctx.Query.GetValueOrDefault("access_tokens"));
        if (ShouldThrottle()) throw new ApiError(31034, "hit frequency control");
        return p switch
        {
            "/openapi/uinfo" => UInfo(token),
            "/openapi/api/quota" => Quota(),
            "/openapi/api/precreate" => Precreate(await ctx.ReadFormAsync()),
            "/rest/2.0/pcs/superfile2" => await UploadPartAsync(ctx),
            "/openapi/api/create" => Create(await ctx.ReadFormAsync()),
            "/openapi/api/filemanager" => FileManager(ctx.Query.GetValueOrDefault("opera") ?? "", await ctx.ReadFormAsync()),
            "/openapi/api/list" => List(ctx.Query),
            "/openapi/api/filemetas" => FileMetas(ctx.Query),
            "/openapi/api/download" => Dlinks(ctx.Query),
            _ => throw new ApiError(3, "unknown method " + p)
        };
    }

    private static JsonObject Ok(JsonObject? o = null) { o ??= new JsonObject(); o["errno"] = 0; return o; }
    private static JsonObject Err(int errno, string msg) => new() { ["errno"] = errno, ["show_msg"] = msg, ["errmsg"] = msg };

    private sealed class ApiError(int errno, string message) : Exception(message) { public int Errno { get; } = errno; }

    // ------------------------------------------------------------------ OAuth

    private sealed class DeviceGrant { public required string Code; public DateTime Expires; public int Polls; public bool Approved; public bool Used; }
    private sealed class Token { public required string Value; public DateTime Expires; public bool Used; public long UserId = 127813130; }
    private sealed class DlinkGrant { public required string Path; public DateTime Expires; }

    private JsonObject DeviceCode(HttpContext ctx)
    {
        if (ctx.Query.GetValueOrDefault("client_id") != ClientId) throw new ApiError(100001, "The client_id parameter is invalid.");
        var g = new DeviceGrant { Code = RandomToken(10), Expires = DateTime.UtcNow.AddSeconds(300) };
        _devices[g.Code] = g;
        return Ok(new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["device_code"] = g.Code, ["expires_in"] = 300, ["interval"] = DevicePollInterval,
                ["qrcode_url"] = "data:image/png;base64," + TinyPngBase64, ["user_code"] = "", ["verification_url"] = ""
            }
        });
    }

    private void CheckSign(IReadOnlyDictionary<string, string> f)
    {
        foreach (var k in new[] { "client_id", "client_secret", "timestamp", "sign" })
            if (string.IsNullOrEmpty(f.GetValueOrDefault(k))) throw new ApiError(2, "Required parameters are missing.");
        if (f["client_id"] != ClientId || f["client_secret"] != ClientSecret) throw new ApiError(100001, "The client_id or client_secret parameter is invalid.");
        var expected = Md5Hex($"{ClientId}_{f["timestamp"]}_{ClientSecret}_{PrivateSecret}");
        if (!string.Equals(f["sign"], expected, StringComparison.OrdinalIgnoreCase)) throw new ApiError(100001, "invalid sign");
    }

    private JsonObject GetToken(IReadOnlyDictionary<string, string> f)
    {
        CheckSign(f);
        var grant = f.GetValueOrDefault("grant_type");
        if (grant != "device_code") throw new ApiError(200001, "Unsupported authorization type grant_type.");
        if (!_devices.TryGetValue(f.GetValueOrDefault("code") ?? "", out var g) || g.Used || g.Expires < DateTime.UtcNow)
            throw new ApiError(100002, "The code is invalid (invalid or expired code).");
        g.Polls++;
        if (!g.Approved && AutoApproveAfterPolls > 0 && g.Polls > AutoApproveAfterPolls) g.Approved = true;
        if (!g.Approved) throw new ApiError(400001, "The user has not yet completed the authorization operation for the device_code.");
        g.Used = true;
        return Ok(new JsonObject { ["data"] = IssueTokens() });
    }

    private JsonObject IssueTokens(DateTime? refreshExpires = null)
    {
        var a = new Token { Value = RandomToken(64), Expires = DateTime.UtcNow + AccessTokenLifetime };
        var r = new Token { Value = RandomToken(64), Expires = refreshExpires ?? DateTime.UtcNow + RefreshTokenLifetime };
        _access[a.Value] = a;
        _refresh[r.Value] = r;
        return new JsonObject { ["access_token"] = a.Value, ["refresh_token"] = r.Value, ["expires_in"] = (int)AccessTokenLifetime.TotalSeconds };
    }

    private JsonObject TokenInfo(IReadOnlyDictionary<string, string> f)
    {
        var t = Authenticate(f.GetValueOrDefault("access_token"), tokenInfoErrors: true);
        return Ok(new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["api_domain"] = Domain, ["upload_domain"] = Domain, ["client_id"] = ClientId,
                ["expires_in"] = (int)Math.Max(0, (t.Expires - DateTime.UtcNow).TotalSeconds),
                ["create_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["user_id"] = t.UserId
            }
        });
    }

    private JsonObject Refresh(IReadOnlyDictionary<string, string> f)
    {
        CheckSign(f);
        if (!_refresh.TryGetValue(f.GetValueOrDefault("refresh_token") ?? "", out var r) || r.Used) throw new ApiError(200004, "Invalid refresh_token.");
        if (r.Expires < DateTime.UtcNow) throw new ApiError(200005, "The refresh_token has expired.");
        r.Used = true; // each refresh_token works exactly once
        foreach (var old in _access.Values) if (old.Expires > DateTime.UtcNow.AddSeconds(15)) old.Expires = DateTime.UtcNow.AddSeconds(15);
        Interlocked.Increment(ref RefreshCount);
        return Ok(new JsonObject { ["data"] = IssueTokens(r.Expires) });
    }

    private Token Authenticate(string? value, bool tokenInfoErrors = false)
    {
        if (string.IsNullOrEmpty(value)) throw new ApiError(2, "Required parameters are missing.");
        // Assumption: file APIs report invalid/expired tokens with the same codes as /oauth/tokeninfo (200002/200003).
        if (!_access.TryGetValue(value, out var t)) throw new ApiError(200002, "Invalid access_token.");
        if (t.Expires < DateTime.UtcNow) throw new ApiError(200003, "The access_token has expired.");
        return t;
    }

    // ------------------------------------------------------------------ user / quota

    private JsonObject UInfo(Token t) => Ok(new JsonObject
    {
        ["uname"] = "test-user", ["avatar_url"] = "", ["vip_type"] = VipType, ["uk"] = t.UserId, ["use_type"] = 0, ["user_type"] = 1
    });

    private long UsedBytes()
    {
        lock (_fsGate)
            return Directory.EnumerateFiles(DataRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
    }

    private JsonObject Quota() => Ok(new JsonObject { ["total"] = QuotaTotal, ["used"] = UsedBytes() });

    // ------------------------------------------------------------------ paths

    private string CheckPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) throw new ApiError(-7, "Invalid file name");
        path = "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (path.Split('/').Any(s => s is "." or "..")) throw new ApiError(-7, "Invalid file name");
        if (!(path == AppRoot || path.StartsWith(AppRoot + "/", StringComparison.Ordinal)))
            throw new ApiError(31064, "file access is restricted to the application folder");
        return path;
    }

    private long IdFor(string path) => _ids.GetOrAdd(path, _ => Interlocked.Increment(ref _nextId));

    private void MoveIds(string from, string to)
    {
        foreach (var k in _ids.Keys.Where(k => k == from || k.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
            if (_ids.TryRemove(k, out var id)) _ids[to + k[from.Length..]] = id;
    }

    private string? PathForId(long id) => _ids.FirstOrDefault(kv => kv.Value == id).Key;

    private JsonObject Entry(string path)
    {
        var disk = DiskPath(path);
        var isDir = Directory.Exists(disk);
        FileSystemInfo fi = isDir ? new DirectoryInfo(disk) : new FileInfo(disk);
        var o = new JsonObject
        {
            ["fs_id"] = IdFor(path), ["path"] = path, ["server_filename"] = path[(path.LastIndexOf('/') + 1)..],
            ["size"] = isDir ? 0 : ((FileInfo)fi).Length, ["isdir"] = isDir ? 1 : 0, ["category"] = isDir ? 6 : 6,
            ["server_mtime"] = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
            ["server_ctime"] = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeSeconds(),
            ["local_mtime"] = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
            ["local_ctime"] = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeSeconds(),
        };
        if (!isDir) o["md5"] = Md5File(disk);
        return o;
    }

    private bool Exists(string path) { var d = DiskPath(path); return File.Exists(d) || Directory.Exists(d); }

    // ------------------------------------------------------------------ upload

    private sealed class Upload { public required string Id; public required string Path; public required string[] Blocks; public required string Dir; }

    private JsonObject Precreate(IReadOnlyDictionary<string, string> f)
    {
        var path = CheckPath(f.GetValueOrDefault("path"));
        if (f.GetValueOrDefault("autoinit") != "1") throw new ApiError(2, "autoinit must be 1");
        var blocks = JsonSerializer.Deserialize<string[]>(f.GetValueOrDefault("block_list") ?? "[]") ?? [];
        var u = new Upload { Id = "N1-" + RandomToken(40), Path = path, Blocks = blocks, Dir = Path.Combine(StateRoot, "uploads", RandomToken(12)) };
        Directory.CreateDirectory(u.Dir);
        _uploads[u.Id] = u;
        var need = new JsonArray();
        for (int i = 0; i < Math.Max(1, blocks.Length); i++) need.Add(i);
        return Ok(new JsonObject { ["path"] = path, ["uploadid"] = u.Id, ["return_type"] = 1, ["block_list"] = need });
    }

    private async Task<JsonObject> UploadPartAsync(HttpContext ctx)
    {
        var q = ctx.Query;
        if (q.GetValueOrDefault("method") != "upload" || q.GetValueOrDefault("app_id") != "250528") throw new ApiError(2, "method=upload and app_id=250528 are required");
        if (!_uploads.TryGetValue(q.GetValueOrDefault("uploadid") ?? "", out var u)) throw new ApiError(31363, "upload id not found");
        if (CheckPath(q.GetValueOrDefault("path")) != u.Path) throw new ApiError(31363, "path does not match upload id");
        if (!int.TryParse(q.GetValueOrDefault("partseq"), out var seq) || seq < 0 || seq >= Math.Max(1, u.Blocks.Length)) throw new ApiError(2, "invalid partseq");
        var body = await ctx.ReadBodyAsync(160 * MiB);
        var data = MultipartFile(body, ctx.Header("Content-Type") ?? "") ?? throw new ApiError(2, "multipart field 'file' is required");
        await File.WriteAllBytesAsync(Path.Combine(u.Dir, seq.ToString(CultureInfo.InvariantCulture)), data);
        return Ok(new JsonObject { ["md5"] = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant(), ["partseq"] = seq, ["uploadid"] = u.Id });
    }

    private JsonObject Create(IReadOnlyDictionary<string, string> f)
    {
        var path = CheckPath(f.GetValueOrDefault("path"));
        var rtype = int.TryParse(f.GetValueOrDefault("rtype"), out var r) ? r : 1;
        if (f.GetValueOrDefault("isdir") == "1")
        {
            // Assumption: folders are created through create with isdir=1 (the response documents isdir; the request table does not).
            lock (_fsGate)
            {
                if (Exists(path) && rtype == 0) throw new ApiError(-8, "The file already exists");
                Directory.CreateDirectory(DiskPath(path));
                return Ok(Entry(path));
            }
        }
        if (!_uploads.TryRemove(f.GetValueOrDefault("uploadid") ?? "", out var u) || u.Path != path) throw new ApiError(31363, "upload id not found");
        try
        {
            var blocks = JsonSerializer.Deserialize<string[]>(f.GetValueOrDefault("block_list") ?? "[]") ?? [];
            if (!blocks.SequenceEqual(u.Blocks, StringComparer.OrdinalIgnoreCase)) throw new ApiError(31352, "block_list differs from precreate");
            var size = long.Parse(f.GetValueOrDefault("size") ?? "-1", CultureInfo.InvariantCulture);
            if (size > EffectiveMaxFileSize) throw new ApiError(31208, $"file size exceeds the limit of your plan ({EffectiveMaxFileSize} bytes)");
            if (UsedBytes() + size > QuotaTotal) throw new ApiError(31112, "Insufficient storage space");
            var tmp = Path.Combine(u.Dir, "assembled");
            using (var outFs = File.Create(tmp))
            {
                for (int i = 0; i < Math.Max(1, blocks.Length); i++)
                {
                    var part = Path.Combine(u.Dir, i.ToString(CultureInfo.InvariantCulture));
                    if (!File.Exists(part)) throw new ApiError(31363, $"shard {i} missing");
                    var bytes = File.ReadAllBytes(part);
                    if (blocks.Length > 0 && !string.Equals(Convert.ToHexString(MD5.HashData(bytes)), blocks[i], StringComparison.OrdinalIgnoreCase))
                        throw new ApiError(31352, $"shard {i} md5 mismatch");
                    if (i < blocks.Length - 1 && bytes.Length < 4 * MiB) throw new ApiError(31363, "each shard except the last must be at least 4 MB");
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
                return Ok(Entry(path));
            }
        }
        finally { try { Directory.Delete(u.Dir, true); } catch (IOException) { } }
    }

    /// <summary>
    /// Puts a new version in place even while the old one is being downloaded: the open old file is renamed away first (the
    /// download keeps its handle and finishes with the old bytes), as a cloud store keeps serving an object being replaced.
    /// </summary>
    private void ReplaceFile(string tmp, string disk)
    {
        if (File.Exists(disk))
        {
            var tomb = Path.Combine(StateRoot, "replaced-" + Guid.NewGuid().ToString("N"));
            File.Move(disk, tomb);
            try { File.Delete(tomb); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        File.Move(tmp, disk);
    }

    private string Renamed(string path)
    {
        var dot = path.LastIndexOf('.');
        var slash = path.LastIndexOf('/');
        var (stem, ext) = dot > slash ? (path[..dot], path[dot..]) : (path, "");
        for (int i = 1; ; i++) { var p = $"{stem}({i}){ext}"; if (!Exists(p)) return p; }
    }

    private static byte[]? MultipartFile(byte[] body, string contentType)
    {
        var m = System.Text.RegularExpressions.Regex.Match(contentType, "boundary=\"?([^\";]+)\"?");
        if (!m.Success) return null;
        var delim = Encoding.ASCII.GetBytes("--" + m.Groups[1].Value);
        var span = body.AsSpan();
        var pos = span.IndexOf(delim);
        while (pos >= 0)
        {
            var start = pos + delim.Length;
            if (start + 2 > span.Length || span.Slice(start, 2).SequenceEqual("--"u8)) return null;
            var headerEnd = span[start..].IndexOf("\r\n\r\n"u8);
            if (headerEnd < 0) return null;
            var headers = Encoding.UTF8.GetString(span.Slice(start, headerEnd));
            var dataStart = start + headerEnd + 4;
            var next = span[dataStart..].IndexOf(delim);
            if (next < 0) return null;
            var dataEnd = dataStart + next - 2; // strip CRLF before delimiter
            if (System.Text.RegularExpressions.Regex.IsMatch(headers, "(?i)[; ]name=\"?file\"?(;|\r|\n|$)")) return span[dataStart..dataEnd].ToArray();
            pos = dataStart + next;
        }
        return null;
    }

    // ------------------------------------------------------------------ file manager / list / meta

    private JsonObject FileManager(string opera, IReadOnlyDictionary<string, string> f) => FileManagerCore(opera, f, webPaths: false);

    private JsonObject FileManagerCore(string opera, IReadOnlyDictionary<string, string> f, bool webPaths)
    {
        string Check(string? p) => webPaths ? CheckWebPath(p) : CheckPath(p);
        var list = JsonNode.Parse(f.GetValueOrDefault("filelist") ?? "[]") as JsonArray ?? [];
        var info = new JsonArray();
        var anyError = 0;
        lock (_fsGate)
        {
            foreach (var item in list)
            {
                var errno = 0;
                string? src = null;
                try
                {
                    switch (opera)
                    {
                        case "delete":
                        {
                            src = Check(item!.GetValue<string>());
                            var d = DiskPath(src);
                            if (File.Exists(d)) File.Delete(d);
                            else if (Directory.Exists(d)) Directory.Delete(d, true);
                            else throw new ApiError(-9, "The file doesn't exist");
                            break;
                        }
                        case "rename":
                        case "move":
                        case "copy":
                        {
                            src = Check(item!["path"]!.GetValue<string>());
                            var newName = item["newname"]?.GetValue<string>() ?? src[(src.LastIndexOf('/') + 1)..];
                            if (newName.Length == 0 || newName.Contains('/')) throw new ApiError(-7, "Invalid file name");
                            var destDir = opera == "rename" ? src[..src.LastIndexOf('/')] : Check(item["dest"]!.GetValue<string>());
                            var dst = Check(destDir + "/" + newName);
                            var ds = DiskPath(src);
                            var dd = DiskPath(dst);
                            if (!File.Exists(ds) && !Directory.Exists(ds)) throw new ApiError(-9, "The file doesn't exist");
                            if (File.Exists(dd) || Directory.Exists(dd)) throw new ApiError(-8, "The file already exists");
                            Directory.CreateDirectory(Path.GetDirectoryName(dd)!);
                            if (opera == "copy")
                            {
                                if (File.Exists(ds)) File.Copy(ds, dd);
                                else CopyDir(ds, dd);
                            }
                            else
                            {
                                if (File.Exists(ds)) File.Move(ds, dd);
                                else Directory.Move(ds, dd);
                                MoveIds(src, dst);
                            }
                            break;
                        }
                        default: throw new ApiError(2, "opera must be copy, move, rename or delete");
                    }
                }
                catch (ApiError e) { errno = e.Errno; anyError = e.Errno; }
                info.Add(new JsonObject { ["errno"] = errno, ["path"] = src ?? "" });
            }
        }
        var res = Ok(new JsonObject { ["info"] = info });
        if (anyError != 0) res["errno"] = 12; // batch partially failed; per-item errno tells which
        return res;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private JsonObject List(IReadOnlyDictionary<string, string> q, bool webPaths = false)
    {
        var dir = webPaths ? CheckWebPath(q.GetValueOrDefault("dir")) : CheckPath(q.GetValueOrDefault("dir"));
        var page = int.TryParse(q.GetValueOrDefault("page"), out var pg) ? Math.Max(1, pg) : 1;
        var num = int.TryParse(q.GetValueOrDefault("num"), out var n) ? Math.Clamp(n, 1, 10000) : 100;
        var disk = DiskPath(dir);
        if (!Directory.Exists(disk))
        {
            if (dir == AppRoot) Directory.CreateDirectory(disk); // the app folder exists once an app is authorized
            else throw new ApiError(-9, "The file doesn't exist");
        }
        IEnumerable<string> names;
        lock (_fsGate)
            names = Directory.EnumerateFileSystemEntries(disk).Select(e => DecodeSegment(Path.GetFileName(e))).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var arr = new JsonArray();
        foreach (var name in names.Skip((page - 1) * num).Take(num)) arr.Add(Entry(dir + "/" + name));
        return Ok(new JsonObject { ["list"] = arr, ["guid"] = 0, ["guid_info"] = "" });
    }

    private JsonObject FileMetas(IReadOnlyDictionary<string, string> q)
    {
        var targets = JsonSerializer.Deserialize<string[]>(q.GetValueOrDefault("target") ?? "[]") ?? [];
        var arr = new JsonArray();
        foreach (var t in targets)
        {
            var path = CheckPath(Uri.UnescapeDataString(t));
            if (!Exists(path)) throw new ApiError(-9, "The file doesn't exist");
            var e = Entry(path);
            if (q.GetValueOrDefault("dlink") == "1" && e["isdir"]!.GetValue<int>() == 0) e["dlink"] = NewDlink(path);
            arr.Add(e);
        }
        return Ok(new JsonObject { ["info"] = arr });
    }

    private JsonObject Dlinks(IReadOnlyDictionary<string, string> q)
    {
        if (q.GetValueOrDefault("type") != "dlink") throw new ApiError(2, "type must be dlink");
        var ids = JsonSerializer.Deserialize<long[]>(q.GetValueOrDefault("fidlist") ?? "[]") ?? [];
        var arr = new JsonArray();
        foreach (var id in ids)
        {
            var path = PathForId(id);
            if (path is null || !File.Exists(DiskPath(path))) throw new ApiError(-9, "The file doesn't exist");
            arr.Add(new JsonObject { ["fs_id"] = id.ToString(CultureInfo.InvariantCulture), ["dlink"] = NewDlink(path) });
        }
        return Ok(new JsonObject { ["dlink"] = arr });
    }

    private string NewDlink(string path)
    {
        var key = RandomToken(32);
        _dlinks[key] = new DlinkGrant { Path = path, Expires = DateTime.UtcNow + DlinkLifetime };
        return $"{BaseUrl}/file/{key}?fid={IdFor(path)}&rt=pr&expires=1h&chkv=1";
    }

    private async Task DownloadAsync(HttpContext ctx)
    {
        var key = ctx.Path["/file/".Length..];
        // As documented: the dlink only works with access_tokens appended (official), or with the website session cookie (web).
        if (!WebSessionValid(ctx))
        {
            try { Authenticate(ctx.Query.GetValueOrDefault("access_tokens")); }
            catch (ApiError) { await ctx.RespondTextAsync(403, "access denied"); return; }
        }
        if (!_dlinks.TryGetValue(key, out var g) || g.Expires < DateTime.UtcNow) { await ctx.RespondTextAsync(403, "link expired"); return; }
        var disk = DiskPath(g.Path);
        if (!File.Exists(disk)) { await ctx.RespondTextAsync(404, "not found"); return; }
        await using var fs = new FileStream(disk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        var size = fs.Length;
        var range = DlinkSupportsRange ? ByteRange.Parse(ctx.Header("Range"), size) : null;
        if (range is { Length: <= 0 })
        {
            await ctx.RespondAsync(416, headers: new Dictionary<string, string> { ["Content-Range"] = $"bytes */{size}" });
            return;
        }
        var (start, len) = range is { } r ? (r.Start, r.Length) : (0L, size);
        var h = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/octet-stream", ["Content-Length"] = len.ToString(CultureInfo.InvariantCulture),
            ["Accept-Ranges"] = DlinkSupportsRange ? "bytes" : "none"
        };
        if (range != null) { h["Content-Range"] = $"bytes {start}-{start + len - 1}/{size}"; Interlocked.Increment(ref _rangeRequests); }
        else Interlocked.Increment(ref _fullRequests);
        await ctx.WriteHeadAsync(range != null ? 206 : 200, h);
        if (ctx.Method == "HEAD") return;
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[64 * 1024];
        long sent = 0;
        while (sent < len)
        {
            if (Offline) throw new IOException("offline");
            var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, len - sent)), ctx.Aborted);
            if (n <= 0) break;
            await _bucket.TakeAsync(n, ctx.Aborted);
            await ctx.ResponseStream.WriteAsync(buf.AsMemory(0, n), ctx.Aborted);
            sent += n;
            Interlocked.Add(ref _downloadBytes, n);
        }
    }

    // ------------------------------------------------------------------ helpers

    public static string Md5Hex(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private readonly ConcurrentDictionary<(string, long, DateTime), string> _md5Cache = new();

    private string Md5File(string path)
    {
        var fi = new FileInfo(path);
        return _md5Cache.GetOrAdd((path, fi.Length, fi.LastWriteTimeUtc), _ => Md5Compute(path));
    }

    private static string Md5Compute(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
    }

    private static string RandomToken(int len)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(len, 0, (span, _) => { for (int i = 0; i < span.Length; i++) span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)]; });
    }

    // 1x1 transparent PNG used as the device-code QR image placeholder.
    private const string TinyPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    public ValueTask DisposeAsync() => _http.DisposeAsync();
}
