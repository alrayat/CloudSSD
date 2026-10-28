using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudSSD.Mount;

public sealed class RcException(string method, int status, string message) : Exception($"rclone rc {method} failed ({status}): {message}")
{
    public string Method { get; } = method;
    public int Status { get; } = status;
}

/// <summary>
/// Client for rclone's remote-control API. The server is always bound to 127.0.0.1 with a random per-session
/// user/password (passed to rclone through environment variables), so no other machine can reach it.
/// </summary>
public sealed class RcClient : IDisposable
{
    private readonly HttpClient _http;

    public RcClient(int port, string user, string pass, TimeSpan? timeout = null)
    {
        Port = port;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
    }

    public int Port { get; }

    public async Task<JsonNode> CallAsync(string method, object? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var json = parameters is null ? "{}" : JsonSerializer.Serialize(parameters);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        using var resp = await _http.PostAsync(method, content, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            string msg = body;
            try { msg = JsonNode.Parse(body)?["error"]?.GetValue<string>() ?? body; } catch (JsonException) { }
            throw new RcException(method, (int)resp.StatusCode, msg);
        }
        return JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) ?? new JsonObject();
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await CallAsync("rc/noop", null, ct, TimeSpan.FromSeconds(3));
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or RcException or OperationCanceledException)
        {
            return false;
        }
    }

    public static int FindFreeLoopbackPort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Parsed rclone core/stats.</summary>
public sealed record CoreStats(long Bytes, double Speed, long Errors, bool RetryError, long Transfers, long Checks, int Transferring,
    double TransferringSpeed, long ListedEntries, string? LastError)
{
    public static CoreStats Parse(JsonNode n)
    {
        int transferring = 0;
        double tspeed = 0;
        if (n["transferring"] is JsonArray arr)
        {
            transferring = arr.Count;
            foreach (var t in arr) tspeed += t?["speed"]?.GetValue<double>() ?? 0;
        }
        return new CoreStats(
            n["bytes"]?.GetValue<long>() ?? 0,
            n["speed"]?.GetValue<double>() ?? 0,
            n["errors"]?.GetValue<long>() ?? 0,
            n["retryError"]?.GetValue<bool>() ?? false,
            n["transfers"]?.GetValue<long>() ?? 0,
            n["checks"]?.GetValue<long>() ?? 0,
            transferring, tspeed,
            n["listed"]?.GetValue<long>() ?? 0,
            n["lastError"]?.GetValue<string>());
    }
}

/// <summary>Parsed rclone vfs/stats.</summary>
public sealed record VfsStats(long DiskBytesUsed, int Files, int ErroredFiles, bool OutOfSpace, int UploadsInProgress, int UploadsQueued,
    int InUse, string? CachePath, string? MetaPath, int? OpenFiles = null, IReadOnlyList<string>? OpenFilePaths = null)
{
    public static VfsStats Parse(JsonNode n)
    {
        var d = n["diskCache"];
        return new VfsStats(
            d?["bytesUsed"]?.GetValue<long>() ?? 0,
            d?["files"]?.GetValue<int>() ?? 0,
            d?["erroredFiles"]?.GetValue<int>() ?? 0,
            d?["outOfSpace"]?.GetValue<bool>() ?? false,
            d?["uploadsInProgress"]?.GetValue<int>() ?? 0,
            d?["uploadsQueued"]?.GetValue<int>() ?? 0,
            n["inUse"]?.GetValue<int>() ?? 0,
            d?["path"]?.GetValue<string>(),
            d?["pathMeta"]?.GetValue<string>(),
            d?["openFiles"]?.GetValue<int>(),
            d?["openFilePaths"] is JsonArray paths ? paths.Select(p => p!.GetValue<string>()).ToArray() : null);
    }
}
