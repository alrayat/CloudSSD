using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CloudSSD.Core;
using Microsoft.Win32.SafeHandles;

namespace CloudSSD.Mount;

public enum ProviderEventKind { Error, Retry, RateLimited, AuthError, NetworkError, CacheCleaned, OutOfSpace }

public sealed record ProviderEvent(DateTime Timestamp, ProviderEventKind Kind, string Message);

/// <summary>
/// Reconstructs individual reads from rclone VFS debug output (-vv, --log-format date,time,microseconds).
/// Verified line shapes (rclone v1.75.2):
/// <code>
/// big.bin(0x3d31c648cc40): _readAt: size=65536, off=0
/// vfs cache: looking for range={Pos:0 Size:65536} in [] - present false
/// big.bin(0x3d31c648cc40): >_readAt: n=65536, err=&lt;nil&gt;
/// </code>
/// "present" is rclone's own cache-hit verdict; the time between _readAt and >_readAt is the wait the application saw.
/// </summary>
public sealed partial class RcloneLogParser
{
    [GeneratedRegex(@"^(?<ts>\d{4}/\d\d/\d\d \d\d:\d\d:\d\d(?:\.\d+)?) (?<lvl>[A-Z]+)\s*: (?<msg>.*)$")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"^(?<path>.+)\((?<h>0x[0-9a-fA-F]+)\): _readAt: size=(?<size>\d+), off=(?<off>\d+)$")]
    private static partial Regex ReadStartRegex();

    [GeneratedRegex(@"^(?<path>.+)\((?<h>0x[0-9a-fA-F]+)\): >_readAt: n=(?<n>-?\d+), err=(?<err>.*)$")]
    private static partial Regex ReadEndRegex();

    [GeneratedRegex(@"^vfs cache: looking for range=\{Pos:(?<pos>\d+) Size:(?<size>\d+)\} in .* - present (?<p>true|false)$")]
    private static partial Regex LookupRegex();

    private sealed class Pending
    {
        public required string Path;
        public required long Offset;
        public required int Size;
        public required DateTime Start;
        public bool? Hit;
    }

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly string _mountId;
    private readonly Func<string, long, long, DateTime, bool>? _isPrefetch;

    public RcloneLogParser(string mountId, Func<string, long, long, DateTime, bool>? isPrefetchRead = null)
    {
        _mountId = mountId;
        _isPrefetch = isPrefetchRead;
    }

    public event Action<AccessEvent>? Read;
    /// <summary>rclone checked its cache for an in-flight read: (path, offset, present, isPrefetch). Arrives before the data.</summary>
    public event Action<string, long, bool, bool>? Lookup;
    public event Action<ProviderEvent>? Provider;

    public long LinesParsed { get; private set; }

    public void ParseLine(string line)
    {
        LinesParsed++;
        var m = LineRegex().Match(line);
        if (!m.Success) return;
        var msg = m.Groups["msg"].Value;
        var lvl = m.Groups["lvl"].Value;

        if (lvl == "DEBUG")
        {
            if (msg.Contains("_readAt: ", StringComparison.Ordinal))
            {
                if (msg.Contains(">_readAt", StringComparison.Ordinal)) OnReadEnd(m, msg);
                else OnReadStart(m, msg);
                return;
            }
            if (msg.StartsWith("vfs cache: looking for range", StringComparison.Ordinal))
            {
                OnLookup(msg);
                return;
            }
            if (msg.Contains("low level retry", StringComparison.OrdinalIgnoreCase) || msg.Contains("pacer: Rate exceeded", StringComparison.OrdinalIgnoreCase))
                Provider?.Invoke(new ProviderEvent(Ts(m), Classify(msg, ProviderEventKind.Retry), msg));
            return;
        }

        if (lvl is "ERROR" or "CRITICAL")
        {
            Provider?.Invoke(new ProviderEvent(Ts(m), Classify(msg, ProviderEventKind.Error), msg));
            return;
        }

        if (msg.Contains("vfs cache: cleaned:", StringComparison.Ordinal))
            Provider?.Invoke(new ProviderEvent(Ts(m), ProviderEventKind.CacheCleaned, msg));
        else if (msg.Contains("out of space", StringComparison.OrdinalIgnoreCase))
            Provider?.Invoke(new ProviderEvent(Ts(m), ProviderEventKind.OutOfSpace, msg));
        else if (msg.Contains("low level retry", StringComparison.OrdinalIgnoreCase))
            Provider?.Invoke(new ProviderEvent(Ts(m), Classify(msg, ProviderEventKind.Retry), msg));
    }

    public static ProviderEventKind Classify(string msg, ProviderEventKind fallback)
    {
        if (msg.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) || msg.Contains("Rate Limit Exceeded", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("429", StringComparison.Ordinal) || msg.Contains("Rate exceeded", StringComparison.OrdinalIgnoreCase))
            return ProviderEventKind.RateLimited;
        if (msg.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) || msg.Contains("oauth2", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("401", StringComparison.Ordinal) || msg.Contains("token", StringComparison.OrdinalIgnoreCase) && msg.Contains("expired", StringComparison.OrdinalIgnoreCase))
            return ProviderEventKind.AuthError;
        if (msg.Contains("no such host", StringComparison.OrdinalIgnoreCase) || msg.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("dial tcp", StringComparison.OrdinalIgnoreCase) || msg.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase) || msg.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase))
            return ProviderEventKind.NetworkError;
        if (msg.Contains("no space left", StringComparison.OrdinalIgnoreCase) || msg.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
            return ProviderEventKind.OutOfSpace;
        return fallback;
    }

    private void OnReadStart(Match line, string msg)
    {
        var r = ReadStartRegex().Match(msg);
        if (!r.Success) return;
        _pending[r.Groups["h"].Value] = new Pending
        {
            Path = r.Groups["path"].Value,
            Offset = long.Parse(r.Groups["off"].ValueSpan, CultureInfo.InvariantCulture),
            Size = int.Parse(r.Groups["size"].ValueSpan, CultureInfo.InvariantCulture),
            Start = Ts(line)
        };
        if (_pending.Count > 4096) _pending.Clear(); // safety valve against unmatched lines
    }

    private void OnLookup(string msg)
    {
        var r = LookupRegex().Match(msg);
        if (!r.Success) return;
        var pos = long.Parse(r.Groups["pos"].ValueSpan, CultureInfo.InvariantCulture);
        var present = r.Groups["p"].ValueSpan.SequenceEqual("true");
        Pending? best = null;
        foreach (var p in _pending.Values)
            if (p.Hit is null && p.Offset == pos && (best is null || p.Start > best.Start)) best = p;
        if (best != null)
        {
            best.Hit = present;
            Lookup?.Invoke(best.Path, best.Offset, present, _isPrefetch?.Invoke(best.Path, best.Offset, best.Size, best.Start) == true);
        }
    }

    private void OnReadEnd(Match line, string msg)
    {
        var r = ReadEndRegex().Match(msg);
        if (!r.Success) return;
        var h = r.Groups["h"].Value;
        if (!_pending.Remove(h, out var p)) return;
        var n = int.Parse(r.Groups["n"].ValueSpan, CultureInfo.InvariantCulture);
        var err = r.Groups["err"].Value;
        var end = Ts(line);
        var isEof = err == "EOF";
        var error = err is "<nil>" or "EOF" ? null : err;
        var len = Math.Max(0, n);
        if (len == 0 && error == null && !isEof) return;
        var source = _isPrefetch?.Invoke(p.Path, p.Offset, Math.Max(len, p.Size), p.Start) == true ? ReadSource.Prefetch : ReadSource.Application;
        Read?.Invoke(new AccessEvent(end, _mountId, p.Path, p.Offset, len, p.Hit, (end - p.Start).TotalMilliseconds, source, error));
    }

    private static DateTime Ts(Match m)
    {
        var s = m.Groups["ts"].ValueSpan;
        var fmt = s.Length > 19 ? "yyyy/MM/dd HH:mm:ss." + new string('f', s.Length - 20) : "yyyy/MM/dd HH:mm:ss";
        return DateTime.TryParseExact(s, fmt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt) ? dt : DateTime.Now;
    }
}

