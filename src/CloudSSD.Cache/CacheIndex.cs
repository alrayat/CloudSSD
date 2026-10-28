using System.Collections.Concurrent;

namespace CloudSSD.Cache;

/// <summary>
/// In-memory snapshot of rclone's cached-range metadata per mount, refreshed in the background, so the prediction
/// engine can skip blocks rclone already holds without touching the disk on every decision.
/// </summary>
public sealed class CacheIndex : IDisposable
{
    private readonly ConcurrentDictionary<string, (string CacheDir, string? Root, Dictionary<string, CachedFile> Files)> _mounts = new();
    private readonly Timer _timer;

    public CacheIndex(TimeSpan? refresh = null)
    {
        var r = refresh ?? TimeSpan.FromSeconds(30);
        _timer = new Timer(_ => RefreshAll(), null, r, r);
    }

    public DateTime? LastRefresh { get; private set; }

    public void Register(string mountId, string cacheDir, string? remoteRoot)
    {
        _mounts[mountId] = (cacheDir, remoteRoot, new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase));
        Refresh(mountId);
    }

    public void Unregister(string mountId) => _mounts.TryRemove(mountId, out _);

    public void Refresh(string mountId)
    {
        if (!_mounts.TryGetValue(mountId, out var m)) return;
        var files = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in CacheInspector.Enumerate(m.CacheDir, m.Root)) files[f.RemotePath] = f;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        _mounts[mountId] = (m.CacheDir, m.Root, files);
        LastRefresh = DateTime.Now;
    }

    public void RefreshAll()
    {
        foreach (var id in _mounts.Keys) Refresh(id);
    }

    public bool IsCached(string mountId, string mountPath, long offset, long length) =>
        _mounts.TryGetValue(mountId, out var m) && m.Files.TryGetValue(mountPath.Replace('\\', '/').TrimStart('/'), out var f) && f.Covers(offset, length);

    public CachedFile? Get(string mountId, string mountPath) =>
        _mounts.TryGetValue(mountId, out var m) && m.Files.TryGetValue(mountPath.Replace('\\', '/').TrimStart('/'), out var f) ? f : null;

    public IReadOnlyList<CachedFile> Files(string mountId) =>
        _mounts.TryGetValue(mountId, out var m) ? m.Files.Values.ToList() : [];

    public void Dispose() => _timer.Dispose();
}
