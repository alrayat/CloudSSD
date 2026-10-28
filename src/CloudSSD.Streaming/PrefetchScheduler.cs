using System.Buffers;
using System.Diagnostics;
using CloudSSD.Core;

namespace CloudSSD.Streaming;

public sealed record PrefetchResult(long Bytes, TimeSpan Elapsed, string? Error);

public interface IPrefetchExecutor
{
    Task<PrefetchResult> FetchAsync(PrefetchItem item, CancellationToken ct);
}

/// <summary>
/// Prefetches by reading the block through the mounted drive. rclone's VFS (full cache mode) stores the downloaded
/// range in its sparse cache file, so CloudSSD adds no second cache layer. Each read is registered with the mount's
/// attribution registry so telemetry does not mistake it for a game read.
/// </summary>
public sealed class MountReadExecutor : IPrefetchExecutor
{
    private readonly Func<string, string?> _mountRoot;
    private readonly Func<string, ReadAttributionRegistry?> _attribution;

    public MountReadExecutor(Func<string, string?> mountRoot, Func<string, ReadAttributionRegistry?> attribution)
    {
        _mountRoot = mountRoot;
        _attribution = attribution;
    }

    public async Task<PrefetchResult> FetchAsync(PrefetchItem item, CancellationToken ct)
    {
        var root = _mountRoot(item.MountId);
        if (root is null) return new PrefetchResult(0, TimeSpan.Zero, "mount not available");
        var path = Path.Combine(root, item.MountPath.Replace('/', '\\'));
        var sw = Stopwatch.StartNew();
        using var reg = _attribution(item.MountId)?.Register(item.MountPath, item.Offset, item.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(item.Length, 1 << 20));
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (item.Offset >= fs.Length) return new PrefetchResult(0, sw.Elapsed, null);
            fs.Seek(item.Offset, SeekOrigin.Begin);
            long remaining = Math.Min(item.Length, fs.Length - item.Offset), total = 0;
            while (remaining > 0)
            {
                var n = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (n == 0) break;
                total += n;
                remaining -= n;
            }
            return new PrefetchResult(total, sw.Elapsed, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PrefetchResult(0, sw.Elapsed, e.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>Token bucket for background (P2/P3) prefetch bandwidth.</summary>
internal sealed class RateLimiter
{
    private double _tokens;
    private long _last = Stopwatch.GetTimestamp();
    private readonly object _gate = new();

    public async Task WaitAsync(long bytes, Func<double> rate, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var r = rate();
                var now = Stopwatch.GetTimestamp();
                _tokens = Math.Min(r, _tokens + (now - _last) / (double)Stopwatch.Frequency * r); // ≤1 s burst
                _last = now;
                if (_tokens >= bytes || r <= 0) { _tokens -= bytes; return; }
                wait = TimeSpan.FromSeconds((bytes - _tokens) / r);
            }
            await Task.Delay(wait > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : wait, ct);
        }
    }
}

/// <summary>
/// Runs prefetch work from a <see cref="PrefetchQueue"/> under the <see cref="SchedulingPolicy"/>: dynamic concurrency,
/// foreground protection, background rate limiting and cancellation.
/// </summary>
public sealed class PrefetchScheduler : IAsyncDisposable
{
    private readonly PrefetchQueue _queue;
    private readonly IPrefetchExecutor _executor;
    private readonly NetworkGovernor _governor;
    private readonly IForegroundMonitor _foreground;
    private readonly Func<long, bool> _mayUseDisk;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Task _loop;
    private readonly RateLimiter _background = new();
    private readonly Dictionary<PrefetchItem, CancellationTokenSource> _running = new();
    private int _active;

    public PrefetchScheduler(PrefetchQueue queue, IPrefetchExecutor executor, NetworkGovernor governor, IForegroundMonitor foreground, Func<long, bool>? mayUseDisk = null)
    {
        _queue = queue;
        _executor = executor;
        _governor = governor;
        _foreground = foreground;
        _mayUseDisk = mayUseDisk ?? (_ => true);
        // Items that leave the queue unfetched are reported as completions so nobody waits on them forever.
        _queue.Dropped += (item, reason) =>
        {
            try { Completed?.Invoke(item, new PrefetchResult(0, TimeSpan.Zero, "dropped: " + reason)); } catch (Exception) { }
        };
        _loop = Task.Run(LoopAsync);
    }

    public event Action<PrefetchItem, PrefetchResult>? Completed;

    public PrefetchQueue Queue => _queue;
    public int Active => Volatile.Read(ref _active);
    public long CompletedCount;
    public long FailedCount;
    public long BytesFetched;
    public bool Paused { get; set; }
    public SchedulingDecision? LastDecision { get; private set; }

    public void Nudge()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    public EnqueueResult Enqueue(PrefetchItem item, Priority p, double confidence, string predictor)
    {
        var r = _queue.Enqueue(item, p, confidence, predictor);
        if (r is EnqueueResult.Queued or EnqueueResult.Upgraded) Nudge();
        return r;
    }

    /// <summary>Cancels queued items and aborts running ones that match.</summary>
    public int Cancel(Func<PrefetchItem, bool> predicate)
    {
        var n = _queue.Cancel(predicate);
        lock (_running)
            foreach (var (item, cts) in _running)
                if (predicate(item)) { cts.Cancel(); n++; }
        return n;
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(TimeSpan.FromMilliseconds(200), ct); }
            catch (OperationCanceledException) { break; }

            while (!ct.IsCancellationRequested && !Paused)
            {
                var decision = SchedulingPolicy.Decide(_governor, _foreground, !_mayUseDisk(_governor.BlockSize));
                LastDecision = decision;
                if (Active >= decision.MaxConcurrency) break;
                var item = _queue.TryDequeue(decision.LowestAllowed);
                if (item is null) break;
                Interlocked.Increment(ref _active);
                _ = RunAsync(item, decision, ct);
            }
        }
    }

    private async Task RunAsync(PrefetchItem item, SchedulingDecision decision, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        lock (_running) _running[item] = cts;
        PrefetchResult result;
        try
        {
            if (item.Priority >= Priority.P2Likely && decision.ThrottleBackground)
                await _background.WaitAsync(item.Length, () => _governor.BackgroundBytesPerSec, cts.Token);
            result = await _executor.FetchAsync(item, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new PrefetchResult(0, TimeSpan.Zero, "cancelled");
        }
        catch (Exception e)
        {
            // Never leak the active counter or leave the item "in flight" because of an unexpected error.
            result = new PrefetchResult(0, TimeSpan.Zero, e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            lock (_running) _running.Remove(item);
        }
        var concurrent = Math.Max(1, Active);
        Interlocked.Decrement(ref _active);
        var ok = result.Error is null;
        _queue.Complete(item, ok);
        if (ok)
        {
            Interlocked.Increment(ref CompletedCount);
            Interlocked.Add(ref BytesFetched, result.Bytes);
            _governor.Record(result.Bytes, result.Elapsed, concurrent);
        }
        else Interlocked.Increment(ref FailedCount);
        try { Completed?.Invoke(item, result); } catch (Exception) { }
        Nudge();
    }

    public int RunningOwned(string owner)
    {
        lock (_running) return _running.Keys.Count(i => i.Owner == owner);
    }

    /// <summary>Waits until no queued or running work remains at or above <paramref name="priority"/>, or the timeout passes.</summary>
    public async Task<bool> DrainAsync(Priority priority, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (_queue.CountAtOrAbove(priority) == 0 && Active == 0) return true;
            await Task.Delay(100, ct);
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch (Exception) { }
        _cts.Dispose();
    }
}
