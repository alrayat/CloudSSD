using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CloudSSD.SimCloud;

/// <summary>
/// A local HTTP/1.1 file server that behaves like a slow, imperfect cloud provider. Used by integration tests and
/// benchmarks: rclone mounts it with its "http" backend, so the complete rclone VFS + WinFsp path is exercised under a
/// reproducible bandwidth, latency and failure profile. Bound to 127.0.0.1 only.
/// </summary>
public sealed class SimCloudServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly TokenBucket _bucket = new();
    private readonly ConcurrentBag<TcpClient> _clients = new();
    private readonly Random _rng;
    private readonly object _rngGate = new();

    public SimCloudServer(string root, int port = 0, int seed = 1234)
    {
        Root = Path.GetFullPath(root);
        _rng = new Random(seed);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string Root { get; }
    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/";

    /// <summary>Throughput limit in megabits per second (0 = unlimited). Shared by all connections like an access link.</summary>
    public double BandwidthMbps
    {
        get => _bucket.BytesPerSecond * 8 / 1_000_000.0;
        set => _bucket.BytesPerSecond = value <= 0 ? 0 : value * 1_000_000.0 / 8.0;
    }

    /// <summary>Added time-to-first-byte per request (simulates provider + internet round trips).</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;
    public TimeSpan LatencyJitter { get; set; } = TimeSpan.Zero;

    /// <summary>Probability that a request is answered with an HTTP error (503/429).</summary>
    public double FailureRate { get; set; }

    /// <summary>Probability that a body transfer is cut off part-way (connection reset).</summary>
    public double DropRate { get; set; }

    /// <summary>When true every connection is closed immediately (internet down).</summary>
    public bool Offline { get; set; }

    public long Requests => Interlocked.Read(ref _requests);
    public long RangeRequests => Interlocked.Read(ref _rangeRequests);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long FailuresInjected => Interlocked.Read(ref _failures);
    private long _requests, _rangeRequests, _bytesSent, _failures;

    public ConcurrentQueue<(string Path, long Start, long Length)> ServedRanges { get; } = new();

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _rangeRequests, 0);
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _failures, 0);
        ServedRanges.Clear();
    }

    private double NextDouble()
    {
        lock (_rngGate) return _rng.NextDouble();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            if (Offline)
            {
                client.Client.LingerState = new LingerOption(true, 0);
                client.Dispose();
                continue;
            }
            client.NoDelay = true;
            _clients.Add(client);
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);
        try
        {
            while (!_cts.IsCancellationRequested && !Offline)
            {
                var requestLine = await reader.ReadLineAsync(_cts.Token);
                if (string.IsNullOrEmpty(requestLine)) return;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cts.Token)))
                {
                    var idx = line.IndexOf(':');
                    if (idx > 0) headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }
                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;
                var keepAlive = !headers.TryGetValue("Connection", out var conn) || !conn.Equals("close", StringComparison.OrdinalIgnoreCase);
                var ok = await ServeAsync(stream, parts[0], parts[1], headers);
                if (!ok || !keepAlive) return;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task<bool> ServeAsync(NetworkStream stream, string method, string rawPath, Dictionary<string, string> headers)
    {
        Interlocked.Increment(ref _requests);
        var delay = Latency + TimeSpan.FromMilliseconds(LatencyJitter.TotalMilliseconds * NextDouble());
        if (delay > TimeSpan.Zero) await Task.Delay(delay, _cts.Token);
        if (Offline) return false;

        if (FailureRate > 0 && NextDouble() < FailureRate)
        {
            Interlocked.Increment(ref _failures);
            var code = NextDouble() < 0.5 ? "503 Service Unavailable" : "429 Too Many Requests";
            await WriteHeadAsync(stream, code, new() { ["Content-Length"] = "0", ["Retry-After"] = "1" });
            return true;
        }

        var rel = Uri.UnescapeDataString(rawPath.Split('?')[0]).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(Root, rel));
        if (!full.Equals(Root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        {
            await WriteHeadAsync(stream, "403 Forbidden", new() { ["Content-Length"] = "0" });
            return true;
        }

        if (Directory.Exists(full))
        {
            if (!rawPath.EndsWith('/'))
            {
                await WriteHeadAsync(stream, "301 Moved Permanently", new() { ["Location"] = rawPath + "/", ["Content-Length"] = "0" });
                return true;
            }
            var html = Listing(full, rawPath);
            var body = Encoding.UTF8.GetBytes(html);
            await WriteHeadAsync(stream, "200 OK", new() { ["Content-Type"] = "text/html; charset=utf-8", ["Content-Length"] = body.Length.ToString() });
            if (method != "HEAD") await stream.WriteAsync(body, _cts.Token);
            return true;
        }

        if (!File.Exists(full))
        {
            await WriteHeadAsync(stream, "404 Not Found", new() { ["Content-Length"] = "0" });
            return true;
        }

        var fi = new FileInfo(full);
        long start = 0, end = fi.Length - 1;
        bool partial = false;
        if (headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range[6..].Split(',')[0].Trim();
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                var a = spec[..dash];
                var b = spec[(dash + 1)..];
                if (a.Length == 0 && long.TryParse(b, out var suffix)) { start = Math.Max(0, fi.Length - suffix); }
                else
                {
                    start = long.Parse(a, CultureInfo.InvariantCulture);
                    if (b.Length > 0) end = Math.Min(end, long.Parse(b, CultureInfo.InvariantCulture));
                }
                partial = true;
            }
            if (start >= fi.Length && fi.Length > 0)
            {
                await WriteHeadAsync(stream, "416 Range Not Satisfiable", new() { ["Content-Range"] = $"bytes */{fi.Length}", ["Content-Length"] = "0" });
                return true;
            }
        }
        var length = Math.Max(0, end - start + 1);
        var h = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/octet-stream",
            ["Content-Length"] = length.ToString(CultureInfo.InvariantCulture),
            ["Accept-Ranges"] = "bytes",
            ["Last-Modified"] = fi.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture),
            ["ETag"] = $"\"{fi.Length:x}-{fi.LastWriteTimeUtc.Ticks:x}\""
        };
        if (partial) h["Content-Range"] = $"bytes {start}-{end}/{fi.Length}";
        await WriteHeadAsync(stream, partial ? "206 Partial Content" : "200 OK", h);
        if (method == "HEAD") return true;
        if (partial) Interlocked.Increment(ref _rangeRequests);
        ServedRanges.Enqueue((rel.Replace('\\', '/'), start, length));

        var dropAt = DropRate > 0 && NextDouble() < DropRate ? (long)(length * NextDouble()) : -1;
        await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[16 * 1024];
        long sent = 0;
        while (sent < length)
        {
            if (Offline) return false;
            var want = (int)Math.Min(buffer.Length, length - sent);
            var n = await fs.ReadAsync(buffer.AsMemory(0, want), _cts.Token);
            if (n <= 0) break;
            await _bucket.TakeAsync(n, _cts.Token);
            await stream.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
            sent += n;
            Interlocked.Add(ref _bytesSent, n);
            if (dropAt >= 0 && sent >= dropAt)
            {
                Interlocked.Increment(ref _failures);
                return false; // abort connection mid-body
            }
        }
        return true;
    }

    private static string Listing(string dir, string urlPath)
    {
        var sb = new StringBuilder("<!DOCTYPE html><html><body><ul>\n");
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
            sb.Append($"<li><a href=\"{Uri.EscapeDataString(Path.GetFileName(d))}/\">{WebUtility.HtmlEncode(Path.GetFileName(d))}/</a></li>\n");
        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
            sb.Append($"<li><a href=\"{Uri.EscapeDataString(Path.GetFileName(f))}\">{WebUtility.HtmlEncode(Path.GetFileName(f))}</a></li>\n");
        sb.Append("</ul></body></html>");
        return sb.ToString();
    }

    private async Task WriteHeadAsync(NetworkStream s, string status, Dictionary<string, string> headers)
    {
        var sb = new StringBuilder($"HTTP/1.1 {status}\r\nServer: CloudSSD-SimCloud\r\nDate: {DateTime.UtcNow:R}\r\n");
        foreach (var (k, v) in headers) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), _cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        foreach (var c in _clients) c.Dispose();
        try { await _acceptLoop; } catch (Exception) { }
        _cts.Dispose();
    }
}

/// <summary>Shared token bucket (bytes/second). A rate of 0 disables limiting.</summary>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private double _tokens;
    private long _lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();

    public double BytesPerSecond { get; set; }

    public async Task TakeAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var rate = BytesPerSecond;
                if (rate <= 0) return;
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var elapsed = (now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                _lastTicks = now;
                // Burst capacity of 50 ms worth of traffic keeps pacing smooth without starving small requests.
                _tokens = Math.Min(rate * 0.05 + bytes, _tokens + elapsed * rate);
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
                wait = TimeSpan.FromSeconds((bytes - _tokens) / rate);
            }
            await Task.Delay(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait, ct);
        }
    }
}
