using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudSSD.Diagnostics;

namespace CloudSSD.TeraBox;

/// <summary>
/// TeraBox Open Platform application credentials. TeraBox issues client_id, client_secret and private_secret only to
/// approved applications ("Vendors need to apply for a client_id and client_secret, as well as a private_secret").
/// CloudSSD never ships invented values: the file is supplied by whoever holds an approved application.
/// </summary>
public sealed record TeraBoxClientFile(string ClientId, string ClientSecret, string PrivateSecret, string AppName, string AppRoot, Uri AuthBase)
{
    public const string BundledFileName = "terabox-client.json";

    /// <summary>
    /// Parses {"client_id","client_secret","private_secret","app_name", optional "app_root", optional "auth_base_url"}.
    /// A non-HTTPS auth_base_url is only accepted for loopback (the test mock).
    /// </summary>
    public static TeraBoxClientFile Parse(string json)
    {
        var o = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("not a JSON object");
        string Req(string k) => o[k]?.GetValue<string>() is { Length: > 0 } v ? v : throw new FormatException($"'{k}' is missing");
        var app = o["app_name"]?.GetValue<string>() ?? "CloudSSD";
        var root = o["app_root"]?.GetValue<string>() ?? $"/From: Other Applications/{app}";
        var auth = new Uri(o["auth_base_url"]?.GetValue<string>() ?? "https://www.terabox.com");
        if (auth.Scheme != Uri.UriSchemeHttps && !auth.IsLoopback) throw new FormatException("auth_base_url must use https");
        return new TeraBoxClientFile(Req("client_id"), Req("client_secret"), Req("private_secret"), app, "/" + root.Trim('/'), auth);
    }

    public static TeraBoxClientFile? TryLoad(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException) { return null; }
    }

    /// <summary>Publisher credentials next to the program, or a DPAPI-protected copy the user imported.</summary>
    public static TeraBoxClientFile? Find(string protectedCopyPath)
    {
        if (SecretStore.Load(protectedCopyPath) is { } json)
        {
            try { return Parse(json); } catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { }
        }
        return TryLoad(Path.Combine(AppContext.BaseDirectory, BundledFileName));
    }

    public static void Import(string sourcePath, string protectedCopyPath)
    {
        var json = File.ReadAllText(sourcePath);
        Parse(json); // validate
        SecretStore.Save(protectedCopyPath, json);
    }

    public string Scheme => AuthBase.Scheme;

    /// <summary>md5("client_id"_"timestamp"_"client_secret"_"private_secret") as documented for gettoken/refreshtoken.</summary>
    public string Sign(long timestamp) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{ClientId}_{timestamp}_{ClientSecret}_{PrivateSecret}"))).ToLowerInvariant();
}

public sealed record TeraBoxTokens(string AccessToken, string RefreshToken, DateTimeOffset AccessExpires, DateTimeOffset RefreshExpires);

/// <summary>Tokens persisted with DPAPI. The refresh token is single-use, so every refresh is saved before use.</summary>
public sealed class TeraBoxTokenStore(string path)
{
    private readonly object _gate = new();

    public string Path { get; } = path;

    public TeraBoxTokens? Load()
    {
        lock (_gate)
        {
            var json = SecretStore.Load(Path);
            return json is null ? null : JsonSerializer.Deserialize<TeraBoxTokens>(json);
        }
    }

    public void Save(TeraBoxTokens t) { lock (_gate) SecretStore.Save(Path, JsonSerializer.Serialize(t)); }

    public void Delete() { lock (_gate) if (File.Exists(Path)) File.Delete(Path); }
}

public sealed record DeviceAuthorization(string DeviceCode, byte[] QrPng, TimeSpan ExpiresIn, TimeSpan Interval);

public sealed class TeraBoxException(int errno, string message) : Exception(message)
{
    public int Errno { get; } = errno;

    /// <summary>Invalid/expired access token (document codes 200002/200003; -6 and 111 are accepted defensively).</summary>
    public bool IsAuth => Errno is 200002 or 200003 or -6 or 111;
    public bool IsRefreshDead => Errno is 200004 or 200005 or 100001;
    public bool IsNotFound => Errno == -9;
    public bool IsExists => Errno == -8;
    /// <summary>Frequency control (300001 documented for token exchange; 31034 is the PCS-family code, assumed).</summary>
    public bool IsThrottle => Errno is 300001 or 31034;
}

