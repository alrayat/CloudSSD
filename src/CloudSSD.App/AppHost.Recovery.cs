using System.Collections.Concurrent;
using CloudSSD.Core;
using CloudSSD.Games;
using Microsoft.Extensions.Logging;

namespace CloudSSD.App;

public sealed partial class AppHost
{
    private readonly CancellationTokenSource _recoveryStop = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _transfers = new();
    private Task? _recoveryTask;
    public TransferJournal Transfers { get; private set; } = null!;

    private void StartRecovery()
    {
        Transfers.RecoverInterrupted();
        // The process may have stopped between persisting transfer success and marking its action ready.
        foreach (var job in Transfers.All().Where(j => j.State == TransferState.Completed)) CompletionActions.MarkComplete(job.Id);
        _recoveryTask = Task.Run(async () =>
        {
            while (!_recoveryStop.IsCancellationRequested)
            {
                foreach (var job in Transfers.All().Where(j => j.State is TransferState.Interrupted or TransferState.Waiting).ToList())
                {
                    if (_recoveryStop.IsCancellationRequested) return;
                    if (DateTime.UtcNow - job.UpdatedUtc < TimeSpan.FromSeconds(10)) continue;
                    if (Library.Get(job.GameId) is not { } game) { Transfers.Save(job with { State = TransferState.Failed, Detail = "Game removed from library." }); continue; }
                    try
                    {
                        if (game.Mode != ExecutionMode.FullLocal && Mounts.Get(game.MountId) is not { State: MountState.Mounted }) continue;
                        _log.LogInformation("Resuming {Kind} for {Game}; cached data is preserved", job.Kind, game.Name);
                        if (job.Kind == TransferKind.FullCache) await FullyCacheAsync(game, null, _recoveryStop.Token);
                        else if (job.Destination != null) await InstallLocalAsync(game, job.Destination, null, _recoveryStop.Token);
                    }
                    catch (OperationCanceledException) when (_recoveryStop.IsCancellationRequested) { return; }
                    catch (Exception e) { _log.LogWarning("Transfer recovery for {Game}: {Error}", game.Name, Diagnostics.Redactor.Redact(e.Message)); }
                }
                try { await Task.Delay(TimeSpan.FromSeconds(30), _recoveryStop.Token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    private async Task<T> RunTransferAsync<T>(GameEntry game, TransferKind kind, string? destination,
        Func<Action<long, long, string?>, CancellationToken, Task<T>> run, Func<T, bool> complete, CancellationToken ct,
        CompletionAction? afterCompletion)
    {
        var id = kind + ":" + game.Id;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _recoveryStop.Token);
        if (!_transfers.TryAdd(id, linked)) throw new InvalidOperationException("This transfer is already active. Its progress appears in the game details.");
        var job = new TransferJob(id, game.Id, kind, destination, TransferState.Running, 0, 0, "Preparing…", DateTime.UtcNow);
        try
        {
            if (afterCompletion is { } action) ArmCompletion(id, game.Name, action, game.Id);
            Transfers.Save(job);
            StateChanged?.Invoke();
            var last = DateTime.MinValue;
            var result = await run((bytes, total, detail) =>
            {
                lock (linked)
                {
                    job = job with { Bytes = bytes, Total = total, Detail = detail, UpdatedUtc = DateTime.UtcNow };
                    if (DateTime.UtcNow - last < TimeSpan.FromSeconds(1)) return;
                    last = DateTime.UtcNow;
                    Transfers.Save(job);
                }
                StateChanged?.Invoke();
            }, linked.Token);
            var success = complete(result);
            Transfers.Save(job with { State = success ? TransferState.Completed : TransferState.Failed, UpdatedUtc = DateTime.UtcNow });
            if (success) CompletionActions.MarkComplete(id);
            return result;
        }
        catch (OperationCanceledException)
        {
            Transfers.Save(job with { State = _recoveryStop.IsCancellationRequested ? TransferState.Interrupted : TransferState.Paused,
                Detail = "Downloaded data kept.", UpdatedUtc = DateTime.UtcNow });
            throw;
        }
        catch (Exception e)
        {
            var retry = e is IOException or HttpRequestException;
            Transfers.Save(job with { State = retry ? TransferState.Waiting : TransferState.Failed,
                Detail = Diagnostics.Redactor.Redact(e.Message), UpdatedUtc = DateTime.UtcNow });
            throw;
        }
        finally { _transfers.TryRemove(id, out _); StateChanged?.Invoke(); }
    }

    public void PauseTransfers(string gameId)
    {
        foreach (var job in Transfers.All().Where(j => j.GameId == gameId && j.State != TransferState.Completed))
        {
            if (_transfers.TryGetValue(job.Id, out var active)) active.Cancel();
            else Transfers.Save(job with { State = TransferState.Paused, UpdatedUtc = DateTime.UtcNow });
        }
        StateChanged?.Invoke();
    }

    public void ResumeTransfers(string gameId)
    {
        foreach (var job in Transfers.All().Where(j => j.GameId == gameId && j.State is TransferState.Paused or TransferState.Failed))
            Transfers.Save(job with { State = TransferState.Interrupted, UpdatedUtc = DateTime.MinValue });
        StateChanged?.Invoke();
    }

    public Task<HydrateResult> FullyCacheAsync(GameEntry game, IProgress<HydrateProgress>? progress, CancellationToken ct,
        CompletionAction? afterCompletion = null) =>
        RunTransferAsync(game, TransferKind.FullCache, null, (checkpoint, token) => FullyCacheCoreAsync(game,
            new InlineProgress<HydrateProgress>(p => { checkpoint(p.DoneBytes, p.TotalBytes, p.Current); progress?.Report(p); }), token), r => r.Complete, ct, afterCompletion);

    public Task<Mount.RcloneResult> InstallLocalAsync(GameEntry game, string destination, IProgress<LocalCopyProgress>? progress, CancellationToken ct,
        CompletionAction? afterCompletion = null) =>
        RunTransferAsync(game, TransferKind.FullLocal, destination, async (checkpoint, token) =>
        {
            var mount = Settings.Mounts.FirstOrDefault(m => m.Id == game.MountId) ?? throw new InvalidOperationException("Cloud drive is not configured.");
            var rclone = Rclone ?? throw new InvalidOperationException("rclone was not found.");
            var result = await Installer.CopyAsync(rclone, mount, game.RootPath, destination, true,
                new InlineProgress<LocalCopyProgress>(p => { checkpoint(p.Bytes, p.TotalBytes, "Downloading…"); progress?.Report(p); }), token);
            if (result.Success)
            {
                game.LocalRoot = destination;
                game.Mode = ExecutionMode.FullLocal;
                game.Readiness = ReadinessStatus.ReadyToLaunch;
                game.ReadinessDetail = "Installed locally at " + destination;
                Library.Save(game);
            }
            return result;
        }, r => r.Success, ct, afterCompletion);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    private async Task StopRecoveryAsync()
    {
        _recoveryStop.Cancel();
        if (_completionTask != null) await _completionTask;
        if (_recoveryTask != null) await _recoveryTask;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!_transfers.IsEmpty && DateTime.UtcNow < until) await Task.Delay(50);
    }
}
