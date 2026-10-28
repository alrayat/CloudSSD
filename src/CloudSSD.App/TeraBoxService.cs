using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using CloudSSD.Mount;
using CloudSSD.TeraBox;
using Microsoft.Extensions.Logging;

namespace CloudSSD.App;

public sealed record TeraBoxBridgeStatus(string? User, string? Plan, long? QuotaTotal, long? QuotaUsed, long Reads, long ReadBytes,
    long Uploads, long UploadBytes, long Errors, long TokenRefreshes, long Throttled, bool? RangeSupported, long RangeEmulated, string? LastError,
    bool ProviderThrottled = false, long RetryAfterSeconds = 0, long LocalSourceBytes = 0);

/// <summary>
/// Connects TeraBox through the official Open Platform: device-code login (QR scanned in the TeraBox app), a local
/// WebDAV bridge process ("cloudssd-cli terabox-bridge") that owns the tokens, and an rclone webdav remote named "terabox"
/// pointing at the bridge. The bridge runs as its own process so drives that stay mounted after CloudSSD closes keep
/// working.
/// </summary>
public sealed class TeraBoxService
{
    public const string RemoteName = "terabox";
    public const string BridgeUser = "cloudssd-cli";

    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly RcloneConfigService _config;
    private readonly Func<RcloneInfo?> _rclone;
    private readonly string _cli;
    private readonly ILogger _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _bridgeGate = new(1, 1);

    public TeraBoxService(AppPaths paths, AppSettings settings, RcloneConfigService config, Func<RcloneInfo?> rclone, string cliPath, ILogger log)
    {
        _paths = paths;
        _settings = settings;
        _config = config;
        _rclone = rclone;
        _cli = cliPath;
        _log = log;
    }

    public TeraBoxClientFile? Client => TeraBoxClientFile.Find(_paths.TeraBoxClientFile);
    /// <summary>The transport selected in settings, with the HttpClient it needs (caller disposes it).</summary>
    public static (ITeraBoxTransport Transport, HttpClient Http) CreateTransport(AppPaths paths, AppSettings settings, ILogger log)
    {
        if (settings.TeraBox.Transport == "web")
        {
            var http = TeraBoxWebApi.CreateHttpClient();
            return (new TeraBoxWebApi(new TeraBoxWebSessionStore(paths.TeraBoxWebSessionFile), http, log, settings.TeraBox.WebRoot), http);
        }
        var client = TeraBoxClientFile.Find(paths.TeraBoxClientFile) ?? throw new InvalidOperationException("TeraBox developer credentials not found.");
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        return (new TeraBoxApi(client, new TeraBoxTokenStore(paths.TeraBoxTokenFile), h, log), h);
    }

    public bool UsesWebSession => _settings.TeraBox.Transport == "web";
    public bool HasLogin => UsesWebSession ? File.Exists(_paths.TeraBoxWebSessionFile) : File.Exists(_paths.TeraBoxTokenFile);

