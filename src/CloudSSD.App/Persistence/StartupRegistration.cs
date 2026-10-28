using CloudSSD.Core;
using Microsoft.Win32;

namespace CloudSSD.App.Persistence;

public enum StartupState { NotRegistered, Registered, DisabledInWindows, PointsElsewhere }

/// <summary>
/// Per-user logon startup of the CloudSSD agent through HKCU\...\CurrentVersion\Run. This starts the agent when the
/// user signs in to Windows — not before sign-in: saved logins are DPAPI-protected for this user and drive letters
/// belong to the user's logon session, so a pre-login service could neither decrypt them nor show the drive to the user.
/// The command line carries no secrets.
/// </summary>
public sealed class StartupRegistration
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "CloudSSD";
    private const string AgentCommandPattern = "^\\s*(?:\"[^\"]*CloudSSD\\.Agent\\.exe\"|\\S*CloudSSD\\.Agent\\.exe)(?<args>\\s.*)?$";
    private static bool IsAgentCommand(object? value) => value is string command &&
        System.Text.RegularExpressions.Regex.IsMatch(command, AgentCommandPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private readonly string _runKey;
    private readonly string _approvedKey;

    /// <param name="runKey">HKCU sub-key; tests pass a private key so the real Run list is never touched.</param>
    public StartupRegistration(string runKey = DefaultRunKey, string approvedKey = ApprovedKey)
    {
        _runKey = runKey;
        _approvedKey = approvedKey;
    }

    public static string AgentPath => Path.Combine(AppPaths.AppDirectory, "CloudSSD.Agent.exe");

    /// <summary>Command line for the Run entry. A non-default data folder is passed explicitly (no environment at logon).</summary>
    public static string CommandFor(string agentExe, AppPaths paths)
    {
        var cmd = $"\"{agentExe}\" --logon";
        if (!string.Equals(Path.GetFullPath(paths.DataRoot).TrimEnd('\\'), Path.GetFullPath(DefaultDataRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            cmd += $" --home \"{paths.DataRoot.TrimEnd('\\')}\"";
        return cmd;
    }

    private static string DefaultDataRoot => AppPaths.ResolveDefaultDataRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public void Register(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKey, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
        EnsureApproved();
    }

    /// <summary>
    /// Records the entry as "enabled" in Windows' Startup-apps list when Windows has no choice recorded for it yet.
    /// Existing disabled marks are the user's choice and are never changed.
    /// </summary>
    public void EnsureApproved()
    {
        using var k = Registry.CurrentUser.CreateSubKey(_approvedKey, writable: true);
        if (k.GetValue(ValueName) is byte[] { Length: > 0 }) return;
        k.SetValue(ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
    }

    public bool HasApprovalMark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(_approvedKey);
        return k?.GetValue(ValueName) is byte[] { Length: > 0 };
    }

    public void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public string? RegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>Setup upgrades an existing entry without enabling startup or losing portable-profile arguments.</summary>
    public bool RepointExistingAgent(string agentExe)
    {
        var old = RegisteredCommand();
        if (old == null) return false;
        var match = System.Text.RegularExpressions.Regex.Match(old,
            AgentCommandPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        Register($"\"{agentExe}\"" + match.Groups["args"].Value);
        return true;
    }

    public StartupState State(string expectedCommand)
    {
        var cmd = RegisteredCommand();
        if (cmd is null) return StartupState.NotRegistered;
        if (!string.Equals(cmd, expectedCommand, StringComparison.OrdinalIgnoreCase)) return StartupState.PointsElsewhere;
        return DisabledByUser() ? StartupState.DisabledInWindows : StartupState.Registered;
    }

    /// <summary>
    /// Task Manager → Startup apps (and Settings → Apps → Startup) record a user's "Disabled" choice in StartupApproved;
    /// the first byte is odd (0x03/0x01) when disabled. CloudSSD reports this and never overrides the user's choice.
    /// </summary>
    public bool DisabledByUser()
    {
        using var k = Registry.CurrentUser.OpenSubKey(_approvedKey);
        return k?.GetValue(ValueName) is byte[] { Length: > 0 } b && (b[0] & 1) == 1;
    }
}
