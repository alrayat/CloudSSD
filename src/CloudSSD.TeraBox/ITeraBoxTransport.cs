namespace CloudSSD.TeraBox;

/// <summary>
/// The operations the WebDAV bridge needs from TeraBox. Two implementations exist:
/// <see cref="TeraBoxApi"/> (official Open Platform; needs TeraBox-issued developer credentials; preferred) and
/// <see cref="TeraBoxWebApi"/> (unofficial: the TeraBox website's own interface, signed in with the user's browser
/// session; used only when the user explicitly accepts the risk that TeraBox may restrict accounts using it).
/// All paths are absolute TeraBox paths; <see cref="Abs"/> maps bridge-relative paths into the transport's root folder.
/// </summary>
public interface ITeraBoxTransport
{
    /// <summary>"official" or "web (unofficial)" — shown in status and diagnostics.</summary>
    string Kind { get; }

    /// <summary>The TeraBox folder that becomes the drive root.</summary>
    string AppRoot { get; }

    string Abs(string rel);

    long Calls { get; }
    long Refreshes { get; }
    long Throttled { get; }

    /// <summary>Largest file this account may store, when known (plan limit); null = unknown.</summary>
    long? MaxFileSize { get; }

    Task<TeraBoxUser> UserAsync(CancellationToken ct);
    Task<TeraBoxQuota> QuotaAsync(CancellationToken ct);
    Task<IReadOnlyList<TeraBoxEntry>> ListAsync(string absDir, CancellationToken ct);
    Task<TeraBoxEntry?> StatAsync(string absPath, CancellationToken ct);
    Task<string> DownloadUrlAsync(TeraBoxEntry file, CancellationToken ct);
    Task<HttpResponseMessage> OpenDownloadAsync(string url, long? start, long? end, CancellationToken ct);
    Task MkdirAsync(string absPath, CancellationToken ct);
    Task DeleteAsync(string absPath, CancellationToken ct);
    Task MoveAsync(string absFrom, string absToDir, string newName, CancellationToken ct);
    Task RenameAsync(string absPath, string newName, CancellationToken ct);
    Task CopyAsync(string absFrom, string absToDir, string newName, CancellationToken ct);
    Task<TeraBoxEntry> UploadAsync(string localFile, string absPath, bool overwrite, IProgress<long>? progress, CancellationToken ct);
}

public static class TeraBoxPaths
{
    /// <summary>Absolute TeraBox path for a path relative to <paramref name="root"/> ("" = the root itself). Rejects "." and "..".</summary>
    public static string Abs(string root, string rel)
    {
        var clean = string.Join('/', rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Split('/').Any(s => s is "." or "..")) throw new ArgumentException("invalid path", nameof(rel));
        var r = "/" + root.Trim('/');
        if (r == "/") return "/" + clean;
        return clean.Length == 0 ? r : r + "/" + clean;
    }
}