    /// <summary>
    /// UNOFFICIAL connection: stores the website session the user created by signing in inside CloudSSD (DPAPI), checks it
    /// against TeraBox (account, plan, quota), switches the bridge to the website transport and prepares the "terabox" remote.
    /// Only called after the user accepted that TeraBox may restrict accounts using non-public interfaces.
    /// </summary>
    public async Task<TeraBoxUser> ConnectWebSessionAsync(TeraBoxWebSession session, CancellationToken ct)
    {
        var store = new TeraBoxWebSessionStore(_paths.TeraBoxWebSessionFile);
        var previous = store.Load();
        store.Save(session);
        try
        {
            using var http = TeraBoxWebApi.CreateHttpClient(TimeSpan.FromSeconds(30));
            var api = new TeraBoxWebApi(store, http, _log, _settings.TeraBox.WebRoot);
            var user = await api.UserAsync(ct);
            var quota = await api.QuotaAsync(ct);
            _settings.TeraBox.Transport = "web";
            _settings.TeraBox.Account = user.Name;
            _settings.TeraBox.Plan = user.Plan;
            await EnsureRemoteAsync(ct);
            // A running bridge still holds the old session (or the other transport). Restart it rather than only stopping
            // it: a mounted drive would otherwise be left talking to nothing (seen after a re-sign-in on 2026-10-19).
            await RestartBridgeAsync(ct);
            _log.LogInformation("TeraBox connected through the website session (unofficial): {Plan}, {Used} of {Total} used",
                user.Plan, ByteSize.Format(quota.Used), ByteSize.Format(quota.Total));
            return user;
        }
        catch
        {
            if (previous != null) store.Save(previous); else store.Delete();
            throw;
        }
    }
    /// <summary>rclone chunker overlay on top of <see cref="RemoteName"/>: large files are stored as parts below the plan limit.</summary>
    public const string ChunkedRemoteName = "terabox-chunked";

    public static bool IsTeraBoxMount(MountDefinition d) =>
        d.RemoteName.Equals(RemoteName, StringComparison.OrdinalIgnoreCase) || d.RemoteName.Equals(ChunkedRemoteName, StringComparison.OrdinalIgnoreCase);

