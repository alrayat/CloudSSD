using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CloudSSD.Core.Net;
using CloudSSD.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CloudSSD.TeraBox;

/// <summary>
/// Local WebDAV server (127.0.0.1 only, HTTP Basic auth with a random password) that exposes the TeraBox application
/// folder through the official Open Platform API. rclone mounts it with its webdav backend, so the existing CloudSSD
/// stack (WinFsp drive, VFS block cache, telemetry, prediction engine) works unchanged on top of TeraBox.
/// Reads use dlinks with HTTP Range; writes are staged to a local file and uploaded in 4 MB shards.
/// </summary>
public sealed partial class TeraBoxWebDavBridge : IAsyncDisposable
{
    private static readonly XNamespace D = "DAV:";
    private readonly ITeraBoxTransport _api;
    private readonly string _user, _pass, _stagingDir;
    private readonly ILogger _log;
    private readonly MiniHttpServer _http;
    private readonly Func<string, long> _freeSpaceBytes;
    private readonly SemaphoreSlim _stagingGate = new(4, 4);
    private readonly SemaphoreSlim _stagingSpaceGate = new(1, 1);
    private long _stagingReservedRemaining;
    private readonly ConcurrentDictionary<string, (IReadOnlyList<TeraBoxEntry> Entries, DateTime At)> _dirs = new(StringComparer.Ordinal);
    private readonly object _directoryCacheGate = new();
    private long _directoryCacheGeneration;
    private readonly ConcurrentDictionary<long, (string Url, DateTime At)> _links = new();
    private readonly ConcurrentDictionary<long, bool> _linkFailed = new();
    private long _resumes, _linkRenewals, _throttledReads, _rangeMismatches, _objectChanged, _stalls, _slowConnections;
    private (TeraBoxQuota Quota, DateTime At)? _quota;
    private TeraBoxUser? _userInfo;
    private long _reads, _readBytes, _uploads, _uploadBytes, _errors, _rangeEmulated;
    private volatile string? _lastError;

