using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CloudSSD.Core.Net;

/// <summary>
/// Minimal HTTP/1.1 server bound to 127.0.0.1 only. Supports keep-alive, Content-Length and chunked request bodies and
/// streamed responses. Used by the TeraBox WebDAV bridge (rclone talks to it) and by the TeraBox API mock in tests.
/// No TLS: it never listens on a network interface.
/// </summary>
public sealed class MiniHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<HttpContext, Task> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly Task _loop;

    public MiniHttpServer(Func<HttpContext, Task> handler, int port = 0)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.ExclusiveAddressUse = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>When true, new and existing connections are dropped (simulates a network outage in tests).</summary>
    public bool Offline { get; set; }

    public event Action<Exception>? HandlerError;

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            if (Offline)
            {
                c.Client.LingerState = new LingerOption(true, 0);
                c.Dispose();
                continue;
            }
            c.NoDelay = true;
            _clients[c] = 0;
            _ = Task.Run(() => ServeConnectionAsync(c));
        }
    }

    private async Task ServeConnectionAsync(TcpClient client)
    {
        try
        {
            using var _ = client;
            var conn = new HttpConnection(client.GetStream());
            while (!_cts.IsCancellationRequested && !Offline)
            {
                var ctx = await conn.ReadRequestAsync(_cts.Token);
                if (ctx is null || Offline) return; // outage: drop the connection without answering
                try { await _handler(ctx); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { return; }
                catch (Exception e)
                {
                    HandlerError?.Invoke(e);
                    if (!ctx.ResponseStarted) await ctx.RespondTextAsync(500, "internal error");
                    else return;
                }
                if (!ctx.ResponseStarted) await ctx.RespondAsync(204);
                if (!ctx.KeepAlive || !await ctx.DrainRequestBodyAsync()) return;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { _clients.TryRemove(client, out _); }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _listener.Stop();
        foreach (var c in _clients.Keys) c.Dispose();
        try { await _loop; } catch (Exception) { }
        _cts.Dispose();
    }
}

/// <summary>Buffered reader over a connection: header lines and body bytes share one buffer.</summary>
internal sealed class HttpConnection(NetworkStream stream)
{
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _pos, _len;

    public NetworkStream Stream { get; } = stream;

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_pos < _len) return true;
        _pos = 0;
        _len = await Stream.ReadAsync(_buf, ct);
        return _len > 0;
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (!await FillAsync(ct)) return sb.Length > 0 ? sb.ToString() : null;
            while (_pos < _len)
            {
                var b = _buf[_pos++];
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
                if (sb.Length > 16 * 1024) throw new IOException("header line too long");
            }
        }
    }

    public async Task<int> ReadAsync(Memory<byte> dest, CancellationToken ct)
    {
        if (!await FillAsync(ct)) return 0;
        var n = Math.Min(dest.Length, _len - _pos);
        _buf.AsMemory(_pos, n).CopyTo(dest);
        _pos += n;
        return n;
    }

    public async Task<HttpContext?> ReadRequestAsync(CancellationToken ct)
    {
        string? line;
        do { line = await ReadLineAsync(ct); } while (line is { Length: 0 });
        if (line is null) return null;
        var parts = line.Split(' ');
        if (parts.Length < 3) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrEmpty(line = await ReadLineAsync(ct)))
        {
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return new HttpContext(this, parts[0].ToUpperInvariant(), parts[1], headers, ct);
    }
}

public sealed class HttpContext
{
    private readonly HttpConnection _conn;
    private readonly CancellationToken _ct;
    private readonly Stream _body;

