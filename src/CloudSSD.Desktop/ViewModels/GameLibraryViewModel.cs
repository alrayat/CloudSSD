using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.Games;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudSSD.Desktop.ViewModels;

public sealed partial class GameItem : ObservableObject
{
    public GameItem(GameEntry g) => Update(g);

    public GameEntry Entry { get; private set; } = null!;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _mode = "";
    [ObservableProperty] private string _compat = "";
    [ObservableProperty] private string _compatTone = "muted";
    [ObservableProperty] private string _readiness = "";
    [ObservableProperty] private string _readinessTone = "muted";
    [ObservableProperty] private string _size = "";
    [ObservableProperty] private string _initial = "";

    public void Update(GameEntry g)
    {
        Entry = g;
        Name = g.Name;
        Initial = g.Name.Length > 0 ? g.Name[..1].ToUpperInvariant() : "?";
        Mode = g.Mode switch { ExecutionMode.CloudDirect => "Cloud Direct", ExecutionMode.HybridOptimized => "Hybrid Optimized", _ => "Full Local" };
        Compat = g.Compatibility?.Summary ?? "Not analyzed";
        CompatTone = Fmt.Tone(g.Compatibility?.Level ?? CompatibilityLevel.NotAnalyzed);
        Readiness = ReadinessText.Describe(g.Readiness);
        ReadinessTone = Fmt.Tone(g.Readiness);
        Size = g.Compatibility is { } c ? $"{ByteSize.Format(c.TotalBytes)} · {c.FileCount:N0} files" : "";
    }
}