    public TeraBoxWebDavBridge(ITeraBoxTransport api, string user, string password, string stagingDir, ILogger log, int port = 0,
        Func<string, long>? freeSpaceBytes = null)
    {
        _api = api;
        _user = user;
        _pass = password;
        _stagingDir = stagingDir;
        _freeSpaceBytes = freeSpaceBytes ?? (path => Core.DiskInfo.FreeSpace(path).FreeForUser);
        _log = log;
        Directory.CreateDirectory(stagingDir);
        foreach (var stale in Directory.EnumerateFiles(stagingDir, "*.upload")) TryDelete(stale);
        _http = new MiniHttpServer(HandleAsync, port);
        _http.HandlerError += e => _log.LogError(e, "TeraBox bridge request failed");
        // Explorer asks for the drive size the moment the letter appears and keeps the answer until refreshed (measured:
        // a blank capacity tile). Fetch the quota right away so the first question already gets the real numbers.
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await QuotaAsync(timeout.Token);
                _userInfo = await _api.UserAsync(timeout.Token);
            }
            catch (Exception e) when (e is TeraBoxException or HttpRequestException or TaskCanceledException) { _log.LogWarning("Quota prefetch failed: {Error}", e.Message); }
        });
    }

    public int Port => _http.Port;
    public string Url => _http.BaseUrl + "/";
    public TimeSpan ListingTtl { get; set; } = TimeSpan.FromSeconds(20);
    public long StagingFreeSpaceReserveBytes { get; set; } = 512 * Core.ByteSize.MiB;
    public TimeSpan StagingAdmissionTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan LinkTtl { get; set; } = TimeSpan.FromMinutes(50);
    public VerifiedLocalSources? LocalSources { get; set; }
    public TimeSpan ReadProgressTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan ThrottleCooldown { get; set; } = TimeSpan.FromSeconds(30);
    private long _throttledUntilTicks;
    private readonly ConcurrentDictionary<string, long> _readCooldowns = new(StringComparer.OrdinalIgnoreCase);

    private bool ReadCoolingDown(string rel, out long until)
    {
        if (!_readCooldowns.TryGetValue(rel, out until)) return false;
        if (DateTime.UtcNow.Ticks < until) return true;
        _readCooldowns.TryRemove(new KeyValuePair<string, long>(rel, until));
        return false;
    }
    private bool ProviderThrottled => DateTime.UtcNow.Ticks < Interlocked.Read(ref _throttledUntilTicks);
    public bool? UpstreamRangeSupported { get; private set; }

    /// <summary>Raised when the owning app asks the bridge process to exit (POST /.cloudssd/shutdown).</summary>
    public event Action? ShutdownRequested;

    public object Status() => new
    {
        transport = _api.Kind, user = _userInfo?.Name, plan = _userInfo?.Plan, quotaTotal = _quota?.Quota.Total, quotaUsed = _quota?.Quota.Used,
        reads = Interlocked.Read(ref _reads), readBytes = Interlocked.Read(ref _readBytes), uploads = Interlocked.Read(ref _uploads),
        uploadBytes = Interlocked.Read(ref _uploadBytes), errors = Interlocked.Read(ref _errors), apiCalls = _api.Calls,
        tokenRefreshes = _api.Refreshes, throttled = _api.Throttled, rangeSupported = UpstreamRangeSupported,
        providerThrottled = ProviderThrottled,
        retryAfterSeconds = Math.Max(0, (Interlocked.Read(ref _throttledUntilTicks) - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerSecond),
        localSourceCount = LocalSources?.Count ?? 0, localSourceHits = LocalSources?.Hits ?? 0, localSourceBytes = LocalSources?.Bytes ?? 0,
        rangeEmulated = Interlocked.Read(ref _rangeEmulated), rangeMismatches = Interlocked.Read(ref _rangeMismatches),
        resumes = Interlocked.Read(ref _resumes), linkRenewals = Interlocked.Read(ref _linkRenewals),
        throttledReads = Interlocked.Read(ref _throttledReads), objectChanged = Interlocked.Read(ref _objectChanged),
        stalls = Interlocked.Read(ref _stalls), slowConnections = Interlocked.Read(ref _slowConnections), zeroFiles = Interlocked.Read(ref _zeroFiles),
        zeroBytes = Interlocked.Read(ref _zeroBytes), supersededUploads = Interlocked.Read(ref _supersededUploads), joinedUploads = Interlocked.Read(ref _joinedUploads), reusedUploads = Interlocked.Read(ref _reusedUploads), protectedDeletes = Interlocked.Read(ref _protectedDeletes), lastError = _lastError
    };

    // ------------------------------------------------------------------ dispatch

    private async Task HandleAsync(HttpContext ctx)
    {
        if (!Authorized(ctx.Header("Authorization")))
        {
            await ctx.RespondAsync(401, headers: new Dictionary<string, string> { ["WWW-Authenticate"] = "Basic realm=\"CloudSSD TeraBox\"" });
            return;
        }
        var rel = ctx.Path.Trim('/');
        if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("{Method} /{Path} depth={Depth} range={Range}", ctx.Method, rel, ctx.Header("Depth"), ctx.Header("Range"));
        try
        {
            if (rel.StartsWith(".cloudssd", StringComparison.Ordinal)) { await ControlAsync(ctx, rel); return; }
            switch (ctx.Method)
            {
                case "OPTIONS":
                    await ctx.RespondAsync(200, headers: new Dictionary<string, string> { ["DAV"] = "1", ["Allow"] = "OPTIONS, PROPFIND, GET, HEAD, PUT, DELETE, MKCOL, MOVE, COPY" });
                    break;
                case "PROPFIND": await PropFindAsync(ctx, rel); break;
                case "GET":
                case "HEAD": await GetAsync(ctx, rel); break;
                case "PUT": await PutAsync(ctx, rel); break;
                case "MKCOL": await MkcolAsync(ctx, rel); break;
                case "DELETE": await DeleteAsync(ctx, rel); break;
                case "MOVE":
                case "COPY": await MoveCopyAsync(ctx, rel, ctx.Method == "COPY"); break;
                default: await ctx.RespondAsync(405); break;
            }
        }
        catch (TeraBoxException e)
        {
            Interlocked.Increment(ref _errors);
            _lastError = e.Message;
            _log.LogWarning("TeraBox {Method} {Path}: {Error}", ctx.Method, rel, e.Message);
            if (!ctx.ResponseStarted) await ctx.RespondTextAsync(MapStatus(e), e.Message);
        }
        catch (IOException e) when (!ctx.ResponseStarted)
        {
            // Upstream data failed validation (wrong range, object changed, truncated beyond retries): an error, never bytes.
            Interlocked.Increment(ref _errors);
            _lastError = e.Message;
            _log.LogWarning("TeraBox {Method} {Path}: {Error}", ctx.Method, rel, e.Message);
            await ctx.RespondTextAsync(502, e.Message);
        }
        catch (HttpRequestException e)
        {
            Interlocked.Increment(ref _errors);
            _lastError = "Network: " + e.Message;
            _log.LogWarning("TeraBox {Method} {Path}: network error {Error}", ctx.Method, rel, e.Message);
            if (!ctx.ResponseStarted) await ctx.RespondTextAsync(503, "TeraBox unreachable: " + e.Message);
            else throw new IOException("upstream failed", e);
        }
    }

    private static int MapStatus(TeraBoxException e) => e switch
    {
        { IsNotFound: true } => 404,
        { IsExists: true } => 412,
        { IsThrottle: true } => 429,
        { IsRefreshDead: true } => 403,
        { IsAuth: true } => 503,
        { Errno: 31208 or 58 } => 413,   // larger than the plan allows (31208 assumed for the official API; 58 per the web source)
        { Errno: TeraBoxWebApi.EmptyFileUnsupported } => 501,
        { Errno: -10 or -32 } => 507,  // web: "Your space is insufficient"
        { Errno: 31112 } => 507,   // assumed: storage full
        { Errno: 31064 or -7 } => 403,
        _ => 502
    };

    private bool Authorized(string? header)
    {
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var given = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())));
            return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(_user + ":" + _pass));
        }
        catch (FormatException) { return false; }
    }

    private async Task ControlAsync(HttpContext ctx, string rel)
    {
        if (rel == ".cloudssd/health") { await ctx.RespondTextAsync(200, "ok"); return; }
        if (rel == ".cloudssd/shutdown" && ctx.Method == "POST") { await ctx.RespondTextAsync(200, "bye"); ShutdownRequested?.Invoke(); return; }
        if (rel == ".cloudssd/status")
        {
            // Diagnostics must remain available while the provider is stalled.
            await ctx.RespondJsonAsync(JsonSerializer.Serialize(Status()));
            return;
        }
        await ctx.RespondAsync(404);
    }

    // ------------------------------------------------------------------ metadata

    private static string Parent(string rel) => rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
    private static string Name(string rel) => rel[(rel.LastIndexOf('/') + 1)..];

    /// <summary>The folder as the client sees it: zero-file markers appear as the files they stand for.</summary>
    private async Task<IReadOnlyList<TeraBoxEntry>> ListAsync(string relDir, CancellationToken ct) => Present(await RawListAsync(relDir, ct));

    /// <summary>The folder as stored in TeraBox.</summary>
    private async Task<IReadOnlyList<TeraBoxEntry>> RawListAsync(string relDir, CancellationToken ct)
    {
        long generation;
        lock (_directoryCacheGate)
        {
            if (_dirs.TryGetValue(relDir, out var c) && DateTime.UtcNow - c.At < ListingTtl) return c.Entries;
            generation = _directoryCacheGeneration;
        }
        IReadOnlyList<TeraBoxEntry> entries;
        try { entries = await _api.ListAsync(_api.Abs(relDir), ct); }
        catch (TeraBoxException e) when (e.IsNotFound && relDir.Length == 0)
        {
            await _api.MkdirAsync(_api.AppRoot, ct); // first use: create the application folder
            entries = [];
        }
        // A listing may have begun before an acknowledged upload/move/delete.
        // It must not repopulate an invalidated directory with that old snapshot.
        lock (_directoryCacheGate)
            if (generation == _directoryCacheGeneration) _dirs[relDir] = (entries, DateTime.UtcNow);
        return entries;
    }

    /// <summary>null = does not exist; root is always a directory.</summary>
    private async Task<TeraBoxEntry?> StatAsync(string rel, CancellationToken ct)
    {
        if (rel.Length == 0) return new TeraBoxEntry(0, _api.AppRoot, "", 0, true, DateTimeOffset.UnixEpoch, null);
        IReadOnlyList<TeraBoxEntry> parent;
        try { parent = await ListAsync(Parent(rel), ct); }
        catch (TeraBoxException e) when (e.IsNotFound) { return null; } // parent missing: so is the entry (MKCOL/PUT then answer 409)
        return parent.FirstOrDefault(e => e.Name == Name(rel));
    }

    private void Invalidate(string rel)
    {
        lock (_directoryCacheGate)
        {
            _directoryCacheGeneration++;
            _dirs.TryRemove(Parent(rel), out _);
            foreach (var k in _dirs.Keys.Where(k => k == rel || k.StartsWith(rel + "/", StringComparison.Ordinal)).ToList()) _dirs.TryRemove(k, out _);
        }
        if (_quota is { } q) _quota = (q.Quota, DateTime.MinValue); // stale: refetch next time, but keep it as a fallback
    }

    private async Task<TeraBoxQuota> QuotaAsync(CancellationToken ct)
    {
        if (_quota is { } q && DateTime.UtcNow - q.At < TimeSpan.FromSeconds(60)) return q.Quota;
        var fresh = await _api.QuotaAsync(ct);
        _quota = (fresh, DateTime.UtcNow);
        return fresh;
    }

    private async Task PropFindAsync(HttpContext ctx, string rel)
    {
        var depth = ctx.Header("Depth") ?? "1";
        await ctx.DrainBodyAsync();
        var self = await StatAsync(rel, ctx.Aborted);
        if (self is null) { await ctx.RespondAsync(404); return; }
        var ms = new XElement(D + "multistatus", new XAttribute(XNamespace.Xmlns + "d", D));
        ms.Add(await ResponseFor(rel, self, rel.Length == 0, ctx.Aborted));
        if (self.IsDir && depth != "0")
            foreach (var e in await ListAsync(rel, ctx.Aborted))
                ms.Add(await ResponseFor(rel.Length == 0 ? e.Name : rel + "/" + e.Name, e, false, ctx.Aborted));
        if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("PROPFIND /{Path}: {Count} entries", rel, ms.Elements().Count());
        var xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + ms.ToString(SaveOptions.DisableFormatting));
        await ctx.RespondAsync(207, xml, "application/xml; charset=utf-8");
    }

    private async Task<XElement> ResponseFor(string rel, TeraBoxEntry e, bool isRoot, CancellationToken ct)
    {
        var href = "/" + string.Join('/', rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + (e.IsDir && rel.Length > 0 ? "/" : "");
        var prop = new XElement(D + "prop",
            new XElement(D + "displayname", e.Name),
            new XElement(D + "resourcetype", e.IsDir ? new XElement(D + "collection") : null),
            new XElement(D + "getlastmodified", e.Modified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)));
        if (!e.IsDir)
        {
            prop.Add(new XElement(D + "getcontentlength", e.Size));
            prop.Add(new XElement(D + "getetag", $"\"{e.FsId:x}-{e.Size:x}-{e.Modified.ToUnixTimeSeconds():x}\""));
        }
        if (isRoot)
        {
            TeraBoxQuota? q = null;
            try { q = await QuotaAsync(ct); }
            catch (Exception ex) when (ex is TeraBoxException or HttpRequestException)
            {
                _lastError = "Quota unavailable: " + ex.Message;
                q = _quota?.Quota; // last known numbers (at most minutes old) rather than a blank capacity in Explorer
            }
            if (q != null) prop.Add(new XElement(D + "quota-available-bytes", Math.Max(0, q.Total - q.Used)), new XElement(D + "quota-used-bytes", q.Used));
        }
        return new XElement(D + "response", new XElement(D + "href", href),
            new XElement(D + "propstat", prop, new XElement(D + "status", "HTTP/1.1 200 OK")));
    }

    // ------------------------------------------------------------------ reads

    private async Task<string> LinkAsync(TeraBoxEntry e, bool fresh, CancellationToken ct)
    {
        if (!fresh && _links.TryGetValue(e.FsId, out var l) && DateTime.UtcNow - l.At < LinkTtl) return l.Url;
        var url = await _api.DownloadUrlAsync(e, ct);
        _links[e.FsId] = (url, DateTime.UtcNow);
        return url;
    }

    private async Task GetAsync(HttpContext ctx, string rel)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ctx.Aborted);
        budget.CancelAfter(ReadProgressTimeout);
        try { await GetCoreAsync(ctx, rel, budget); }
        catch (Exception ex) when (ex is ProviderSlowException ||
                                   ex is OperationCanceledException && !ctx.Aborted.IsCancellationRequested)
        {
            var until = DateTime.UtcNow.Add(ThrottleCooldown).Ticks;
            _readCooldowns[rel] = until;
            Interlocked.Exchange(ref _throttledUntilTicks, until);
            Interlocked.Increment(ref _throttledReads);
            _lastError = "A cloud file read is too slow or timed out. That file is cooling down; other files remain available. Uncached games may need local preparation.";
            _log.LogWarning("TeraBox read suspended for {Seconds}s: {Path}", ThrottleCooldown.TotalSeconds, rel);
            if (ctx.ResponseStarted) throw new IOException(_lastError, ex);
            await ctx.RespondAsync(503, headers: new Dictionary<string, string>
            {
                ["Retry-After"] = Math.Ceiling(ThrottleCooldown.TotalSeconds).ToString(CultureInfo.InvariantCulture)
            });
        }
    }

    private sealed class ProviderSlowException : Exception;

    private async Task GetCoreAsync(HttpContext ctx, string rel, CancellationTokenSource budget)
    {
        var ct = budget.Token;
        var e = await StatAsync(rel, ct);
        if (e is null) { await ctx.RespondAsync(404); return; }
        if (e.IsDir) { await ctx.RespondAsync(405); return; }
        var range = ByteRange.Parse(ctx.Header("Range"), e.Size);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/octet-stream", ["Accept-Ranges"] = "bytes",
            ["Last-Modified"] = e.Modified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)
        };
        if (range is { Length: <= 0 })
        {
            headers["Content-Range"] = $"bytes */{e.Size}";
            await ctx.RespondAsync(416, headers: headers);
            return;
        }
        var (start, len) = range is { } r ? (r.Start, r.Length) : (0L, e.Size);
        headers["Content-Length"] = len.ToString(CultureInfo.InvariantCulture);
        if (range != null) headers["Content-Range"] = $"bytes {start}-{start + len - 1}/{e.Size}";
        if (ctx.Method == "HEAD" || len == 0) { await ctx.WriteHeadAsync(range != null ? 206 : 200, headers); return; }
        if (IsZeroFile(e))
        {
            await ctx.WriteHeadAsync(range != null ? 206 : 200, headers);
            Interlocked.Increment(ref _reads);
            var zeros = new byte[128 * 1024];
            for (long left = len; left > 0;)
            {
                var n = (int)Math.Min(zeros.Length, left);
                await ctx.ResponseStream.WriteAsync(zeros.AsMemory(0, n), ct);
                left -= n;
            }
            return;
        }

        if (ctx.Header("X-CloudSSD-Cloud-Only") != "1" && LocalSources is { } sources)
        {
            await using var local = await sources.TryOpenAsync(rel, e, ct);
            if (local != null)
            {
                local.Position += start;
                await ctx.WriteHeadAsync(range != null ? 206 : 200, headers);
                Interlocked.Increment(ref _reads);
                var buffer = new byte[128 * 1024];
                for (long left = len; left > 0;)
                {
                    var n = await local.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
                    if (n == 0) throw new EndOfStreamException("Verified local source ended early.");
                    await ctx.ResponseStream.WriteAsync(buffer.AsMemory(0, n), ct);
                    sources.RecordBytes(n);
                    Interlocked.Add(ref _readBytes, n);
                    left -= n;
                    budget.CancelAfter(ReadProgressTimeout);
                }
                return;
            }
        }
        if (ReadCoolingDown(rel, out var cooldownUntil))
        {
            await ctx.RespondAsync(503, headers: new Dictionary<string, string>
            {
                ["Retry-After"] = Math.Max(1, (cooldownUntil - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture)
            });
            return;
        }

        // Every upstream response is checked against what was asked for before a byte is passed on. A body that ends early is
        // resumed from the exact offset reached (headers are already sent then); failures never turn into invented bytes:
        // the client gets an I/O error instead.
        long sent = 0;
        var headSent = false;
        var connectFailures = 0;
        var slowAttempts = 0;
        var buf = new byte[128 * 1024];
        for (int attempt = 0; ; attempt++)
        {
            var from = start + sent;
            var to = start + len - 1;
            var wantRange = range != null || sent > 0;
            HttpResponseMessage resp;
            try
            {
                resp = await OpenUpstreamAsync(e, wantRange ? from : null, wantRange ? to : null, attempt, ct);
            }
            catch (HttpRequestException) when (++connectFailures < MaxConnectFailures && attempt < MaxReadAttempts && !ctx.Aborted.IsCancellationRequested)
            {
                // Could not reach TeraBox at all. Retry briefly, then fail: while offline, rclone and the game must get an
                // I/O error promptly instead of waiting through many reconnects (resumes of a started transfer are separate).
                await Task.Delay(ReadBackoff(attempt), ct);
                continue;
            }
            using (resp)
            {
                long skip;
                try { skip = CheckUpstream(resp, e, wantRange, from, to, rel); }
                catch (TransientUpstreamException) when (attempt < MaxReadAttempts)
                {
                    await Task.Delay(ReadBackoff(attempt), ct);
                    continue;
                }
                if (!headSent)
                {
                    await ctx.WriteHeadAsync(range != null ? 206 : 200, headers);
                    headSent = true;
                    Interlocked.Increment(ref _reads);
                }
                try
                {
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    // Measured on a free account: a long download connection can keep delivering a trickle (~14 KB/s)
                    // instead of failing. A connection that stalls or stays below the floor is replaced by a fresh one that
                    // continues at the exact offset reached.
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(StallTimeout);
                    var window = System.Diagnostics.Stopwatch.StartNew();
                    long windowBytes = 0;
                    while (sent < len)
                    {
                        int n;
                        try { n = await src.ReadAsync(buf, stall.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref _stalls);
                            throw new IOException($"no data from TeraBox for {StallTimeout.TotalSeconds:0} s");
                        }
                        if (n == 0) throw new EndOfStreamException();
                        stall.CancelAfter(StallTimeout);
                        windowBytes += n;
                        if (window.Elapsed >= SlowWindow)
                        {
                            if (windowBytes < SlowFloorBytesPerSecond * window.Elapsed.TotalSeconds && len - sent > windowBytes)
                            {
                                Interlocked.Increment(ref _slowConnections);
                                if (++slowAttempts >= 2) throw new ProviderSlowException();
                                throw new IOException($"TeraBox connection slowed to {windowBytes / window.Elapsed.TotalSeconds / 1024:0} KB/s");
                            }
                            // Healthy long downloads are allowed; trickles and repeated resumes cannot extend the deadline.
                            budget.CancelAfter(ReadProgressTimeout);
                            window.Restart();
                            windowBytes = 0;
                        }
                        var offset = 0;
                        if (skip > 0)
                        {
                            var drop = (int)Math.Min(skip, n);
                            skip -= drop;
                            offset = drop;
                            if (offset == n) continue;
                        }
                        var take = (int)Math.Min(n - offset, len - sent);
                        try { await ctx.ResponseStream.WriteAsync(buf.AsMemory(offset, take), ct); }
                        catch (Exception wx) when (wx is IOException or ObjectDisposedException) { throw new ClientDisconnectedException(wx); }
                        sent += take;
                        Interlocked.Add(ref _readBytes, take);
                    }
                    return;
                }
                catch (Exception ex) when (ex is EndOfStreamException or IOException or HttpRequestException && !ctx.Aborted.IsCancellationRequested
                                           && attempt < MaxReadAttempts)
                {
                    Interlocked.Increment(ref _resumes);
                    _log.LogWarning("TeraBox download of {Path} interrupted after {Sent} of {Len} bytes; resuming from offset {Offset}", rel, sent, len, start + sent);
                    await Task.Delay(ReadBackoff(attempt), ct);
                }
                catch (EndOfStreamException)
                {
                    throw new IOException($"TeraBox download of {rel} ended early ({sent} of {len} bytes) after {attempt + 1} attempts");
                }
            }
        }
    }

    // ------------------------------------------------------------------ zero files
    //
    // Installers such as Steam first "reserve space": they create every file at full size, filled with zeros, and write the
    // real bytes later. Measured 2026-10-20 with GTA V Enhanced (~94 GiB): uploading those placeholders cost as much as the
    // game itself at ~3.4 MB/s, and refusing them (the first fix) made rclone retry whole files over and over. A file that
    // is entirely zeros is now stored as a tiny marker object "<name>.cloudssdzero-<size>" and served back as exactly that many
    // zero bytes, without touching TeraBox. Nothing is lost: zeros are the file's real content. When real bytes arrive, the
    // file is uploaded normally and the marker removed. If both exist (an interrupted replace), the newer one wins.

    /// <summary>Store files of only zeros (at least <see cref="ZeroFileThreshold"/> bytes) as markers.</summary>
    public static bool StoreZeroFilesAsMarkers { get; set; } = true;
    public static long ZeroFileThreshold { get; set; } = 4L * 1024 * 1024;
    public const string ZeroMarkerTag = ".cloudssdzero-";
    private readonly ConcurrentDictionary<long, string> _zeroMarkers = new(); // FsId of a marker object -> its stored name
    private long _zeroFiles, _zeroBytes;

    public static bool TryParseMarker(string name, out string logical, out long size)
    {
        logical = "";
        size = 0;
        var i = name.LastIndexOf(ZeroMarkerTag, StringComparison.Ordinal);
        if (i <= 0 || !long.TryParse(name.AsSpan(i + ZeroMarkerTag.Length), NumberStyles.None, CultureInfo.InvariantCulture, out size)) return false;
        logical = name[..i];
        return true;
    }

    private IReadOnlyList<TeraBoxEntry> Present(IReadOnlyList<TeraBoxEntry> raw)
    {
        if (!raw.Any(e => !e.IsDir && e.Name.Contains(ZeroMarkerTag, StringComparison.Ordinal))) return raw;
        var byName = new Dictionary<string, TeraBoxEntry>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var e in raw)
        {
            var shown = e;
            if (!e.IsDir && TryParseMarker(e.Name, out var logical, out var size))
            {
                _zeroMarkers[e.FsId] = e.Name;
                shown = e with { Name = logical, Size = size, Path = e.Path[..^(e.Name.Length - logical.Length)], Md5 = null };
            }
            if (byName.TryGetValue(shown.Name, out var other))
            {
                if (shown.Modified > other.Modified) byName[shown.Name] = shown;
            }
            else { byName[shown.Name] = shown; order.Add(shown.Name); }
        }
        return order.Select(n => byName[n]).ToList();
    }

    private bool IsZeroFile(TeraBoxEntry e) => !e.IsDir && _zeroMarkers.ContainsKey(e.FsId);

    /// <summary>Where <paramref name="rel"/> (as the client names it) is actually stored.</summary>
    private string StoredRel(string rel, TeraBoxEntry e) =>
        IsZeroFile(e) ? Join(Parent(rel), _zeroMarkers[e.FsId]) : rel;

    private static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    private static async Task<bool> IsAllZeroAsync(string file, CancellationToken ct)
    {
        var buf = new byte[1 << 20];
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
            if (buf.AsSpan(0, n).IndexOfAnyExcept((byte)0) >= 0) return false;
        return true;
    }

    private const int MaxReadAttempts = 8;
    private const int MaxConnectFailures = 2;

    /// <summary>No byte for this long ends the connection (it is resumed elsewhere).</summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Below this rate over <see cref="SlowWindow"/>, a connection is replaced (32 KB/s over 15 s by default).</summary>
    public static long SlowFloorBytesPerSecond { get; set; } = 32 * 1024;
    public static TimeSpan SlowWindow { get; set; } = TimeSpan.FromSeconds(15);

    private static TimeSpan ReadBackoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(8000, 400 * Math.Pow(2, attempt)) * (0.75 + Random.Shared.NextDouble() * 0.5));

    /// <summary>Writing to our own client (rclone) failed: it went away, so there is nothing to retry upstream.</summary>
    private sealed class ClientDisconnectedException(Exception inner) : Exception("the client closed the connection", inner);

    private sealed class TransientUpstreamException(string message) : Exception(message);

    /// <summary>Opens the download, renewing an expired link once (403/404) and backing off on throttling and server errors.</summary>
    private async Task<HttpResponseMessage> OpenUpstreamAsync(TeraBoxEntry e, long? from, long? to, int attempt, CancellationToken ct)
    {
        var url = await LinkAsync(e, fresh: attempt > 0 && _linkFailed.ContainsKey(e.FsId), ct);
        var resp = await _api.OpenDownloadAsync(url, from, to, ct);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone && !_linkFailed.ContainsKey(e.FsId))
        {
            resp.Dispose();
            _linkFailed[e.FsId] = true;
            Interlocked.Increment(ref _linkRenewals);
            url = await LinkAsync(e, fresh: true, ct);
            resp = await _api.OpenDownloadAsync(url, from, to, ct);
        }
        if (resp.IsSuccessStatusCode) _linkFailed.TryRemove(e.FsId, out _);
        return resp;
    }

    /// <summary>
    /// Validates an upstream download response for [from, to] of <paramref name="e"/> and returns how many leading bytes
    /// must be discarded (only when the server ignored Range and sent the whole object). Status, Content-Range and
    /// Content-Length are all checked; Accept-Ranges alone proves nothing. A different total size means the object changed
    /// in the cloud: the cached listing is dropped and the read fails rather than mixing versions.
    /// </summary>
    private long CheckUpstream(HttpResponseMessage resp, TeraBoxEntry e, bool wantRange, long from, long to, string rel)
    {
        var code = (int)resp.StatusCode;
        if (code is 429 or >= 500)
        {
            if (code == 429) Interlocked.Increment(ref _throttledReads);
            throw new TransientUpstreamException($"HTTP {code}");
        }
        if (code == 416) { ObjectChanged(rel, e, "the server rejected the byte range (object may have shrunk)"); }
        if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            throw new HttpRequestException($"download returned HTTP {code}", null, resp.StatusCode);
        var length = resp.Content.Headers.ContentLength;
        if (resp.StatusCode == HttpStatusCode.PartialContent)
        {
            if (wantRange) UpstreamRangeSupported = true;
            var cr = resp.Content.Headers.ContentRange;
            if (cr is not { HasRange: true, From: { } f, To: { } t } || !string.Equals(cr.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _rangeMismatches);
                throw new IOException($"TeraBox answered a range request for {rel} without a valid Content-Range; refusing the data.");
            }
            if (cr.HasLength && cr.Length != e.Size) ObjectChanged(rel, e, $"size is now {cr.Length} bytes, expected {e.Size}");
            if (f != from || t < to || (length is { } l && l != t - f + 1))
            {
                Interlocked.Increment(ref _rangeMismatches);
                throw new IOException($"TeraBox returned bytes {f}-{t} (length {length}) for a request of {from}-{to} of {rel}; refusing the data.");
            }
            return 0;
        }
        // 200 OK: the whole object.
        if (length is { } total && total != e.Size) ObjectChanged(rel, e, $"size is now {total} bytes, expected {e.Size}");
        if (!wantRange) return 0;
        UpstreamRangeSupported = false;
        if (Interlocked.Increment(ref _rangeEmulated) == 1)
            _log.LogWarning("TeraBox download links ignore HTTP Range; random reads are emulated by discarding leading bytes and will be slow");
        return from;
    }

    private void ObjectChanged(string rel, TeraBoxEntry e, string detail)
    {
        Interlocked.Increment(ref _objectChanged);
        Invalidate(rel);
        _links.TryRemove(e.FsId, out _);
        throw new IOException($"{rel} changed in TeraBox while it was being read ({detail}); the read was stopped instead of mixing versions.");
    }

    // ------------------------------------------------------------------ writes

    // rclone gives up on a PUT after its I/O timeout (5 min, fixed while the drive is mounted) and sends the part again under
    // a new chunker transaction name. With 1 GiB parts and TeraBox taking ~10 MB/s in total, several parts at once never
    // finished inside 5 minutes: every attempt was abandoned and restarted, so nothing completed (measured 2026-10-20,
    // 09:25-09:55). Now:
    // - at most MaxConcurrentUploads parts go to TeraBox at a time, so each one finishes sooner;
    // - a retry with the same content (MD5 of the body) joins the upload already running instead of starting over, and the
    //   completed object is copied to each acknowledged transaction name (rclone can still finalize either request);
    // - a retry with different content (the file changed) cancels the older upload.
    private sealed class InFlightUpload(string hash, CancellationTokenSource cts)
    {
        public string Hash { get; } = hash;
        public CancellationTokenSource Cts { get; } = cts;
        public TaskCompletionSource<int> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Finalizing;
    }

    private readonly ConcurrentDictionary<string, InFlightUpload> _uploading = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _uploadGate = new(MaxConcurrentUploads, MaxConcurrentUploads);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    public static int MaxConcurrentUploads { get; set; } = 3;
    private long _supersededUploads, _joinedUploads, _reusedUploads;

    /// <summary>Finished uploads by target, kept a while: a retry that arrives after the upload finished (its client gave up
    /// meanwhile) gets the stored object renamed to its name instead of uploading the same bytes again.</summary>
    private sealed record FinishedUpload(string Hash, string Dir, string StoredName, bool Zero, long Size, DateTime At);
    private readonly ConcurrentDictionary<string, FinishedUpload> _finished = new(StringComparer.Ordinal);
    public static TimeSpan FinishedUploadMemory { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>The target an upload is for: chunker's temporary transaction suffix ("..rclone_chunk.001_6zjkj9") removed.</summary>
    public static string UploadKey(string rel) => ChunkTransaction().Replace(rel, "$1");

    [System.Text.RegularExpressions.GeneratedRegex(@"^(.*\.rclone_chunk\.\d{3,})_[0-9a-z]{4,}$")]
    private static partial System.Text.RegularExpressions.Regex ChunkTransaction();

    private async Task PutAsync(HttpContext ctx, string rel)
    {
        if (rel.Length == 0) { await ctx.RespondAsync(405); return; }
        if (!await _stagingGate.WaitAsync(StagingAdmissionTimeout, ctx.Aborted))
        {
            await ctx.RespondAsync(503, Encoding.UTF8.GetBytes("Upload staging is busy; the cached write is retained for retry."),
                "text/plain; charset=utf-8", headers: new Dictionary<string, string> { ["Retry-After"] = "15" });
            return;
        }
        var key = UploadKey(rel);
        var staging = Path.Combine(_stagingDir, Guid.NewGuid().ToString("N") + ".upload");
        long reservedRemaining = 0;
        try
        {
            var length = long.TryParse(ctx.Header("Content-Length"), out var declared) && declared > 0 ? declared : 0;
            bool admitted;
            await _stagingSpaceGate.WaitAsync(ctx.Aborted);
            try
            {
                admitted = HasStagingSpace(length);
                if (admitted) { _stagingReservedRemaining += length; reservedRemaining = length; }
            }
            finally { _stagingSpaceGate.Release(); }
            if (!admitted)
            {
                await ctx.RespondTextAsync(503, "Local cache space is reserved; the cached write is retained for retry.");
                return;
            }
            string hash;
            var outOfSpace = false;
            using (var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
            {
                await using var fs = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
                var buf = new byte[1 << 20];
                int n;
                while ((n = await ctx.Body.ReadAsync(buf, ctx.Aborted)) > 0)
                {
                    // Reserve future bytes across all requests. Serialize only each local write and its
                    // accounting, so another request cannot count the same free space during that write.
                    await _stagingSpaceGate.WaitAsync(ctx.Aborted);
                    try
                    {
                        var extra = Math.Max(0, n - reservedRemaining);
                        if (!HasStagingSpace(extra)) { outOfSpace = true; break; }
                        _stagingReservedRemaining += extra; reservedRemaining += extra;
                        await fs.WriteAsync(buf.AsMemory(0, n), ctx.Aborted);
                        _stagingReservedRemaining -= n; reservedRemaining -= n;
                    }
                    finally { _stagingSpaceGate.Release(); }
                    md5.AppendData(buf, 0, n);
                }
                hash = Convert.ToHexString(md5.GetHashAndReset());
            }
            if (outOfSpace)
            {
                // The partial copy is removed before answering, so a retry never finds it.
                TryDelete(staging);
                await ctx.RespondTextAsync(503, "Local cache space is reserved; the cached write is retained for retry.");
                return;
            }
            var parent = await StatAsync(Parent(rel), ctx.Aborted);
            if (parent is null) { await ctx.RespondTextAsync(409, "parent folder does not exist"); return; }
            if (parent is { IsDir: false }) { await ctx.RespondTextAsync(409, "parent is a file"); return; }

            while (true)
            {
                if (_uploading.TryGetValue(key, out var cur))
                {
                    if (cur.Hash == hash)
                    {
                        bool joined;
                        lock (cur) { joined = !cur.Finalizing; }
                        if (joined)
                        {
                            Interlocked.Increment(ref _joinedUploads);
                            _log.LogInformation("TeraBox upload of {Path}: same content is already uploading; waiting for it", rel);
                        }
                        // Wait, then copy the finished object to this attempt's name. A success response must
                        // never name an object that only exists under another request's transaction suffix.
                        try { await cur.Done.Task.WaitAsync(ctx.Aborted); }
                        catch (Exception e) when (e is not OperationCanceledException || !ctx.Aborted.IsCancellationRequested) { }
                        _uploading.TryRemove(new KeyValuePair<string, InFlightUpload>(key, cur));
                        continue;
                    }
                    Interlocked.Increment(ref _supersededUploads);
                    _log.LogInformation("TeraBox upload of {Path}: the file changed; stopping the older upload", key);
                    try { cur.Cts.Cancel(); } catch (ObjectDisposedException) { }
                    _uploading.TryRemove(new KeyValuePair<string, InFlightUpload>(key, cur));
                    continue;
                }
                if (_finished.TryGetValue(key, out var done) && done.Hash == hash && DateTime.UtcNow - done.At < FinishedUploadMemory
                    && done.Dir == Parent(rel))
                {
                    var newName = done.Zero ? Name(rel) + ZeroMarkerTag + done.Size.ToString(CultureInfo.InvariantCulture) : Name(rel);
                    await _mutationGate.WaitAsync(ctx.Aborted);
                    try
                    {
                        // Re-read after taking the gate: the first client may have finalized its transaction meanwhile.
                        if (_finished.TryGetValue(key, out var latest) && latest.Hash == hash) done = latest;
                        if (newName != done.StoredName) await _api.CopyAsync(_api.Abs(Join(done.Dir, done.StoredName)), _api.Abs(Parent(rel)), newName, ctx.Aborted);
                        else if (await _api.StatAsync(_api.Abs(Join(done.Dir, done.StoredName)), ctx.Aborted) == null) throw new TeraBoxException(-9, "Completed upload is no longer present.");
                        Invalidate(rel);
                        Interlocked.Increment(ref _reusedUploads);
                        _log.LogInformation("TeraBox upload of {Path}: reused completed content for this transaction", rel);
                        await ctx.RespondAsync(201);
                        return;
                    }
                    catch (TeraBoxException e) when (e.IsNotFound || e.IsExists) { } // changed externally: upload normally
                    finally { _mutationGate.Release(); }
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Aborted);
                var mine = new InFlightUpload(hash, cts);
                if (!_uploading.TryAdd(key, mine)) continue;
                try
                {
                    var code = await UploadStagedAsync(rel, staging, mine);
                    mine.Done.TrySetResult(code);
                    try { await ctx.RespondAsync(code); } catch (IOException) { } // this attempt's client may be long gone
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested && !ctx.Aborted.IsCancellationRequested)
                {
                    mine.Done.TrySetCanceled();
                    if (!ctx.ResponseStarted) await ctx.RespondTextAsync(409, "superseded by a newer version of the same file");
                }
                catch (Exception e)
                {
                    mine.Done.TrySetException(e);
                    throw;
                }
                finally { _uploading.TryRemove(new KeyValuePair<string, InFlightUpload>(key, mine)); }
                return;
            }
        }
        finally
        {
            TryDelete(staging);
            await _stagingSpaceGate.WaitAsync();
            try { _stagingReservedRemaining -= reservedRemaining; }
            finally { _stagingSpaceGate.Release(); _stagingGate.Release(); }
        }
    }

    private bool HasStagingSpace(long bytes)
    {
        var available = _freeSpaceBytes(_stagingDir) - Math.Max(0, StagingFreeSpaceReserveBytes);
        return available >= 0 && bytes >= 0 && bytes <= available && _stagingReservedRemaining <= available - bytes;
    }

    /// <summary>Uploads a staged body (zeros as a marker), removes older versions, and renames it to the newest attempt's name.</summary>
    private async Task<int> UploadStagedAsync(string rel, string staging, InFlightUpload u)
    {
        var ct = u.Cts.Token;
        await _uploadGate.WaitAsync(ct);
        try
        {
            var dir = Parent(rel);
            var size = new FileInfo(staging).Length;
            var name = Name(rel);
            // Everything stored under this name now: the real file and/or zero markers (any size).
            var stored = (await RawListAsync(dir, ct))
                .Where(x => !x.IsDir && (x.Name == name || TryParseMarker(x.Name, out var l, out _) && l == name)).ToList();
            var existed = stored.Count > 0;
            // TeraBox's web uploader cannot create empty files. Steam uses them for staging/state,
            // so represent them with the same durable marker format as large zero reservations.
            var zero = StoreZeroFilesAsMarkers && (size == 0 || size >= ZeroFileThreshold && await IsAllZeroAsync(staging, ct));
            string Target(string n) => zero ? n + ZeroMarkerTag + size.ToString(CultureInfo.InvariantCulture) : n;
            if (zero)
            {
                await File.WriteAllTextAsync(staging, $"CloudSSD: this file stands for {size} zero bytes named {name}.\n", ct);
                Interlocked.Increment(ref _zeroFiles);
                Interlocked.Add(ref _zeroBytes, size);
                _log.LogInformation("TeraBox upload {Path} ({Size} of zeros) stored as a marker", rel, Core.ByteSize.Format(size));
            }
            else _log.LogInformation("TeraBox upload {Path} ({Size})", rel, Core.ByteSize.Format(size));
            var target = Target(name);
            await _api.UploadAsync(staging, _api.Abs(Join(dir, target)), overwrite: true, null, ct);
            Interlocked.Increment(ref _uploads);
            Interlocked.Add(ref _uploadBytes, new FileInfo(staging).Length);
            // The new version is stored; only now remove the old ones (a crash in between leaves both, and the newer wins).
            foreach (var old in stored.Where(x => x.Name != target))
                await _api.DeleteAsync(_api.Abs(Join(dir, old.Name)), ct);
            lock (u) { u.Finalizing = true; }
            var storedAs = target;
            _finished[UploadKey(rel)] = new FinishedUpload(u.Hash, dir, storedAs, zero, size, DateTime.UtcNow);
            foreach (var stale in _finished.Where(kv => DateTime.UtcNow - kv.Value.At > FinishedUploadMemory).Select(kv => kv.Key).ToList())
                _finished.TryRemove(stale, out _);
            Invalidate(rel);
            _links.Clear(); // an overwritten file must not be read through the old file's link
            return existed ? 204 : 201;
        }
        finally { _uploadGate.Release(); }
    }

    private async Task MkcolAsync(HttpContext ctx, string rel)
    {
        await ctx.DrainBodyAsync();
        if (rel.Length == 0 || await StatAsync(rel, ctx.Aborted) != null) { await ctx.RespondAsync(405); return; }
        if (await StatAsync(Parent(rel), ctx.Aborted) is not { IsDir: true }) { await ctx.RespondAsync(409); return; }
        await _api.MkdirAsync(_api.Abs(rel), ctx.Aborted);
        Invalidate(rel);
        await ctx.RespondAsync(201);
    }

    /// <summary>
    /// Optional text file listing folder prefixes (one per line, relative to the drive root) in which files must not be
    /// deleted. Used to protect a game download in progress: Steam, after a restart that left it without the game's depot
    /// list, decided an 87 % download was "no changes" and began deleting it (2026-10-20 12:51). Refused deletes answer 403.
    /// </summary>
    public string? DeleteProtectionFile { get; set; }
    /// <summary>When set, protected cleanup is archived here instead of failing a store's update with 403.</summary>
    public string? ProtectedDeleteRecoveryRoot { get; set; }
    /// <summary>Durable local mapping written before an archive move, so a lost response cannot orphan saved files.</summary>
    public string? ProtectedDeleteJournalDirectory { get; set; }
    private (DateTime At, string[] Prefixes) _protect = (DateTime.MinValue, []);
    private long _protectedDeletes;

    private bool IsDeleteProtected(string rel)
    {
        if (DeleteProtectionFile is null) return false;
        // rclone's own temporary upload parts ("..rclone_chunk.001_6zjkj9") are never protected: refusing them made every
        // upload in the folder fail ("Failed to remove temporary chunk", 2026-10-20 16:42).
        if (UploadKey(rel) != rel) return false;
        if (DateTime.UtcNow - _protect.At > TimeSpan.FromSeconds(5))
        {
            string[] list = [];
            try
            {
                if (File.Exists(DeleteProtectionFile))
                    list = File.ReadAllLines(DeleteProtectionFile).Select(l => l.Trim().Replace('\\', '/').Trim('/')).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
            }
            catch (IOException) { list = _protect.Prefixes; }
            _protect = (DateTime.UtcNow, list);
        }
        return _protect.Prefixes.Any(p => rel == p || rel.StartsWith(p + "/", StringComparison.Ordinal) || p.StartsWith(rel + "/", StringComparison.Ordinal));
    }

    private async Task ArchiveProtectedAsync(string rel, TeraBoxEntry entry, CancellationToken ct)
    {
        var root = ProtectedDeleteRecoveryRoot!.Trim('/');
        if (root.Length == 0 || root.Split('/').Any(p => p is "." or "..")) throw new IOException("Invalid protected-file recovery folder.");
        if (await _api.StatAsync(_api.Abs(root), ct) == null) await _api.MkdirAsync(_api.Abs(root), ct);
        var folder = root + "/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        var stored = StoredRel(rel, entry);
        var recovery = folder + "/" + Name(stored);
        var record = new ProtectedArchiveRecord(rel, stored, recovery, entry.FsId, entry.Size, entry.IsDir, "prepared", DateTime.UtcNow);
        await SaveProtectedArchiveRecordAsync(record, ct);
        await _api.MkdirAsync(_api.Abs(folder), ct);
        await _api.MoveAsync(_api.Abs(stored), _api.Abs(folder), Name(stored), ct);
        // Even if the client disconnected after the server accepted the move, keep its recovery mapping durable.
        await SaveProtectedArchiveRecordAsync(record with { State = "archived", RecordedUtc = DateTime.UtcNow }, CancellationToken.None);
        _log.LogWarning("Protected cleanup archived {Original} at {Recovery}", rel, recovery);
        Invalidate(root);
    }

    private sealed record ProtectedArchiveRecord(string Original, string StoredOriginal, string Recovery, long EntryId,
        long Size, bool IsDirectory, string State, DateTime RecordedUtc);

    private async Task SaveProtectedArchiveRecordAsync(ProtectedArchiveRecord record, CancellationToken ct)
    {
        if (ProtectedDeleteJournalDirectory is not { } directory) return;
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.Recovery))).ToLowerInvariant();
        var final = Path.Combine(directory, key + ".json");
        var temp = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, record, cancellationToken: ct);
                file.Flush(true);
            }
            File.Move(temp, final, true);
        }
        finally { TryDelete(temp); }
    }

    private async Task DeleteAsync(HttpContext ctx, string rel)
    {
        if (rel.Length == 0) { await ctx.RespondAsync(403); return; }
        if (await StatAsync(rel, ctx.Aborted) is not { } e) { await ctx.RespondAsync(404); return; }
        if (IsDeleteProtected(rel))
        {
            Interlocked.Increment(ref _protectedDeletes);
            if (ProtectedDeleteRecoveryRoot != null) await ArchiveProtectedAsync(rel, e, ctx.Aborted);
            else
            {
                _log.LogWarning("Refused to delete {Path}: the folder is protected ({File})", rel, DeleteProtectionFile);
                await ctx.RespondTextAsync(403, "this folder is protected from deletion");
                return;
            }
        }
        else await _api.DeleteAsync(_api.Abs(StoredRel(rel, e)), ctx.Aborted);
        Invalidate(rel);
        _links.Clear();
        await ctx.RespondAsync(204);
    }

    private async Task MoveCopyAsync(HttpContext ctx, string rel, bool copy)
    {
        await _mutationGate.WaitAsync(ctx.Aborted);
        try { await MoveCopyCoreAsync(ctx, rel, copy); }
        finally { _mutationGate.Release(); }
    }

    private async Task MoveCopyCoreAsync(HttpContext ctx, string rel, bool copy)
    {
        var destHeader = ctx.Header("Destination");
        if (rel.Length == 0 || destHeader is null) { await ctx.RespondAsync(400); return; }
        var dest = Uri.UnescapeDataString(Uri.TryCreate(destHeader, UriKind.Absolute, out var u) ? u.AbsolutePath : destHeader).Trim('/');
        if (dest.Length == 0 || dest == rel) { await ctx.RespondAsync(403); return; }
        if (await StatAsync(rel, ctx.Aborted) is not { } src) { await ctx.RespondAsync(404); return; }
        if (await StatAsync(Parent(dest), ctx.Aborted) is not { IsDir: true }) { await ctx.RespondAsync(409); return; }
        var existing = await StatAsync(dest, ctx.Aborted);
        if (existing != null)
        {
            if (string.Equals(ctx.Header("Overwrite"), "F", StringComparison.OrdinalIgnoreCase)) { await ctx.RespondAsync(412); return; }
            if (IsDeleteProtected(dest))
            {
                if (ProtectedDeleteRecoveryRoot == null) { await ctx.RespondAsync(403); return; }
                await ArchiveProtectedAsync(dest, existing, ctx.Aborted);
            }
            else await _api.DeleteAsync(_api.Abs(StoredRel(dest, existing)), ctx.Aborted);
        }
        // A zero file moves as its marker, renamed to match the destination.
        var from = StoredRel(rel, src);
        var toName = IsZeroFile(src) ? Name(dest) + ZeroMarkerTag + src.Size.ToString(CultureInfo.InvariantCulture) : Name(dest);
        if (copy) await _api.CopyAsync(_api.Abs(from), _api.Abs(Parent(dest)), toName, ctx.Aborted);
        else if (Parent(rel) == Parent(dest)) await _api.RenameAsync(_api.Abs(from), toName, ctx.Aborted);
        else await _api.MoveAsync(_api.Abs(from), _api.Abs(Parent(dest)), toName, ctx.Aborted);
        if (!copy && _finished.TryGetValue(UploadKey(rel), out var finished) && finished.Dir == Parent(rel) && finished.StoredName == Name(from))
            _finished[UploadKey(rel)] = finished with { Dir = Parent(dest), StoredName = toName };
        Invalidate(rel);
        Invalidate(dest);
        _links.Clear();
        await ctx.RespondAsync(existing != null ? 204 : 201);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public ValueTask DisposeAsync() => _http.DisposeAsync();
}
