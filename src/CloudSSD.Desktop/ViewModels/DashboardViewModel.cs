using System.Collections.ObjectModel;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudSSD.Desktop.ViewModels;

public sealed record SetupStep(string Text, bool Done, string Hint);

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly Action<string> _navigate;

    public DashboardViewModel(AppHost host, Action<string> navigate) : base(host, "Dashboard", "") => _navigate = navigate;

    public ObservableCollection<KeyValueRow> Accounts { get; } = new();
    public ObservableCollection<KeyValueRow> Drives { get; } = new();
    public ObservableCollection<SetupStep> Setup { get; } = new();
    public ObservableCollection<KeyValueRow> Warnings { get; } = new();

    [ObservableProperty] private string _transferRate = Fmt.NA;
    [ObservableProperty] private string _cacheUsage = Fmt.NA;
    [ObservableProperty] private string _cacheDetail = "";
    [ObservableProperty] private double _cacheFraction;
    [ObservableProperty] private string _freeSpace = Fmt.NA;
    [ObservableProperty] private string _engineState = "Idle";
    [ObservableProperty] private string _engineDetail = "";
    [ObservableProperty] private string _hitRate = Fmt.NA;
    [ObservableProperty] private string _gameState = "No game running";
    [ObservableProperty] private string _health = "No drives mounted";
    [ObservableProperty] private string _healthTone = "muted";
    [ObservableProperty] private double?[] _rateHistory = [];
    [ObservableProperty] private bool _setupComplete;

    public override void OnShown() => OnStateChanged();

    public override void OnStateChanged()
    {
        Accounts.ReplaceWith(Host.Remotes.Select(r =>
        {
            var p = Providers.ProviderRegistry.ForBackend(r.Type);
            return new KeyValueRow(r.Name, $"{p?.DisplayName ?? r.Type} · {(p?.Support ?? Providers.SupportLevel.AvailableUntested) switch
            {
                Providers.SupportLevel.Supported => "supported",
                Providers.SupportLevel.AvailableUntested => "via rclone (untested)",
                _ => "unsupported"
            }}", "accent");
        }));
        var sessions = Host.Mounts.Sessions;
        Drives.ReplaceWith(Host.Settings.Mounts.Select(d =>
        {
            var s = sessions.FirstOrDefault(x => x.Id == d.Id);
            var state = s?.State ?? MountState.Stopped;
            return new KeyValueRow($"{(s != null ? s.DriveLetter + ":" : (d.DriveLetter ?? "?") + ":")}  {d.DisplayName}",
                $"{state} · {d.RemoteSpec}{(s?.Message is { } m && state != MountState.Mounted ? " · " + m : "")}", Fmt.Tone(state));
        }));
        var mounted = sessions.Count(s => s.State == MountState.Mounted);
        var degraded = sessions.Count(s => s.State is MountState.Degraded or MountState.Failed);
        (Health, HealthTone) = degraded > 0 ? ($"{degraded} drive(s) need attention", "error")
            : mounted > 0 ? ($"{mounted} drive(s) mounted and healthy", "ok")
            : ("No drives mounted", "muted");

        var deps = Host.Dependencies;
        Warnings.ReplaceWith(deps.Where(d => d.State != DependencyState.Ok).Select(d => new KeyValueRow(d.Name, d.Detail + (d.Remedy != null ? " " + d.Remedy : ""),
            d.State == DependencyState.Warning ? "warn" : "error")));
        var games = Host.Library.All();
        Setup.ReplaceWith([
            new("WinFsp installed", deps.Any(d => d.Name == "WinFsp" && d.State == DependencyState.Ok), "Required for the virtual drive"),
            new("rclone available", Host.Rclone != null, "Bundled with CloudSSD"),
            new("Cloud account connected", Host.Remotes.Count > 0, "Cloud Drives → Connect Google Drive"),
            new("Drive mounted", mounted > 0, "Cloud Drives → Add drive"),
            new("Game added", games.Count > 0, "Game Library → Add game"),
        ]);
        SetupComplete = Setup.All(s => s.Done);
        OnMetrics(Host.Metrics.Latest);
    }

    public override void OnMetrics(EngineMetrics m)
    {
        TransferRate = Fmt.Rate(m.DownloadBytesPerSec);
        RateHistory = Host.Metrics.TransferRate.Snapshot();
        var budget = Host.Settings.Cache.BudgetBytes;
        CacheUsage = m.CacheBytesOnDisk is { } used ? $"{ByteSize.Format(used)} of {ByteSize.Format(budget)}" : Fmt.NA;
        CacheFraction = m.CacheBytesOnDisk is { } u && budget > 0 ? Math.Min(1, u / (double)budget) : 0;
        CacheDetail = m.CacheLogicalBytes is { } l ? $"Files touched: {ByteSize.Format(l)} logical size (sparse — only downloaded ranges use disk)" : "";
        FreeSpace = m.CacheVolumeFreeBytes is { } f ? $"{ByteSize.Format(f)} free on cache drive (reserve {ByteSize.Format(Host.Settings.Cache.MinFreeSpaceBytes)})" : Fmt.NA;
        HitRate = Fmt.Pct(m.HitRate);
        GameState = m.GameProcessState ?? "No game running";
        var e = Host.Engine;
        var sessions = e.Sessions.Count;
        EngineState = !Host.Settings.Streaming.PredictionEnabled ? "Prediction off"
            : Host.DiskPressure ? "Paused (low disk space)"
            : e.Scheduler.Active > 0 ? "Prefetching"
            : sessions > 0 ? "Profiling game"
            : "Idle";
        EngineDetail = $"Queue {e.Queue.Count} · active {e.Scheduler.Active} · completed {e.Scheduler.CompletedCount:N0} · fetched {ByteSize.Format(e.Scheduler.BytesFetched)}";
    }

    [RelayCommand] private void Go(string page) => _navigate(page);
}
