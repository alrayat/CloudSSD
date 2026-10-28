using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CloudSSD.TeraBox;

/// <summary>An opt-in source of existing local bytes, never a second writable copy of the cloud filesystem.</summary>
public sealed record LocalSourceMapping(string RemotePrefix, string LocalRoot, long ChunkSize = 1073741824);

public sealed class VerifiedLocalSources(IEnumerable<LocalSourceMapping> mappings, string? verificationCacheFile = null)
{
    private readonly LocalSourceMapping[] _mappings = mappings.ToArray();
    private readonly ConcurrentDictionary<string, string> _verified = LoadCache(verificationCacheFile);
    private readonly SemaphoreSlim _hashGate = new(1, 1);
    private static readonly Regex Chunk = new(@"^(.+)\.rclone_chunk\.(\d{3,})(?:_[a-z0-9]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public int Count => _mappings.Length;
    public long Hits => Interlocked.Read(ref _hits);
    public long Bytes => Interlocked.Read(ref _bytes);
    private long _hits, _bytes;

    /// <summary>
    /// Checks the entire physical cloud object (or its chunk) against the provider MD5 before returning any bytes.
    /// The returned handle denies writes/deletes for the duration of verification and delivery.
    /// Missing, changed, unverified, or mismatched files simply fall back to the provider.
    /// </summary>
    public async Task<FileStream?> TryOpenAsync(string relativePath, TeraBoxEntry entry, CancellationToken ct)
    {
        if (entry.IsDir || entry.Size <= 0 || string.IsNullOrEmpty(entry.Md5)) return null;
        var expected = TeraBoxWebApi.DecodeMd5(entry.Md5);
        if (expected.Length != 32 || !expected.All(Uri.IsHexDigit)) return null;
        if (relativePath.Contains('\\') || relativePath.Split('/').Any(p => p is "" or "." or ".." || p.Contains(':'))) return null;
        foreach (var mapping in _mappings)
        {
            var prefix = mapping.RemotePrefix.Trim('/');
            if (!relativePath.StartsWith(prefix + "/", StringComparison.Ordinal) || mapping.ChunkSize <= 0) continue;
            var name = relativePath[(prefix.Length + 1)..];
            long offset = 0;
            var chunk = Chunk.Match(name);
            if (chunk.Success)
            {
                if (!long.TryParse(chunk.Groups[2].Value, out var part) || part <= 0 ||
                    part - 1 > long.MaxValue / mapping.ChunkSize || entry.Size > mapping.ChunkSize) continue;
                offset = (part - 1) * mapping.ChunkSize;
                name = chunk.Groups[1].Value;
            }
            FileStream? file = null;
            try
            {
                var root = Path.GetFullPath(mapping.LocalRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                // Do not follow junctions/symlinks into another folder (or back into this cloud mount).
                if (HasReparsePoint(path)) continue;
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
                if ((!chunk.Success && file.Length != entry.Size) || offset > file.Length || entry.Size > file.Length - offset)
                    continue;
                var stamp = $"{file.Length}:{File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks}:{File.GetCreationTimeUtc(file.SafeFileHandle).Ticks}";
                var key = $"{path}|{offset}|{entry.Size}|{expected}";
                // Already verified assets must not queue behind an unrelated multi-gigabyte
                // hash pass: DirectStorage can time out while waiting for that lock.
                if (!_verified.TryGetValue(key, out var cachedStamp) || cachedStamp != stamp)
                {
                    await _hashGate.WaitAsync(ct);
                    try
                    {
                        if (!_verified.TryGetValue(key, out var known) || known != stamp)
                        {
                            file.Position = offset;
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                            // Website uploads use a checksum of the 4 MiB shard-hash JSON list for multipart files.
                            // Listing MD5 is that upload checksum on real accounts, not necessarily MD5(file bytes).
                            var buffer = new byte[4 << 20];
                            var shards = new List<string>();
                            for (long left = entry.Size; left > 0;)
                            {
                                var n = (int)Math.Min(buffer.Length, left);
                                await file.ReadExactlyAsync(buffer.AsMemory(0, n), ct);
                                hash.AppendData(buffer, 0, n);
                                shards.Add(Convert.ToHexString(MD5.HashData(buffer.AsSpan(0, n))).ToLowerInvariant());
                                left -= n;
                            }
                            var contentMd5 = Convert.ToHexString(hash.GetHashAndReset());
                            var uploadMd5 = shards.Count == 1 ? shards[0] :
                                Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(shards))));
                            if (!contentMd5.Equals(expected, StringComparison.OrdinalIgnoreCase) &&
                                !uploadMd5.Equals(expected, StringComparison.OrdinalIgnoreCase))
                                continue;
                            _verified[key] = stamp;
                            SaveCache();
                        }
                    }
                    finally { _hashGate.Release(); }
                }
                file.Position = offset;
                var result = file;
                file = null;
                Interlocked.Increment(ref _hits);
                return result;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A local optimization must never hide the real cloud object.
            }
            finally { if (file != null) await file.DisposeAsync(); }
        }
        return null;
    }

    public void RecordBytes(int count) => Interlocked.Add(ref _bytes, count);

    private static ConcurrentDictionary<string, string> LoadCache(string? path)
    {
        var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path == null) return result;
        try
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) return result;
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (saved != null)
                foreach (var entry in saved)
                    if (entry.Value != null) result[entry.Key] = entry.Value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return result;
    }

    // Only metadata is stored. Every reuse still checks the cloud checksum and the
    // open local file's size/timestamps. A corrupt/unavailable cache costs a rehash.
    private void SaveCache()
    {
        if (verificationCacheFile == null) return;
        var temporary = verificationCacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_verified));
            File.Move(temporary, verificationCacheFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool HasReparsePoint(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}