    public static bool IsChunked(MountDefinition d) => d.RemoteName.Equals(ChunkedRemoteName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The remote a TeraBox drive should use with the current settings.</summary>
    public string DriveRemoteName => _settings.TeraBox.ChunkLargeFiles ? ChunkedRemoteName : RemoteName;

    /// <summary>Chunk size actually used: the setting, kept safely below the plan's per-file limit (4 GB decimal on Free).</summary>
    public static long EffectiveChunkBytes(TeraBoxSettings s) =>
        Math.Clamp((long)s.ChunkSizeMiB * 1024 * 1024, 16L * 1024 * 1024, 3_500_000_000L / (1024 * 1024) * 1024 * 1024);

    public void ImportClientFile(string path) => TeraBoxClientFile.Import(path, _paths.TeraBoxClientFile);

    private TeraBoxClientFile RequireClient() => Client ?? throw new InvalidOperationException(
        "TeraBox developer credentials are missing. TeraBox only issues them to approved apps (client_id, client_secret and private_secret). " +
        "Add the terabox-client.json file you received from TeraBox.");

    // ------------------------------------------------------------------ login

    public Task<DeviceAuthorization> StartLoginAsync(CancellationToken ct) => new TeraBoxAuth(RequireClient(), _http).StartDeviceAsync(ct);

    /// <summary>Waits for the QR approval, stores the tokens (DPAPI), prepares the bridge and the rclone remote.</summary>
    public async Task<TeraBoxUser> FinishLoginAsync(DeviceAuthorization dev, CancellationToken ct)
    {
        var client = RequireClient();
        var tokens = await new TeraBoxAuth(client, _http).WaitForDeviceApprovalAsync(dev, ct);
        var store = new TeraBoxTokenStore(_paths.TeraBoxTokenFile);
        store.Save(tokens);
        var api = new TeraBoxApi(client, store, _http, _log);
        var user = await api.UserAsync(ct);
        _settings.TeraBox.Account = user.Name;
        _settings.TeraBox.Plan = user.Plan;
        await EnsureRemoteAsync(ct);
        _log.LogInformation("TeraBox connected ({Plan} account)", user.Plan);
        return user;
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await StopBridgeAsync();
        new TeraBoxTokenStore(_paths.TeraBoxTokenFile).Delete();
        new TeraBoxWebSessionStore(_paths.TeraBoxWebSessionFile).Delete();
        _settings.TeraBox.Transport = "official";
        if (_rclone() is { } rc)
        {
            try { await _config.DeleteRemoteAsync(rc, ChunkedRemoteName, ct); } catch (InvalidOperationException) { }
            try { await _config.DeleteRemoteAsync(rc, RemoteName, ct); } catch (InvalidOperationException) { }
        }
        _settings.TeraBox.Account = _settings.TeraBox.Plan = null;
    }

    // ------------------------------------------------------------------ rclone remote

    private string BridgeSecret()
    {
        var s = SecretStore.Load(_paths.TeraBoxBridgeSecretFile);
        if (s is null)
        {
            s = SecretStore.RandomToken(24);
            SecretStore.Save(_paths.TeraBoxBridgeSecretFile, s);
        }
        return s;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>Creates or updates the "terabox" webdav remote that points at the bridge.</summary>
    public async Task EnsureRemoteAsync(CancellationToken ct)
    {
        var rclone = _rclone() ?? throw new InvalidOperationException("rclone was not found.");
        if (_settings.TeraBox.BridgePort == 0) _settings.TeraBox.BridgePort = FreePort();
        var url = $"http://127.0.0.1:{_settings.TeraBox.BridgePort}/";
        var remotes = await _config.ListRemotesAsync(rclone, ct);
        var exists = remotes.Any(r => r.Name == RemoteName);
        string[] args = exists
            ? ["config", "update", RemoteName, $"url={url}", "--obscure", $"pass={BridgeSecret()}", "--non-interactive"]
            : ["config", "create", RemoteName, "webdav", $"url={url}", "vendor=other", $"user={BridgeUser}", $"pass={BridgeSecret()}",
               "description=CloudSSD TeraBox bridge", "--obscure", "--non-interactive"];
        var r = await _config.CreateRunner(rclone).RunAsync(args, TimeSpan.FromSeconds(30), ct);
        if (!r.Success) throw new InvalidOperationException("Could not save the TeraBox drive settings: " + Redactor.Redact(r.StdErr.Trim()));

        // Chunker overlay: files above the chunk size become parts ("name.rclone_chunk.001" …) plus a small metadata object
        // holding the logical size and an MD5 of the whole file; the drive shows one file of the original size. Files below
        // the chunk size are stored unchanged and stay readable through either remote.
        var chunk = $"{EffectiveChunkBytes(_settings.TeraBox) / (1024 * 1024)}Mi";
        string[] cargs = remotes.Any(x => x.Name == ChunkedRemoteName)
            ? ["config", "update", ChunkedRemoteName, $"chunk_size={chunk}", "--non-interactive"]
            : ["config", "create", ChunkedRemoteName, "chunker", $"remote={RemoteName}:", $"chunk_size={chunk}", "hash_type=md5",
               "description=CloudSSD TeraBox large files (parts below the plan limit)", "--non-interactive"];
        var cr = await _config.CreateRunner(rclone).RunAsync(cargs, TimeSpan.FromSeconds(30), ct);
        if (!cr.Success) throw new InvalidOperationException("Could not save the TeraBox large-file settings: " + Redactor.Redact(cr.StdErr.Trim()));
    }

    // ------------------------------------------------------------------ bridge process

    private HttpRequestMessage BridgeRequest(HttpMethod m, string path)
    {
        var req = new HttpRequestMessage(m, $"http://127.0.0.1:{_settings.TeraBox.BridgePort}/{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{BridgeUser}:{BridgeSecret()}")));
        return req;
    }

    public Task<bool> IsBridgeHealthyAsync(CancellationToken ct) => HealthyAsync(ct);

    private async Task<bool> HealthyAsync(CancellationToken ct)
    {
        if (_settings.TeraBox.BridgePort == 0) return false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var r = await _http.SendAsync(BridgeRequest(HttpMethod.Get, ".cloudssd/health"), cts.Token);
            return r.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { return false; }
    }

