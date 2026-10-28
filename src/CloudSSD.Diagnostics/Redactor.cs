using System.Text.RegularExpressions;

namespace CloudSSD.Diagnostics;

/// <summary>
/// Removes credentials from text before it is logged, displayed or exported. Applied to every log line and to
/// diagnostic reports. Conservative: it is better to over-redact than to leak a token.
/// </summary>
public static partial class Redactor
{
    private const string Mask = "***REDACTED***";

    [GeneratedRegex("\"(access_token|refresh_token|id_token|client_secret|password|pass|token|secret|api_key|apikey)\"\\s*:\\s*\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();

    [GeneratedRegex(@"(?im)^(\s*(?:token|client_secret|password|pass|secret_access_key|access_key_id|key|session_token|sas_url|service_account_credentials)\s*=\s*).+$")]
    private static partial Regex IniSecret();

    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9\-\._~\+/]+=*")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)((?:client_secret|access_token|refresh_token|code|password|pass|token)=)[^&\s""']+")]
    private static partial Regex QuerySecret();

    [GeneratedRegex(@"(?i)(RCLONE_[A-Z_]*(?:PASS|SECRET|TOKEN)[A-Z_]*\s*[=:]\s*)\S+")]
    private static partial Regex RcloneEnv();

    [GeneratedRegex(@"(?i)(--(?:rc-pass|rc-user|password|client-secret|drive-client-secret|token)[= ])\S+")]
    private static partial Regex Flag();

    [GeneratedRegex(@"ya29\.[A-Za-z0-9\-_\.]+")]
    private static partial Regex GoogleAccessToken();

    [GeneratedRegex(@"1//[A-Za-z0-9\-_]{20,}")]
    private static partial Regex GoogleRefreshToken();

    [GeneratedRegex(@"GOCSPX-[A-Za-z0-9\-_]+")]
    private static partial Regex GoogleClientSecret();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = JsonSecret().Replace(text, m => $"\"{m.Groups[1].Value}\":\"{Mask}\"");
        s = IniSecret().Replace(s, m => m.Groups[1].Value + Mask);
        s = Bearer().Replace(s, m => m.Groups[1].Value + Mask);
        s = QuerySecret().Replace(s, m => m.Groups[1].Value + Mask);
        s = RcloneEnv().Replace(s, m => m.Groups[1].Value + Mask);
        s = Flag().Replace(s, m => m.Groups[1].Value + Mask);
        s = GoogleAccessToken().Replace(s, Mask);
        s = GoogleRefreshToken().Replace(s, Mask);
        s = GoogleClientSecret().Replace(s, Mask);
        return s;
    }
}