/// <summary>
/// Follows a growing log file written by another process. Handles truncation and size-based rotation (the file at the
/// path is replaced) by comparing NTFS file IDs. Opens with full sharing so rclone can keep writing and rotating.
/// </summary>
public sealed class LogTailer : IDisposable
{
    private readonly string _path;
    private readonly Action<string> _onLine;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private FileStream? _stream;
    private ulong _fileId;
    private readonly StringBuilder _partial = new();

    public LogTailer(string path, Action<string> onLine, bool fromEnd = false, TimeSpan? interval = null)
    {
        _path = path;
        _onLine = onLine;
        FromEnd = fromEnd;
        _loop = Task.Run(() => LoopAsync(interval ?? TimeSpan.FromMilliseconds(150), _cts.Token));
    }

    public bool FromEnd { get; }
    public long BytesRead { get; private set; }
    public Exception? LastError { get; private set; }

    private async Task LoopAsync(TimeSpan interval, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        int idleTicks = 0;
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_stream == null)
                {
                    if (File.Exists(_path))
                    {
                        _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                        _fileId = FileId(_stream.SafeFileHandle);
                        if (first && FromEnd) _stream.Seek(0, SeekOrigin.End);
                        first = false;
                    }
                }
                if (_stream != null)
                {
                    if (_stream.Length < _stream.Position) _stream.Seek(0, SeekOrigin.Begin); // truncated
                    int n;
                    bool any = false;
                    while ((n = await _stream.ReadAsync(buffer, ct)) > 0)
                    {
                        any = true;
                        BytesRead += n;
                        var c = decoder.GetChars(buffer, 0, n, chars, 0);
                        Emit(chars.AsSpan(0, c));
                    }
                    if (!any && ++idleTicks % 10 == 0 && Rotated())
                    {
                        _stream.Dispose();
                        _stream = null;
                        continue;
                    }
                    if (any) idleTicks = 0;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LastError = e;
                _stream?.Dispose();
                _stream = null;
            }
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void Emit(ReadOnlySpan<char> chunk)
    {
        while (true)
        {
            var nl = chunk.IndexOf('\n');
            if (nl < 0)
            {
                _partial.Append(chunk);
                return;
            }
            _partial.Append(chunk[..nl]);
            var line = _partial.ToString().TrimEnd('\r');
            _partial.Clear();
            if (line.Length > 0)
            {
                try { _onLine(line); } catch (Exception) { /* consumer errors must not stop tailing */ }
            }
            chunk = chunk[(nl + 1)..];
        }
    }

    private bool Rotated()
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FileId(fs.SafeFileHandle) != _fileId;
        }
        catch (IOException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

    private static ulong FileId(SafeFileHandle h) =>
        GetFileInformationByHandle(h, out var i) ? ((ulong)i.FileIndexHigh << 32) | i.FileIndexLow : 0;

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stream?.Dispose();
        _cts.Dispose();
    }
}
