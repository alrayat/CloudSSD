using System.Net.NetworkInformation;
using CloudSSD.Core;
using CloudSSD.Mount;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CloudSSD.App.Persistence;

/// <summary>
/// The headless owner of the drives while the desktop window is closed ("Keep Cloud SSD Permanent"). It holds the data
/// folder lock, adopts drives left running by the app (rclone and the TeraBox bridge keep running throughout, so the
/// drive letter never disappears during a hand-over), restores permanent drives at logon, and keeps them alive:
/// waits for the network, retries failed mounts with bounded backoff, re-checks after sleep, and never moves a
/// permanent drive to another letter. It exits when the desktop app asks for the drives (<c>release</c>), when asked to
/// stop, or when there is nothing left to look after.
/// </summary>
public sealed class AgentWorker
{
    public const int ExitOk = 0;
    /// <summary>Another CloudSSD process (normally the desktop app) owns the data folder; nothing to do.</summary>
    public const int ExitLockBusy = 3;

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    /// <summary>Longest wait between mount attempts (5 min; CLOUDSSD_AGENT_MAX_BACKOFF_S overrides it for tests).</summary>
    private static readonly TimeSpan MaxBackoff =
        int.TryParse(Environment.GetEnvironmentVariable("CLOUDSSD_AGENT_MAX_BACKOFF_S"), out var sec) && sec > 0 ? TimeSpan.FromSeconds(sec) : TimeSpan.FromMinutes(5);

    private readonly AppPaths _paths;
    private readonly Dictionary<string, (DateTime Next, TimeSpan Backoff, string? Reason)> _retry = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private AppHost? _host;
    private ILogger? _log;
    private volatile string? _exitMode; // null = running, "release", "stop", "session-end"
    private readonly CancellationTokenSource _mountStop = new();

    public AgentWorker(AppPaths paths) => _paths = paths;

    /// <summary>Interval of the supervision loop (tests shorten it).</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        await using var host = AppHost.Create(_paths);
        _host = host;
        if (!host.TryAcquireInstanceLock()) return ExitLockBusy;
        _log = host.LoggerFactory.CreateLogger("Agent");
        _log.LogInformation("Background agent started (pid {Pid}); keeps permanent drives available while the window is closed", Environment.ProcessId);

