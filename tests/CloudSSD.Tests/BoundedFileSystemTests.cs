using CloudSSD.Core;

namespace CloudSSD.Tests;

public sealed class BoundedFileSystemTests
{
    [Fact]
    public async Task Hung_native_call_times_out_without_spawning_more_workers_and_recovers_after_release()
    {
        var fs = new BoundedFileSystem(1);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var first = fs.RunAsync(() => { started.SetResult(); release.Wait(); completed.SetResult(); return 1; }, TimeSpan.FromMilliseconds(300));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => first);
            var extraWorkers = 0;
            for (int i = 0; i < 3; i++)
                await Assert.ThrowsAsync<TimeoutException>(() => fs.RunAsync(() => Interlocked.Increment(ref extraWorkers), TimeSpan.FromMilliseconds(50)));
            Assert.Equal(0, extraWorkers);
        }
        finally { release.Set(); }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, await fs.RunAsync(() => 42, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_and_work_never_runs_on_the_callers_thread()
    {
        var fs = new BoundedFileSystem(1);
        var caller = Environment.CurrentManagedThreadId;
        Assert.NotEqual(caller, await fs.RunAsync(() => Environment.CurrentManagedThreadId, TimeSpan.FromSeconds(5)));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.RunAsync<int>(() => throw new Exception("must not run"), TimeSpan.FromSeconds(1), cancel.Token));
        Assert.Equal(7, await fs.RunAsync(() => 7, TimeSpan.FromSeconds(5)));
    }
}
