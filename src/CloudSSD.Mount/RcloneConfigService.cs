using System.Text.Json;
using System.Text.Json.Nodes;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CloudSSD.Mount;

public sealed record RemoteEntry(string Name, string Type);

public sealed record RemoteItem(string Name, string Path, bool IsDir, long Size, DateTimeOffset? ModTime, string? MimeType);

/// <summary>
/// Owns CloudSSD's private rclone config file (%LOCALAPPDATA%\CloudSSD\rclone.conf). When encryption is enabled
/// the file is encrypted by rclone itself with a random password that is stored DPAPI-protected and handed to rclone
/// only through the RCLONE_CONFIG_PASS environment variable.
/// </summary>
public sealed class RcloneConfigService
{
    private readonly AppPaths _paths;
    private readonly ILogger _log;
    private const string EncryptedHeader = "# Encrypted rclone configuration File";

    public RcloneConfigService(AppPaths paths, ILogger<RcloneConfigService> log)
    {
        _paths = paths;
        _log = log;
    }

    public string ConfigPath => _paths.RcloneConfigFile;

    public bool IsEncrypted => File.Exists(ConfigPath) && File.ReadLines(ConfigPath).FirstOrDefault()?.StartsWith(EncryptedHeader) == true;

    /// <summary>Environment every rclone child process receives.</summary>
    public IReadOnlyDictionary<string, string?> BaseEnvironment()
    {
        var env = new Dictionary<string, string?>
        {
            ["RCLONE_CONFIG"] = ConfigPath,
            // Never let rclone prompt on a hidden console.
            ["RCLONE_ASK_PASSWORD"] = "false",
            ["RCLONE_PASSWORD_COMMAND"] = null,
        };
        var pass = SecretStore.Load(_paths.ConfigPasswordFile);
        env["RCLONE_CONFIG_PASS"] = IsEncrypted ? pass : null;
        return env;
    }

    public RcloneRunner CreateRunner(RcloneInfo rclone) => new(rclone.Path, BaseEnvironment());

