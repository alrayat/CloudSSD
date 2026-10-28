namespace CloudSSD.Providers;

public enum SupportLevel
{
    /// <summary>Implemented and exercised by CloudSSD's own workflow (connect, mount, stream).</summary>
    Supported,
    /// <summary>Works through the corresponding rclone backend; not tested end-to-end by CloudSSD.</summary>
    AvailableUntested,
    /// <summary>No authorized integration exists; the adapter is a placeholder that explains why.</summary>
    Unsupported,
    /// <summary>Implemented against the provider's official API but usable only with credentials the provider issues to approved apps.</summary>
    NeedsProviderApproval
}

public sealed record ProviderCapabilities(bool RangeReads, bool Checksums, bool Writes, bool ChangeNotifications, string? Notes = null);

/// <summary>Describes one cloud backend and how CloudSSD connects to it.</summary>
public interface ICloudProvider
{
    string Id { get; }
    string DisplayName { get; }
    string? RcloneBackend { get; }
    SupportLevel Support { get; }
    ProviderCapabilities Capabilities { get; }
    bool RequiresOAuthClient { get; }
    string SetupSummary { get; }
    string? DocumentationUrl { get; }
}

public sealed record CloudProvider(
    string Id, string DisplayName, string? RcloneBackend, SupportLevel Support, ProviderCapabilities Capabilities,
    bool RequiresOAuthClient, string SetupSummary, string? DocumentationUrl) : ICloudProvider;

public static class ProviderRegistry
{
    public static readonly CloudProvider GoogleDrive = new(
        "gdrive", "Google Drive", "drive", SupportLevel.Supported,
        new ProviderCapabilities(RangeReads: true, Checksums: true, Writes: true, ChangeNotifications: true,
            "MD5 checksums and modification times are used for cache validation; Drive API quotas apply (rate limits are retried with backoff by rclone)."),
        RequiresOAuthClient: true,
        "Create your own OAuth client in Google Cloud Console (Desktop app type), enable the Google Drive API, then paste the client ID and secret into CloudSSD. " +
        "rclone's shared client ID is being retired during 2026, so your own client is required.",
        "https://rclone.org/drive/#making-your-own-client-id");

    public static readonly CloudProvider TeraBox = new(
        "terabox", "TeraBox", "webdav", SupportLevel.NeedsProviderApproval,
        new ProviderCapabilities(RangeReads: true, Checksums: false, Writes: true, ChangeNotifications: false,
            "CloudSSD's local WebDAV bridge (drive T:). Preferred: the official Open Platform API (needs the client_id, client_secret and " +
            "private_secret TeraBox issues to approved apps). Alternative chosen by the account owner: the TeraBox website's own interface with " +
            "their signed-in session (unofficial; TeraBox may restrict accounts using it). Both tested against mocks; run cloudssd-cli terabox-probe on the " +
            "real account. Free accounts: 4 GB per file."),
        RequiresOAuthClient: true,
        "Connect TeraBox: load terabox-client.json and scan the QR code, or sign in with the TeraBox website (unofficial). See TERABOX_INTEGRATION.md.",
        "https://www.terabox.com/integrations/docs?lang=en");

    public static IReadOnlyList<CloudProvider> All { get; } =
    [
        GoogleDrive,
        new("onedrive", "Microsoft OneDrive", "onedrive", SupportLevel.AvailableUntested,
            new ProviderCapabilities(true, true, true, true), true,
            "Configure with 'rclone config' (backend 'onedrive') using CloudSSD's config file, then add it in Cloud Drives.", "https://rclone.org/onedrive/"),
        new("dropbox", "Dropbox", "dropbox", SupportLevel.AvailableUntested,
            new ProviderCapabilities(true, true, true, true), true,
            "Configure with 'rclone config' (backend 'dropbox'); a custom app key is recommended.", "https://rclone.org/dropbox/"),
        new("s3", "S3-compatible storage", "s3", SupportLevel.AvailableUntested,
            new ProviderCapabilities(true, true, true, false), false,
            "Configure with 'rclone config' (backend 's3'); access keys stay in the encrypted rclone config.", "https://rclone.org/s3/"),
        new("nas", "Local NAS (SMB/SFTP)", "smb", SupportLevel.AvailableUntested,
            new ProviderCapabilities(true, false, true, false), false,
            "Configure with 'rclone config' (backend 'smb' or 'sftp').", "https://rclone.org/smb/"),
        TeraBox,
    ];

    public static CloudProvider? ForBackend(string rcloneType) =>
        All.Where(p => p != TeraBox).FirstOrDefault(p => string.Equals(p.RcloneBackend, rcloneType, StringComparison.OrdinalIgnoreCase));
}