        PowerModeChangedEventHandler power = (_, e) =>
        {
            if (e.Mode != PowerModes.Resume) return;
            _ = host.Mounts.OnSystemResumeAsync();
            ResetBackoff();
            _wake.Release();
        };
        SessionEndingEventHandler ending = (_, _) =>
        {
            // Windows is signing out or shutting down and will end rclone with the session. Record a consistent state;
            // write-back data stays in the cache and is uploaded on the next mount.
            _exitMode = "session-end";
            host.Mounts.DetachAll();
            _wake.Release();
        };
        NetworkAvailabilityChangedEventHandler net = (_, e) => { if (e.IsAvailable) { ResetBackoff(); _wake.Release(); } };
        SystemEvents.PowerModeChanged += power;
        SystemEvents.SessionEnding += ending;
        NetworkChange.NetworkAvailabilityChanged += net;
        try
        {
            using var mountStop = CancellationTokenSource.CreateLinkedTokenSource(ct, _mountStop.Token);
            await using var control = new AgentControlServer(AgentPipe.NameFor(_paths), HandleAsync);
            try { await host.InitializeAsync(restoreMounts: false, ct: mountStop.Token); } // adopts drives the app left running
            catch (OperationCanceledException) when (_exitMode != null) { }
            while (_exitMode is null && !ct.IsCancellationRequested)
            {
                await EnsurePermanentMountsAsync(mountStop.Token);
                if (host.Mounts.Sessions.Count == 0 && !host.Settings.Mounts.Any(m => m.Permanent))
                {
                    _log.LogInformation("No drives to look after; agent exits");
                    break;
                }
                try { await _wake.WaitAsync(Tick, ct); } catch (OperationCanceledException) { break; }
            }

            // A request can interrupt initialization before existing mounts were adopted. Their records must
            // still be handed over (release) or explicitly unmounted (stop), never erased as an empty session set.
            if (_exitMode is "stop" or "release") await host.Mounts.AdoptExistingAsync(CancellationToken.None);
            switch (_exitMode)
            {
                case "stop":
                    _log.LogInformation("Stop requested: unmounting all drives (pending uploads get up to 60 s)");
                    await host.ShutdownAsync(keepMounts: false);
                    break;
                case "session-end":
                    break; // already detached; Windows ends the processes
                default:
                    await host.ShutdownAsync(keepMounts: true);
                    _log.LogInformation(_exitMode == "release" ? "Drives handed over to the CloudSSD window" : "Agent exiting; drives stay mounted");
                    break;
            }
            return ExitOk;
        }
        finally
        {
            SystemEvents.PowerModeChanged -= power;
            SystemEvents.SessionEnding -= ending;
            NetworkChange.NetworkAvailabilityChanged -= net;
        }
    }

    private void ResetBackoff()
    {
        lock (_retry)
            foreach (var k in _retry.Keys.ToList()) _retry[k] = (DateTime.MinValue, Min(MinBackoff, MaxBackoff), _retry[k].Reason);
    }

    private async Task EnsurePermanentMountsAsync(CancellationToken ct)
    {
        var host = _host!;
        foreach (var def in host.Settings.Mounts.Where(m => m.Permanent))
        {
            var s = host.Mounts.Get(def.Id);
            // Running, starting, or rclone's own crash-restart is in charge.
            if (s is { State: MountState.Mounted or MountState.Degraded or MountState.Starting or MountState.Stopping }) { Clear(def.Id); continue; }
            if (s is { RestartScheduled: true }) continue;

            (DateTime Next, TimeSpan Backoff, string? Reason) r;
            lock (_retry) r = _retry.GetValueOrDefault(def.Id, (DateTime.MinValue, Min(MinBackoff, MaxBackoff), null));
            if (DateTime.UtcNow < r.Next) continue;
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                SetRetry(def.Id, r.Backoff, "waiting for a network connection");
                continue;
            }
            lock (_retry) if (_retry.ContainsKey(def.Id)) _retry[def.Id] = (DateTime.UtcNow + r.Backoff, r.Backoff, "mounting…");
            try
            {
                var m = await host.MountAsync(def, ct);
                _log!.LogInformation("Permanent drive {Name} available at {Letter}:", def.DisplayName, m.DriveLetter);
                Clear(def.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                var next = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, r.Backoff.Ticks * 2));
                SetRetry(def.Id, next, e.Message);
                _log!.LogWarning("Permanent drive {Name} not available yet ({Error}); retrying in {Delay:0} s", def.DisplayName,
                    Diagnostics.Redactor.Redact(e.Message), r.Backoff.TotalSeconds);
            }
        }
    }

    private void SetRetry(string id, TimeSpan backoff, string reason)
    {
        lock (_retry)
        {
            var current = _retry.GetValueOrDefault(id, (DateTime.MinValue, Min(MinBackoff, MaxBackoff), null));
            _retry[id] = (DateTime.UtcNow + current.Backoff, backoff, reason);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void Clear(string id)
    {
        lock (_retry) _retry.Remove(id);
    }

    private AgentStatus Status()
    {
        var host = _host!;
        var mounts = host.Mounts.Sessions.Select(s => new AgentMountStatus(s.Id, s.Definition.DisplayName, s.DriveLetter + ":", s.State.ToString(),
            s.Message, host.Settings.Mounts.FirstOrDefault(m => m.Id == s.Id)?.Permanent ?? false)).ToList();
        List<string> waiting;
        lock (_retry)
            waiting = _retry.Select(kv => $"{host.Settings.Mounts.FirstOrDefault(m => m.Id == kv.Key)?.DisplayName ?? kv.Key}: " +
                                          $"{Diagnostics.Redactor.Redact(kv.Value.Reason ?? "")} (next try {kv.Value.Next.ToLocalTime():T})").ToList();
        return new AgentStatus(Environment.ProcessId, _started, mounts, waiting);
    }

    private async Task<(AgentResponse, Func<Task>?)> HandleAsync(string cmd)
    {
        if (cmd == "stop" && _host != null)
        {
            try
            {
                foreach (var session in _host.Mounts.Sessions)
                    await _host.Mounts.EnsureCanUnmountAsync(session.Id);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                return (new AgentResponse(false, Diagnostics.Redactor.Redact(e.Message), Status: Status()), null);
            }
        }
        (AgentResponse, Func<Task>?) result = cmd switch
        {
            "status" => (new AgentResponse(true, Status: Status()), null),
            "release" or "stop" => (new AgentResponse(true, Status: Status()), () =>
            {
                _exitMode = cmd;
                _mountStop.Cancel();
                _wake.Release();
                return Task.CompletedTask;
            }),
            _ => (new AgentResponse(false, "Unknown command"), null),
        };
        return result;
    }
}