public sealed partial class GameLibraryViewModel : PageViewModel
{
    public GameLibraryViewModel(AppHost host) : base(host, "Game Library", "")
    {
        FilteredGames = CollectionViewSource.GetDefaultView(Games);
        FilteredGames.Filter = item => item is GameItem game &&
            game.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public ObservableCollection<GameItem> Games { get; } = new();
    public ICollectionView FilteredGames { get; }
    [ObservableProperty] private string _searchText = "";
    public string LibraryCount => SearchText.Trim().Length == 0
        ? $"{Games.Count} game{(Games.Count == 1 ? "" : "s")}" : $"{FilteredGames.Cast<GameItem>().Count()} of {Games.Count} games";
    public bool NoSearchResults => Games.Count > 0 && FilteredGames.IsEmpty;

    partial void OnSearchTextChanged(string value) => RefreshFilter();

    private void RefreshFilter()
    {
        FilteredGames.Refresh();
        if (Selected == null || !FilteredGames.Contains(Selected))
            Selected = FilteredGames.Cast<GameItem>().FirstOrDefault();
        OnPropertyChanged(nameof(LibraryCount));
        OnPropertyChanged(nameof(NoSearchResults));
    }
    public ObservableCollection<Issue> Issues { get; } = new();
    public ObservableCollection<KeyValueRow> Details { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private GameItem? _selected;

    [ObservableProperty] private string _readinessDetail = "";
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private bool _isRunning;

    public string[] CompletionChoices { get; } = ["Do nothing", "Launch game", "Shut down PC", "Restart PC"];
    [ObservableProperty] private int _completionChoice;

    public bool HasSelection => Selected != null;

    public override void OnShown() => Reload();

    public override void OnStateChanged()
    {
        var running = Host.IsLaunching || Host.CurrentLaunch is { State: LaunchState.Running or LaunchState.Starting };
        IsRunning = running;
        Reload();
    }

    private void Reload()
    {
        var id = Selected?.Entry.Id;
        var all = Host.Library.All().OrderByDescending(g => g.LastPlayed ?? g.Added).ToList();
        Games.ReplaceWith(all.Select(g => new GameItem(g)));
        Selected = Games.FirstOrDefault(g => g.Entry.Id == id) ?? Games.FirstOrDefault();
        RefreshFilter();
    }

    private string? _choiceGameId;
    partial void OnSelectedChanged(GameItem? value)
    {
        if (_choiceGameId != value?.Entry.Id) CompletionChoice = 0;
        _choiceGameId = value?.Entry.Id;
        ShowDetails();
    }

    private void ShowDetails()
    {
        Issues.Clear();
        Details.Clear();
        if (Selected is null) return;
        var g = Selected.Entry;
        var c = g.Compatibility;
        if (c != null) foreach (var i in c.Issues.OrderByDescending(i => i.Severity)) Issues.Add(i);
        var mount = Host.Settings.Mounts.FirstOrDefault(m => m.Id == g.MountId);
        var summary = Host.Profiles.Summary(g.Id, Host.Settings.Streaming.StartupWindow);
        Details.ReplaceWith([
            new("Drive", mount != null ? $"{mount.DisplayName} ({mount.RemoteSpec})" : "missing drive"),
            new("Folder", string.IsNullOrEmpty(g.RootPath) ? "(drive root)" : g.RootPath),
            new("Executable", g.Executable ?? "not found"),
            new("Arguments", string.IsNullOrWhiteSpace(g.Arguments) ? "—" : g.Arguments),
            new("Mode", Selected.Mode + (g.LocalRoot != null && g.Mode != ExecutionMode.CloudDirect ? $" · {g.LocalRoot}" : "")),
            new("Engine", c?.Engine ?? "unknown"),
            new("Architecture", c?.Architecture ?? "unknown"),
            new("Size", c != null ? $"{ByteSize.Format(c.TotalBytes)} in {c.FileCount:N0} files" : "unknown"),
            new("Full download", c?.FullDownloadEstimate is { } t ? $"{Bandwidth.FormatDuration(t)} at {c.EstimateMbps:0} Mbps" : "unknown"),
            new("Access profile", summary.Sessions > 0 ? $"{summary.Sessions} launch(es), startup set ≈ {ByteSize.Format(summary.StartupBytesEstimate)}" : "none yet (first launch will profile)"),
            new("Verified modes", g.VerifiedModes.Count > 0 ? string.Join(", ", g.VerifiedModes) : "none — not yet verified by a launch", g.VerifiedModes.Count > 0 ? "ok" : "muted"),
            new("Game-Aware", g.GameAwareEnabled ? "telemetry pipe enabled (needs an authorized game plugin)" : "off (Universal Mode)"),
        ]);
        ReadinessDetail = g.ReadinessDetail ?? "Run Optimize & Prepare to check readiness.";
        LastResult = g.LastLaunchResult ?? "Never launched.";
        foreach (var job in Host.Transfers.All().Where(j => j.GameId == g.Id))
            Details.Add(new("Download", $"{job.Kind}: {job.State} · {ByteSize.Format(job.Bytes)} / {ByteSize.Format(job.Total)} · {job.Detail}"));
    }

    [RelayCommand]
    private async Task AddGame()
    {
        if (!Host.Mounts.Sessions.Any(s => s.State == MountState.Mounted))
        {
            Say("Mount a cloud drive first (Cloud Drives).", "warn");
            return;
        }
        var w = new Views.AddGameWindow(Host) { Owner = System.Windows.Application.Current.MainWindow };
        if (w.ShowDialog() != true || w.MountId is null || w.Folder is null) return;
        await RunAsync(async () =>
        {
            var g = await Host.AddGameAsync(w.MountId, w.Folder, w.GameName);
            Reload();
            Selected = Games.FirstOrDefault(x => x.Entry.Id == g.Id);
            Say($"Added {g.Name}: {g.Compatibility?.Summary}. Run Optimize & Prepare before the first launch.", "ok");
        });
    }

    [RelayCommand]
    private void ImportGame()
    {
        var w = new Views.ImportGameWindow(Host) { Owner = System.Windows.Application.Current.MainWindow };
        w.ShowDialog();
        Reload();
        if (w.ImportedGame != null) Say($"{w.ImportedGame.Name} was imported to the cloud and added to your library.", "ok");
    }

    [RelayCommand]
    private async Task Prepare()
    {
        if (Selected is null) return;
        var g = Selected.Entry;
        var w = new Views.PrepareWindow(Host, g) { Owner = System.Windows.Application.Current.MainWindow };
        w.ShowDialog();
        Reload();
        await Task.CompletedTask;
    }

    [ObservableProperty] private string _fullCacheLabel = "Fully cache…";
    private CancellationTokenSource? _fullCacheCts;

    /// <summary>Mode C "Fully cached": whole game into the local cache, verified. Click again while running to stop.</summary>
    [RelayCommand]
    private async Task FullyCache()
    {
        if (_fullCacheCts is { } running) { running.Cancel(); return; }
        if (Selected is null) return;
        var g = Selected.Entry;
        if (Host.Mounts.Get(g.MountId) is not { State: MountState.Mounted })
        {
            Say("Mount the game's cloud drive first.", "warn");
            return;
        }
        (long GameBytes, long AlreadyCached, long Budget, long Free, bool Fits, string Explanation) check;
        try { check = await Task.Run(() => Host.FullCacheCheck(g)); }
        catch (Exception e) { Say(Diagnostics.Redactor.Redact(e.Message), "error"); return; }
        if (!check.Fits) { Say("Fully cached is not possible yet: " + check.Explanation, "warn"); return; }
        if (!Confirm($"Fully cache {g.Name}?\n\n{check.Explanation}\nThis is NOT the storage-saving mode: the whole game ({ByteSize.Format(check.GameBytes)}) will be kept on your local disk." +
                     $"\n\nAt the measured TeraBox free-plan speed (about 1 MB/s) this takes roughly {Bandwidth.FormatDuration(TimeSpan.FromSeconds((check.GameBytes - Math.Min(check.GameBytes, check.AlreadyCached)) / 1_000_000.0))}." +
                     " You can stop at any time; what is already downloaded stays cached."))
            return;
        _fullCacheCts = new CancellationTokenSource();
        FullCacheLabel = "Stop caching";
        var progress = new Progress<Games.HydrateProgress>(p =>
            Say($"Caching {g.Name}: {ByteSize.Format(p.DoneBytes)} of {ByteSize.Format(p.TotalBytes)} ({p.DoneBytes / (double)Math.Max(1, p.TotalBytes):P0}), " +
                $"{p.FilesDone}/{p.Files} files, {p.MBps:0.00} MB/s" +
                (p.MBps > 0 ? $", about {Bandwidth.FormatDuration(TimeSpan.FromSeconds((p.TotalBytes - p.DoneBytes) / 1e6 / p.MBps))} left" : ""), "accent"));
        try
        {
            var r = await Host.FullyCacheAsync(g, progress, _fullCacheCts.Token, (CompletionAction)CompletionChoice);
            Say(r.Message, r.Complete ? "ok" : "warn");
        }
        catch (OperationCanceledException) { Say("Caching stopped. Everything downloaded so far stays in the cache.", "muted"); }
        catch (Exception e) { Say("Caching failed: " + Diagnostics.Redactor.Redact(e.Message), "error"); }
        finally
        {
            _fullCacheCts.Dispose();
            _fullCacheCts = null;
            FullCacheLabel = "Fully cache…";
            Reload();
        }
    }

    [RelayCommand]
    private async Task Play()
    {
        if (Selected is null) return;
        var g = Selected.Entry;
        if (g.Compatibility?.Level is CompatibilityLevel.NeedsLocalInstall or CompatibilityLevel.Incompatible && g.Mode != ExecutionMode.FullLocal
            && !Confirm($"{g.Name} is marked \"{g.Compatibility.Summary}\".\n\n{g.Compatibility.Issues.FirstOrDefault(i => i.Severity == Severity.Error)?.Message}\n\nTry to launch anyway?"))
            return;
        if (g.Readiness is ReadinessStatus.Unknown or ReadinessStatus.MayRequireAdditionalDownloads)
            Say("Launching. Data that is not cached will be fetched from the cloud on demand — expect loading waits on first launch.", "warn");
        await RunAsync(async () =>
        {
            Say("Checking the drive and starting the game through its launcher…", "muted");
            var launch = await Host.PlayAsync(g);
            Say($"{g.Name} started ({launch.Mode}). Telemetry is being recorded to improve the next launch.", "ok");
        });
        OnStateChanged();
    }

    [RelayCommand]
    private void PauseDownloads()
    {
        if (Selected == null) return;
        Host.PauseTransfers(Selected.Entry.Id);
        Say("Download paused. Progress is kept across restarts.", "muted");
    }

    [RelayCommand]
    private void ResumeDownloads()
    {
        if (Selected == null) return;
        Host.ResumeTransfers(Selected.Entry.Id);
        Say("Download queued to resume automatically.", "ok");
    }

    [RelayCommand]
    private void Settings()
    {
        if (Selected is null) return;
        var w = new Views.GameSettingsWindow(Host, Selected.Entry) { Owner = System.Windows.Application.Current.MainWindow };
        if (w.ShowDialog() == true)
        {
            Host.Library.Save(Selected.Entry);
            Reload();
            Say("Game settings saved.", "ok");
        }
    }

    [RelayCommand]
    private void InstallLocally()
    {
        if (Selected is null) return;
        var w = new Views.FullLocalWindow(Host, Selected.Entry) { Owner = System.Windows.Application.Current.MainWindow };
        w.ShowDialog();
        Reload();
    }

    [RelayCommand]
    private async Task Reanalyze()
    {
        if (Selected is null) return;
        var g = Selected.Entry;
        await RunAsync(async () =>
        {
            var fresh = await Host.AddGameAsync(g.MountId, g.RootPath, g.Name);
            // Keep the original identity (profile, verified modes, settings); take the new analysis.
            Host.Library.Remove(fresh.Id);
            g.Compatibility = fresh.Compatibility;
            g.Executable ??= fresh.Executable;
            Host.Library.Save(g);
            Reload();
        }, "Analysis updated.");
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null) return;
        if (!Confirm($"Remove {Selected.Name} from the library?\n\nCloud files, local copies and save games are not deleted.")) return;
        Host.Library.Remove(Selected.Entry.Id);
        Reload();
    }

    [RelayCommand]
    private void ResetProfile()
    {
        if (Selected is null) return;
        if (!Confirm($"Delete the learned access profile of {Selected.Name}? The next launch starts learning from scratch.")) return;
        Host.Profiles.DeleteProfile(Selected.Entry.Id);
        ShowDetails();
        Say("Profile deleted.", "ok");
    }
}