    public async Task<IReadOnlyList<RemoteEntry>> ListRemotesAsync(RcloneInfo rclone, CancellationToken ct = default)
    {
        if (!File.Exists(ConfigPath)) return [];
        var r = await CreateRunner(rclone).RunAsync(["listremotes", "--long"], TimeSpan.FromSeconds(30), ct);
        if (!r.Success) throw new InvalidOperationException("rclone listremotes failed: " + Redactor.Redact(r.StdErr.Trim()));
        var list = new List<RemoteEntry>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            // "--long" prints "name: type [description]"; the type is the first word.
            list.Add(new RemoteEntry(line[..idx], line[(idx + 1)..].Trim().Split(' ', 2)[0]));
        }
        return list;
    }

    /// <summary>Lists one directory level of a remote (used by the folder picker). Uses metadata only, no file data.</summary>
    public async Task<IReadOnlyList<RemoteItem>> ListDirectoryAsync(RcloneInfo rclone, string remote, string path, CancellationToken ct = default)
    {
        var target = $"{remote}:{path.TrimStart('/')}";
        var r = await CreateRunner(rclone).RunAsync(["lsjson", target], TimeSpan.FromMinutes(2), ct);
        if (!r.Success) throw new InvalidOperationException(ExplainError(r.StdErr));
        var arr = JsonNode.Parse(r.StdOut) as JsonArray ?? [];
        return arr.Where(n => n != null).Select(n => new RemoteItem(
            n!["Name"]!.GetValue<string>(),
            n["Path"]!.GetValue<string>(),
            n["IsDir"]?.GetValue<bool>() ?? false,
            n["Size"]?.GetValue<long>() ?? -1,
            DateTimeOffset.TryParse(n["ModTime"]?.GetValue<string>(), out var mt) ? mt : null,
            n["MimeType"]?.GetValue<string>())).ToList();
    }

    public async Task DeleteRemoteAsync(RcloneInfo rclone, string name, CancellationToken ct = default)
    {
        var r = await CreateRunner(rclone).RunAsync(["config", "delete", name], TimeSpan.FromSeconds(30), ct);
        if (!r.Success) throw new InvalidOperationException(Redactor.Redact(r.StdErr));
    }

    /// <summary>
    /// Encrypts the config with a random password (stored with DPAPI) if it is not encrypted yet.
    /// rclone reads the new password through --password-command, which invokes "cloudssd-cli.exe secret emit-config-pass".
    /// </summary>
    public async Task<bool> EnsureEncryptedAsync(RcloneInfo rclone, string cloudssdExePath, CancellationToken ct = default)
    {
        if (!File.Exists(ConfigPath) || IsEncrypted) return IsEncrypted;
        if (!File.Exists(cloudssdExePath))
        {
            _log.LogWarning("Cannot encrypt rclone config: helper {Helper} not found", cloudssdExePath);
            return false;
        }
        if (SecretStore.Load(_paths.ConfigPasswordFile) is null)
            SecretStore.Save(_paths.ConfigPasswordFile, SecretStore.RandomToken(32));
        var runner = new RcloneRunner(rclone.Path, new Dictionary<string, string?> { ["RCLONE_CONFIG"] = ConfigPath, ["RCLONE_CONFIG_PASS"] = null });
        var cmd = $"\"{cloudssdExePath}\" secret emit-config-pass --home \"{_paths.DataRoot}\"";
        var r = await runner.RunAsync(["config", "encryption", "set", "--password-command", cmd], TimeSpan.FromSeconds(30), ct);
        if (!r.Success || !IsEncrypted)
        {
            _log.LogWarning("rclone config encryption failed: {Err}", Redactor.Redact(r.StdErr));
            return false;
        }
        _log.LogInformation("rclone config encrypted (password protected with Windows DPAPI)");
        return true;
    }

    /// <summary>True when rclone failed because the saved login is no longer valid: restarting cannot help, reconnecting can.</summary>
    public static bool IsAuthFailure(string text) =>
        text.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) || text.Contains("couldn't fetch token", StringComparison.OrdinalIgnoreCase)
        || text.Contains("empty token", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps common rclone/provider error text to an actionable message.</summary>
    public static string ExplainError(string stderr)
    {
        var s = Redactor.Redact(stderr);
        if (s.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) || s.Contains("token expired", StringComparison.OrdinalIgnoreCase)
            || s.Contains("couldn't fetch token", StringComparison.OrdinalIgnoreCase) || s.Contains("empty token", StringComparison.OrdinalIgnoreCase))
            return "Cloud authentication required: the saved login has expired or was revoked. Reconnect the account. (" + LastLine(s) + ")";
        if (s.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) || s.Contains("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase) || s.Contains(" 429", StringComparison.Ordinal))
            return "The provider is rate limiting requests. rclone retries with exponential backoff; try again shortly. (" + LastLine(s) + ")";
        if (s.Contains("no such host", StringComparison.OrdinalIgnoreCase) || s.Contains("dial tcp", StringComparison.OrdinalIgnoreCase))
            return "Network unavailable: cannot reach the provider. (" + LastLine(s) + ")";
        if (s.Contains("didn't find section in config file", StringComparison.OrdinalIgnoreCase))
            return "The cloud account is not configured. Connect it in Cloud Drives.";
        if (s.Contains("directory not found", StringComparison.OrdinalIgnoreCase))
            return "Folder not found on the remote.";
        return LastLine(s);
    }

    private static string LastLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? s;
}

/// <summary>
/// Creates a Google Drive remote with the user's own OAuth client through rclone's rc config state machine.
/// The client secret travels only in the HTTP body to a 127.0.0.1 rclone daemon; the Google login happens in the
/// user's own browser on Google's official consent page (rclone listens on 127.0.0.1:53682 for the redirect).
/// </summary>
public sealed class GoogleDriveConnector
{
    private readonly RcloneConfigService _config;
    private readonly ILogger _log;

