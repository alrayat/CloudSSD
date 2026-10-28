using System.Diagnostics;
using CloudSSD.Core;
using CloudSSD.Games;
using Microsoft.Extensions.Logging;

namespace CloudSSD.App;

public sealed partial class AppHost
{
    public CompletionJournal CompletionActions { get; private set; } = null!;
    private CompletionCoordinator? _completionCoordinator;
    private Task? _completionTask;
    private int _activeImports;
    public volatile bool CompletionUiAvailable;
    private DateTime _lastCompletionUiRequest;

    public void ArmCompletion(string id, string name, CompletionAction action, string? gameId = null,
        string? steamManifest = null, uint? steamAppId = null)
    {
        if (action == CompletionAction.DoNothing) CompletionActions.Cancel(id);
        else CompletionActions.Save(new(id, name, action, gameId, steamManifest, steamAppId));
        StateChanged?.Invoke();
    }

    public void CancelCompletion(string id) { CompletionActions.Cancel(id); StateChanged?.Invoke(); }

    private void StartCompletionActions()
    {
        _completionCoordinator = new(CompletionActions, CompletionReadyAsync, ExecuteCompletionAsync);
        _completionCoordinator.Recover();
        _completionTask = Task.Run(async () =>
        {
            while (!_recoveryStop.IsCancellationRequested)
            {
                try
                {
                    await _completionCoordinator.TickAsync(DateTime.UtcNow, _recoveryStop.Token);
                    StateChanged?.Invoke();
                    await Task.Delay(TimeSpan.FromSeconds(2), _recoveryStop.Token);
                }
                catch (OperationCanceledException) when (_recoveryStop.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    _log.LogWarning("Completion check: {Error}", Diagnostics.Redactor.Redact(e.Message));
                    try { await Task.Delay(TimeSpan.FromSeconds(5), _recoveryStop.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        });
    }

    private async Task<(bool Ready, string Detail)> CompletionReadyAsync(CompletionJob job, CancellationToken ct)
    {
        if (job.SteamManifest != null && job.SteamAppId is { } appId)
        {
            try
            {
                string? cached = null;
                foreach (var mount in Mounts.Sessions)
                {
                    var root = Path.GetFullPath(mount.Root).TrimEnd('\\') + "\\";
                    if (!job.SteamManifest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    cached = SteamManifestCache.Read(mount.CacheDir, mount.Definition.RemotePath,
                        job.SteamManifest[root.Length..].Replace('\\', '/'));
                    break;
                }
                var installed = await BoundedFileSystem.Shared.RunAsync(() =>
                    SteamInstallCompletion.IsInstalled(cached ?? File.ReadAllText(job.SteamManifest), appId), TimeSpan.FromSeconds(5), ct);
                if (!installed) return (false, "Waiting for Steam to finish installation and verification.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
            { return (false, "Waiting for Steam's installation status to become available."); }
        }
        else if (!job.TransferComplete) return (false, "Waiting for a successful transfer and verification.");

        if (job.Action is CompletionAction.ShutDown or CompletionAction.Restart)
        {
            if (!_transfers.IsEmpty || Volatile.Read(ref _activeImports) != 0)
                return (false, "Waiting for other downloads and imports.");
            if (IsLaunching || CurrentLaunch is { State: LaunchState.Starting or LaunchState.Running } ||
                _gameWatcher?.Running.Count > 0) return (false, "Waiting for the running game to close.");
            foreach (var def in Settings.Mounts.Where(m => !m.ReadOnly))
            {
                if (Mounts.Get(def.Id) is not { State: MountState.Mounted })
                    return (false, "Waiting for cloud drives to be available for a safe write check.");
                if (!await Mounts.IsReadyForShutdownAsync(def.Id, ct))
                    return (false, "Waiting for open files and pending cloud writes to finish.");
            }
            // The agent may own the job after a desktop crash/close. Bring back the cancellable UI before counting down.
            if (!CompletionUiAvailable)
            {
                if (DateTime.UtcNow - _lastCompletionUiRequest > TimeSpan.FromMinutes(2))
                {
                    _lastCompletionUiRequest = DateTime.UtcNow;
                    var desktop = Path.Combine(AppPaths.AppDirectory, "CloudSSD.exe");
                    if (File.Exists(desktop))
                    {
                        var start = new ProcessStartInfo(desktop) { UseShellExecute = false, WorkingDirectory = AppPaths.AppDirectory };
                        start.Environment["CLOUDSSD_HOME"] = Paths.DataRoot;
                        start.Environment["CLOUDSSD_HOME"] = Paths.DataRoot;
                        using var process = Process.Start(start);
                    }
                }
                return (false, "Waiting for CloudSSD to show the cancellable countdown.");
            }
        }
        return (true, "Complete.");
    }

    private async Task ExecuteCompletionAsync(CompletionJob job, CancellationToken ct)
    {
        _log.LogInformation("After completion: {Action} for {Name}", job.Action, job.Name);
        switch (job.Action)
        {
            case CompletionAction.LaunchGame:
                if (job.GameId is { } id && Library.Get(id) is { } game) await PlayAsync(game, ct);
                else if (job.SteamAppId is { } app)
                {
                    var client = SteamLaunch.FindClient() ?? throw new InvalidOperationException("Steam is not installed.");
                    using var p = Process.Start(new ProcessStartInfo(client) { UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(client)!, Arguments = $"-applaunch {app}" });
                }
                else throw new InvalidOperationException("The game was removed. Choose it again before scheduling a launch.");
                break;
            case CompletionAction.ShutDown:
            case CompletionAction.Restart:
                // Countdown belongs to this app. shutdown /t > 0 implies /f on Windows; never use it.
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
                    { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(job.Action == CompletionAction.ShutDown ? "/s" : "/r");
                start.ArgumentList.Add("/t"); start.ArgumentList.Add("0");
                using (var p = Process.Start(start) ?? throw new IOException("Windows did not accept the power request."))
                {
                    await p.WaitForExitAsync(ct);
                    if (p.ExitCode != 0) throw new IOException($"Windows declined the power request ({p.ExitCode}).");
                }
                break;
        }
        StateChanged?.Invoke();
    }
}