    private static bool PortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>Starts the bridge process if it is not already answering (it may survive from an earlier session).</summary>
    public async Task EnsureBridgeAsync(CancellationToken ct)
    {
        await _bridgeGate.WaitAsync(ct);
        try
        {
            if (await HealthyAsync(ct)) return;
            if (!HasLogin) throw new InvalidOperationException("TeraBox is not connected. Connect it in Cloud Drives.");
            if (!UsesWebSession) RequireClient();
            if (_settings.TeraBox.BridgePort == 0 || !PortFree(_settings.TeraBox.BridgePort))
            {
                _settings.TeraBox.BridgePort = FreePort(); // another program took the port: move and repoint rclone
                await EnsureRemoteAsync(ct);
            }
            if (!File.Exists(_cli)) throw new FileNotFoundException("cloudssd-cli.exe (the TeraBox bridge host) was not found next to CloudSSD.", _cli);
            var psi = new ProcessStartInfo(_cli)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = false, RedirectStandardError = false,
                WorkingDirectory = AppPaths.AppDirectory
            };
            foreach (var a in new[] { "terabox-bridge", "--home", _paths.DataRoot, "--port", _settings.TeraBox.BridgePort.ToString() }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the TeraBox bridge.");
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(200, ct);
                if (p.HasExited) throw new InvalidOperationException($"The TeraBox bridge stopped immediately (exit code {p.ExitCode}). See logs\\terabox-bridge*.log.");
                if (await HealthyAsync(ct))
                {
                    _log.LogInformation("TeraBox bridge running on 127.0.0.1:{Port} (pid {Pid})", _settings.TeraBox.BridgePort, p.Id);
                    return;
                }
            }
            throw new TimeoutException("The TeraBox bridge did not start within 10 seconds.");
        }
        finally { _bridgeGate.Release(); }
    }

    public async Task StopBridgeAsync()
    {
        await _bridgeGate.WaitAsync();
        try { await StopBridgeCoreAsync(); }
        finally { _bridgeGate.Release(); }
    }

    /// <summary>Restarts the bridge if it is running, so it picks up a new login; a stopped bridge stays stopped.</summary>
    public async Task RestartBridgeAsync(CancellationToken ct)
    {
        bool wasRunning;
        await _bridgeGate.WaitAsync(ct);
        try
        {
            wasRunning = await HealthyAsync(ct);
            if (wasRunning) await StopBridgeCoreAsync();
        }
        finally { _bridgeGate.Release(); }
        if (wasRunning) await EnsureBridgeAsync(ct);
    }

    private async Task StopBridgeCoreAsync()
    {
        if (!await HealthyAsync(CancellationToken.None)) return;
        try { using var _ = await _http.SendAsync(BridgeRequest(HttpMethod.Post, ".cloudssd/shutdown")); }
        catch (HttpRequestException) { }
        for (int i = 0; i < 25 && await HealthyAsync(CancellationToken.None); i++) await Task.Delay(200);
        _log.LogInformation("TeraBox bridge stopped");
    }

    public async Task<TeraBoxBridgeStatus?> StatusAsync(CancellationToken ct = default)
    {
        if (!await HealthyAsync(ct)) return null;
        try
        {
            using var r = await _http.SendAsync(BridgeRequest(HttpMethod.Get, ".cloudssd/status"), ct);
            var o = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))!;
            long L(string k) => o[k]?.GetValue<long>() ?? 0;
            return new TeraBoxBridgeStatus(o["user"]?.GetValue<string>(), o["plan"]?.GetValue<string>(), o["quotaTotal"]?.GetValue<long>(),
                o["quotaUsed"]?.GetValue<long>(), L("reads"), L("readBytes"), L("uploads"), L("uploadBytes"), L("errors"), L("tokenRefreshes"),
                L("throttled"), o["rangeSupported"]?.GetValue<bool>(), L("rangeEmulated"), o["lastError"]?.GetValue<string>(),
                o["providerThrottled"]?.GetValue<bool>() ?? false, L("retryAfterSeconds"), L("localSourceBytes"));
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }
}