    public GoogleDriveConnector(RcloneConfigService config, ILogger<GoogleDriveConnector> log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Connection request. Leave ClientId and ClientSecret empty to use rclone's built-in shared OAuth client
    /// ("one-click" sign-in). That client is being retired by rclone during 2026 and is shared by all rclone users, so
    /// a personal client remains the reliable option.
    /// </summary>
    public sealed record Request(string RemoteName, string ClientId, string ClientSecret, bool ReadOnlyScope, string? RootFolderId)
    {
        public bool UsesSharedClient => string.IsNullOrWhiteSpace(ClientId) && string.IsNullOrWhiteSpace(ClientSecret);
    }

    public async Task ConnectAsync(RcloneInfo rclone, Request req, IProgress<string>? progress, CancellationToken ct)
    {
        if (!req.UsesSharedClient)
        {
            if (string.IsNullOrWhiteSpace(req.ClientId) || !req.ClientId.Trim().EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The client ID must be a Google OAuth client ID ending in .apps.googleusercontent.com.");
            if (string.IsNullOrWhiteSpace(req.ClientSecret)) throw new ArgumentException("The client secret is required.");
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(req.RemoteName, "^[A-Za-z0-9_\\-]{1,40}$"))
            throw new ArgumentException("Account name may only contain letters, digits, '-' and '_'.");

        var port = RcClient.FindFreeLoopbackPort();
        var user = SecretStore.RandomToken(12);
        var pass = SecretStore.RandomToken(24);
        var env = new Dictionary<string, string?>(_config.BaseEnvironment()) { ["RCLONE_RC_USER"] = user, ["RCLONE_RC_PASS"] = pass };
        var runner = new RcloneRunner(rclone.Path, env);
        var psi = runner.CreateStartInfo(["rcd", "--rc-addr", $"127.0.0.1:{port}"]);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using var rc = new RcClient(port, user, pass, TimeSpan.FromMinutes(10));
        try
        {
            for (int i = 0; i < 50 && !await rc.PingAsync(ct); i++) await Task.Delay(100, ct);

            var parameters = new Dictionary<string, string> { ["scope"] = req.ReadOnlyScope ? "drive.readonly" : "drive" };
            if (!req.UsesSharedClient)
            {
                parameters["client_id"] = req.ClientId.Trim();
                parameters["client_secret"] = req.ClientSecret.Trim();
            }
            if (!string.IsNullOrWhiteSpace(req.RootFolderId)) parameters["root_folder_id"] = req.RootFolderId.Trim();

            progress?.Report("Saving OAuth client configuration…");
            var node = await rc.CallAsync("config/create", new
            {
                name = req.RemoteName,
                type = "drive",
                parameters,
                opt = new { nonInteractive = true, obscure = false }
            }, ct);

            for (int step = 0; step < 10; step++)
            {
                var state = node["State"]?.GetValue<string>() ?? "";
                var err = node["Error"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(err)) throw new InvalidOperationException("Google Drive setup failed: " + Redactor.Redact(err));
                if (string.IsNullOrEmpty(state)) break;
                var option = node["Option"]?["Name"]?.GetValue<string>() ?? "";
                string answer = option switch
                {
                    "config_is_local" => "true",
                    "config_change_team_drive" => "false",
                    // rclone >= 1.75 warns that its shared client is being retired; the user chose it explicitly.
                    "config_shared_client_id" => req.UsesSharedClient ? "true" : "false",
                    _ => node["Option"]?["DefaultStr"]?.GetValue<string>() ?? ""
                };
                if (option == "config_is_local")
                    progress?.Report("Opening Google's sign-in page in your browser. Complete the consent there; CloudSSD never sees your password.");
                else
                    progress?.Report($"Finishing configuration ({option})…");
                _log.LogInformation("Drive config step {Option}", option);
                node = await rc.CallAsync("config/update", new
                {
                    name = req.RemoteName,
                    parameters = new Dictionary<string, string>(),
                    opt = new { nonInteractive = true, obscure = false, @continue = true, state, result = answer }
                }, ct, TimeSpan.FromMinutes(10));
            }

            // Verify the token works with a cheap metadata call.
            progress?.Report("Verifying access to Google Drive…");
            try
            {
                var about = await rc.CallAsync("operations/about", new { fs = req.RemoteName + ":" }, ct, TimeSpan.FromMinutes(1));
                _log.LogInformation("Google Drive connected: quota total={Total} used={Used}", about["total"]?.ToString(), about["used"]?.ToString());
            }
            catch (RcException e)
            {
                // Do not leave a half-working login behind.
                try { await rc.CallAsync("config/delete", new { name = req.RemoteName }, CancellationToken.None, TimeSpan.FromSeconds(10)); } catch (Exception) { }
                throw GoogleSetupException.From(e.Message) ?? (Exception)e;
            }
        }
        finally
        {
            try { await rc.CallAsync("core/quit", null, CancellationToken.None, TimeSpan.FromSeconds(3)); } catch (Exception) { }
            if (!proc.WaitForExit(3000)) try { proc.Kill(true); } catch (InvalidOperationException) { }
        }
    }
}

/// <summary>A Google Cloud project setting the user must change, with a one-click link to fix it.</summary>
public sealed class GoogleSetupException(string message, string fixUrl, string fixLabel) : Exception(message)
{
    public string FixUrl { get; } = fixUrl;
    public string FixLabel { get; } = fixLabel;

    public static GoogleSetupException? From(string error)
    {
        if (error.Contains("accessNotConfigured", StringComparison.OrdinalIgnoreCase) || error.Contains("SERVICE_DISABLED", StringComparison.OrdinalIgnoreCase)
            || error.Contains("has not been used in project", StringComparison.OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(error, @"project[= ](\d+)");
            var url = "https://console.developers.google.com/apis/api/drive.googleapis.com/overview" + (m.Success ? "?project=" + m.Groups[1].Value : "");
            return new GoogleSetupException(
                "Almost done: the Google Drive API is switched off in your Google project. Click 'Enable Google Drive API', press Enable on Google's page, wait about a minute, then sign in again.",
                url, "Enable Google Drive API");
        }
        return null;
    }
}