/// <summary>OAuth flows of the TeraBox Open Platform (device code mode, signed token exchange, refresh, tokeninfo).</summary>
public sealed class TeraBoxAuth(TeraBoxClientFile client, HttpClient http)
{
    private Uri U(string path) => new(client.AuthBase, path);

    public async Task<DeviceAuthorization> StartDeviceAsync(CancellationToken ct)
    {
        var o = await TeraBoxApi.ReadAsync(await http.GetAsync(U("/oauth/devicecode?client_id=" + Uri.EscapeDataString(client.ClientId)), ct), ct);
        var d = o["data"]!;
        var qr = d["qrcode_url"]!.GetValue<string>();
        var comma = qr.IndexOf(',');
        return new DeviceAuthorization(d["device_code"]!.GetValue<string>(), Convert.FromBase64String(comma >= 0 ? qr[(comma + 1)..] : qr),
            TimeSpan.FromSeconds(d["expires_in"]!.GetValue<int>()), TimeSpan.FromSeconds(Math.Max(1, d["interval"]!.GetValue<int>())));
    }

    /// <summary>Polls /oauth/gettoken until the user approves in the TeraBox app, the code expires, or cancellation.</summary>
    public async Task<TeraBoxTokens> WaitForDeviceApprovalAsync(DeviceAuthorization dev, CancellationToken ct)
    {
        var until = DateTime.UtcNow + dev.ExpiresIn;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(dev.Interval, ct);
            try
            {
                return await ExchangeAsync("device_code", dev.DeviceCode, ct);
            }
            catch (TeraBoxException e) when (e.Errno == 400001) { } // not approved yet
            catch (TeraBoxException e) when (e.IsThrottle) { await Task.Delay(dev.Interval, ct); }
        }
        throw new TimeoutException("The TeraBox QR code expired before it was approved. Start again.");
    }

    public Task<TeraBoxTokens> ExchangeAsync(string grantType, string code, CancellationToken ct) =>
        TokenCallAsync("/oauth/gettoken", new() { ["grant_type"] = grantType, ["code"] = code }, null, ct);

    public Task<TeraBoxTokens> RefreshAsync(TeraBoxTokens current, CancellationToken ct) =>
        TokenCallAsync("/oauth/refreshtoken", new() { ["refresh_token"] = current.RefreshToken }, current.RefreshExpires, ct);

    private async Task<TeraBoxTokens> TokenCallAsync(string path, Dictionary<string, string> extra, DateTimeOffset? keepRefreshExpiry, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var form = new Dictionary<string, string>(extra)
        {
            ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret, ["timestamp"] = ts.ToString(), ["sign"] = client.Sign(ts)
        };
        var o = await TeraBoxApi.ReadAsync(await http.PostAsync(U(path), new FormUrlEncodedContent(form), ct), ct);
        var d = o["data"]!;
        var now = DateTimeOffset.UtcNow;
        return new TeraBoxTokens(d["access_token"]!.GetValue<string>(), d["refresh_token"]!.GetValue<string>(),
            now.AddSeconds(d["expires_in"]!.GetValue<int>()),
            keepRefreshExpiry ?? now.AddDays(30)); // documented: refresh_token valid 30 days; a refresh keeps the original expiry
    }

    public async Task<(string ApiDomain, string UploadDomain, long UserId)> TokenInfoAsync(string accessToken, CancellationToken ct)
    {
        var o = await TeraBoxApi.ReadAsync(await http.PostAsync(U("/oauth/tokeninfo"),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["access_token"] = accessToken }), ct), ct);
        var d = o["data"]!;
        var api = d["api_domain"]?.GetValue<string>() ?? client.AuthBase.Authority;
        var up = d["upload_domain"]?.GetValue<string>();
        return (api, string.IsNullOrEmpty(up) ? api : up, d["user_id"]?.GetValue<long>() ?? 0);
    }
}
