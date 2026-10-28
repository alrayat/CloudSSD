using System.Collections.ObjectModel;
using CloudSSD.App;
using CloudSSD.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CloudSSD.Games;

namespace CloudSSD.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppHost host)
    {
        Host = host;
        Pages = new ObservableCollection<PageViewModel>
        {
            new DashboardViewModel(host, Navigate),
            new CloudDrivesViewModel(host),
            new GameLibraryViewModel(host),
            new AcceleratorViewModel(host),
            new SmartCacheViewModel(host),
            new PerformanceViewModel(host),
            new SettingsViewModel(host),
            new DiagnosticsViewModel(host),
        };
        _selected = Pages[0];
        host.StateChanged += () => Ui(() =>
        {
            Selected.OnStateChanged();
            UpdateStatus(host.Metrics.Latest);
        });
        host.Metrics.Updated += m => Ui(() =>
        {
            Selected.OnMetrics(m);
            UpdateStatus(m);
        });
    }

    public AppHost Host { get; }
    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _selected;
    [ObservableProperty] private string _drivesStatus = "No drives mounted";
    [ObservableProperty] private string _rateStatus = "";
    [ObservableProperty] private string _engineStatus = "";
    [ObservableProperty] private string _gameStatus = "";
    [ObservableProperty] private string _completionStatus = "After completion…";
    [RelayCommand] private void ShowCompletionActions() => new Views.CompletionActionsWindow(Host) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
    [RelayCommand] private void CancelCompletionActions() { foreach (var j in Host.CompletionActions.All().Where(j => j.State is CompletionState.Waiting or CompletionState.Countdown)) Host.CancelCompletion(j.Id); }
    [ObservableProperty] private string _version = "CloudSSD / " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    partial void OnSelectedChanged(PageViewModel value) => value?.OnShown();

    public void Navigate(string title)
    {
        var p = Pages.FirstOrDefault(x => x.Title == title);
        if (p != null) Selected = p;
    }

    [RelayCommand]
    private void NavigatePage(string index)
    {
        if (int.TryParse(index, out var number) && number >= 1 && number <= Pages.Count)
            Selected = Pages[number - 1];
    }

    private void UpdateStatus(EngineMetrics m)
    {
        var pending = Host.CompletionActions.All().Where(j => j.State is CompletionState.Waiting or CompletionState.Countdown).ToList();
        var due = pending.Where(j => j.DueUtc != null).OrderBy(j => j.DueUtc).FirstOrDefault();
        CompletionStatus = due != null ? $"{due.Action} in {Math.Max(0, (int)(due.DueUtc!.Value - DateTime.UtcNow).TotalSeconds)}s — {due.Name}" : pending.Count > 0 ? $"After completion ({pending.Count} scheduled)…" : "After completion…";
        var sessions = Host.Mounts.Sessions;
        DrivesStatus = sessions.Count == 0 ? "No drives mounted"
            : string.Join("  ", sessions.Select(s => $"{s.DriveLetter}: {s.State}"));
        RateStatus = m.DownloadBytesPerSec is { } r ? $"↓ {Bandwidth.BytesPerSecondToMbps(r):0.0} Mbps" : "";
        EngineStatus = Host.DiskPressure ? "Prefetch paused (disk space)" : Host.Engine.Scheduler.Active > 0 ? $"Prefetching ({Host.Engine.Queue.Count} queued)" : "";
        GameStatus = Host.CurrentLaunch is { State: Games.LaunchState.Running } l ? $"▶ {l.Game.Name}" : "";
    }

    private static void Ui(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) a();
        else d.InvokeAsync(a);
    }
}
