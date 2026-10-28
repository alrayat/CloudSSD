namespace CloudSSD.Core;

/// <summary>
/// Isolates synchronous filesystem calls that a disconnected FUSE/network drive may never finish.
/// A timeout releases the caller, not its worker slot: repeated retries cannot exhaust the thread pool.
/// </summary>
public sealed class BoundedFileSystem(int concurrency = 2)
{
    public static BoundedFileSystem Shared { get; } = new();
    private readonly SemaphoreSlim _slots = new(concurrency, concurrency);

    public async Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false);
            var task = Task.Factory.StartNew(() =>
            {
                try { token.ThrowIfCancellationRequested(); return work(); }
                finally { _slots.Release(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The drive is not answering filesystem requests. Downloaded data is kept. Retry after the drive recovers.");
        }
    }
}