    internal HttpContext(HttpConnection conn, string method, string target, Dictionary<string, string> headers, CancellationToken ct)
    {
        _conn = conn;
        _ct = ct;
        Method = method;
        Target = target;
        Headers = headers;
        var q = target.IndexOf('?');
        RawPath = q >= 0 ? target[..q] : target;
        Path = Uri.UnescapeDataString(RawPath);
        Query = ParseQuery(q >= 0 ? target[(q + 1)..] : "");
        KeepAlive = !(headers.TryGetValue("Connection", out var c) && c.Equals("close", StringComparison.OrdinalIgnoreCase));
        if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            _body = new ChunkedBodyStream(conn, ct);
        else
            _body = new LengthBodyStream(conn, headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var n) ? n : 0, ct);
    }

    public string Method { get; }
    public string Target { get; }
    public string RawPath { get; }
    /// <summary>Percent-decoded path.</summary>
    public string Path { get; }
    public IReadOnlyDictionary<string, string> Query { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public bool KeepAlive { get; private set; }
    public bool ResponseStarted { get; private set; }
    public CancellationToken Aborted => _ct;

    /// <summary>Request body (bounded by Content-Length, or de-chunked).</summary>
    public Stream Body => _body;

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public async Task<byte[]> ReadBodyAsync(long max = 64 * 1024 * 1024)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await _body.ReadAsync(buf, _ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > max) throw new IOException("request body too large");
        }
        return ms.ToArray();
    }

    /// <summary>Parses an application/x-www-form-urlencoded body.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadFormAsync() => ParseQuery(Encoding.UTF8.GetString(await ReadBodyAsync()));

    /// <summary>Reads and discards the rest of the request body (needed before responding on keep-alive connections).</summary>
    public Task DrainBodyAsync() => DrainRequestBodyAsync();

    internal async Task<bool> DrainRequestBodyAsync()
    {
        try
        {
            var buf = new byte[16 * 1024];
            long drained = 0;
            while (await _body.ReadAsync(buf, _ct) is var n && n > 0)
                if ((drained += n) > 8 * 1024 * 1024) return false;
            return true;
        }
        catch (IOException) { return false; }
    }

    public async Task WriteHeadAsync(int status, IDictionary<string, string>? headers = null)
    {
        if (ResponseStarted) throw new InvalidOperationException("response already started");
        ResponseStarted = true;
        var h = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (!h.ContainsKey("Content-Length") && !h.ContainsKey("Transfer-Encoding")) KeepAlive = false;
        if (!KeepAlive) h["Connection"] = "close";
        var sb = new StringBuilder($"HTTP/1.1 {status} {Reason(status)}\r\nDate: {DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture)}\r\n");
        foreach (var (k, v) in h) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("\r\n");
        await _conn.Stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), _ct);
    }

    public Stream ResponseStream => _conn.Stream;

    public async Task RespondAsync(int status, byte[]? body = null, string? contentType = null, IDictionary<string, string>? headers = null)
    {
        var h = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Length"] = (body?.Length ?? 0).ToString(CultureInfo.InvariantCulture)
        };
        if (contentType != null) h["Content-Type"] = contentType;
        await WriteHeadAsync(status, h);
        if (body is { Length: > 0 } && Method != "HEAD") await _conn.Stream.WriteAsync(body, _ct);
    }

    public Task RespondTextAsync(int status, string text) => RespondAsync(status, Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8");

    public Task RespondJsonAsync(string json, int status = 200) => RespondAsync(status, Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8");

    public static IReadOnlyDictionary<string, string> ParseQuery(string q)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var k = Uri.UnescapeDataString((i < 0 ? pair : pair[..i]).Replace('+', ' '));
            var v = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
            d[k] = v;
        }
        return d;
    }

    private static string Reason(int s) => s switch
    {
        200 => "OK", 201 => "Created", 204 => "No Content", 206 => "Partial Content", 207 => "Multi-Status",
        301 => "Moved Permanently", 304 => "Not Modified", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", 412 => "Precondition Failed", 413 => "Payload Too Large",
        416 => "Range Not Satisfiable", 423 => "Locked", 429 => "Too Many Requests", 500 => "Internal Server Error",
        502 => "Bad Gateway", 503 => "Service Unavailable", 507 => "Insufficient Storage", _ => "Status"
    };
}

internal sealed class LengthBodyStream(HttpConnection conn, long length, CancellationToken ct) : Stream
{
    private readonly long _total = length;
    private long _left = length;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (_left <= 0) return 0;
        var n = await conn.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], ct);
        if (n == 0) throw new IOException("connection closed before the request body was complete");
        _left -= n;
        return n;
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _total;
    public override long Position { get => _total - _left; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class ChunkedBodyStream(HttpConnection conn, CancellationToken ct) : Stream
{
    private long _chunkLeft;
    private bool _done;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (_done) return 0;
        if (_chunkLeft == 0)
        {
            var line = await conn.ReadLineAsync(ct) ?? throw new IOException("truncated chunked body");
            var semi = line.IndexOf(';');
            _chunkLeft = long.Parse(semi >= 0 ? line[..semi] : line, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (_chunkLeft == 0)
            {
                while (!string.IsNullOrEmpty(await conn.ReadLineAsync(ct))) { } // trailers
                _done = true;
                return 0;
            }
        }
        var n = await conn.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _chunkLeft)], ct);
        if (n == 0) throw new IOException("truncated chunked body");
        _chunkLeft -= n;
        if (_chunkLeft == 0) await conn.ReadLineAsync(ct);
        return n;
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Parsed "Range: bytes=a-b" header (single range only).</summary>
public readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;

    /// <summary>Returns null when there is no usable single range; throws nothing.</summary>
    public static ByteRange? Parse(string? header, long size)
    {
        if (header is null || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || size <= 0) return null;
        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;
        var a = spec[..dash];
        var b = spec[(dash + 1)..];
        if (a.Length == 0)
        {
            if (!long.TryParse(b, out var suffix) || suffix <= 0) return null;
            return new ByteRange(Math.Max(0, size - suffix), size - 1);
        }
        if (!long.TryParse(a, out var start)) return null;
        var end = size - 1;
        if (b.Length > 0 && long.TryParse(b, out var e)) end = Math.Min(e, size - 1);
        if (start > end) return new ByteRange(start, start - 1); // unsatisfiable, Length <= 0
        return new ByteRange(start, end);
    }
}
