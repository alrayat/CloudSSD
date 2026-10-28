using System.Threading;
using System.Windows;
using CloudSSD.App;
using CloudSSD.App.Persistence;
using CloudSSD.Desktop.ViewModels;
using CloudSSD.Desktop.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CloudSSD.Desktop;

public partial class App : Application
{
    private Mutex? _single;
    private AppHost? _host;
    private bool _shuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\CloudSSD.Desktop.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("CloudSSD is already running.", "CloudSSD", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var logs = new CloudSSD.Core.AppPaths().LogDirectory;
        Diagnostics.StartupTrace.Mark(logs, "app", "started; creating host");
        _host = AppHost.Create();
        Diagnostics.StartupTrace.Mark(logs, "app", "host created; taking the data-folder lock");
        if (!_host.TryAcquireInstanceLock())
        {
            Diagnostics.StartupTrace.Mark(logs, "app", "lock busy; asking the background agent to hand over");
            // The background agent may own the drives (Keep Cloud SSD Permanent): ask it to hand them over. The drives
            // stay mounted throughout; this window re-attaches to them.
            var released = await AgentControlClient.ReleaseAsync(_host.Paths, TimeSpan.FromSeconds(30));
            if (!released || !await _host.TryAcquireInstanceLockAsync(TimeSpan.FromSeconds(10)))
            {
                MessageBox.Show("Another CloudSSD process (for example the command-line tool) is using the data folder.", "CloudSSD");
                Shutdown();
                return;
            }
        }
        DispatcherUnhandledException += (_, args) =>
        {
            _host.LoggerFactory.CreateLogger("App").LogError(args.Exception, "Unhandled UI exception");
            MessageBox.Show(Diagnostics.Redactor.Redact(args.Exception.Message), "CloudSSD error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // This separate process survives an abrupt desktop crash and restores supervision.
        // It waits for this exact process instance, so PID reuse cannot trigger a false hand-over.
        if (File.Exists(StartupRegistration.AgentPath))
        {
            try
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                using var watcher = AgentControlClient.Launch(_host.Paths, null, "--watch-desktop", self.Id.ToString(),
                    "--started-ticks", self.StartTime.ToUniversalTime().Ticks.ToString());
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                _host.LoggerFactory.CreateLogger("App").LogWarning("Crash recovery watcher could not start: {Error}", Diagnostics.Redactor.Redact(ex.Message));
            }
        }
        SystemEvents.PowerModeChanged += (_, args) =>
        {
            if (args.Mode == PowerModes.Resume && _host != null) _ = _host.Mounts.OnSystemResumeAsync();
        };

        var vm = new MainViewModel(_host);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Closing += OnClosing;
        SessionEnding += (_, _) =>
        {
            // Windows is signing out or shutting down: rclone ends with the session. Record a consistent state; pending
            // uploads stay in the cache and permanent drives return at the next logon.
            _host?.Mounts.DetachAll();
        };
        window.Show();
        _host.CompletionUiAvailable = true;
        Diagnostics.StartupTrace.Mark(logs, "app", "window shown; initializing");

        try
        {
            await _host.InitializeAsync();
            Diagnostics.StartupTrace.Mark(logs, "app", "initialized");
        }
        catch (Exception ex)
        {
            MessageBox.Show(window, "Startup problem: " + Diagnostics.Redactor.Redact(ex.Message), "CloudSSD", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RepairStartupEntry();
        if (!_host.Settings.FirstRunCompleted)
        {
            var first = new FirstRunWindow(_host) { Owner = window };
            first.ShowDialog();
        }
        vm.Selected.OnShown();
    }

    /// <summary>
    /// Permanent drives return at sign-in through a Run entry naming this copy's agent. After an install, upgrade or move
    /// the entry can be missing or point at another installation, so
    /// it is re-pointed here. Only for the user's normal data folder (never a test or portable folder), and never when the
    /// user disabled it in Task Manager.
    /// </summary>
    private void RepairStartupEntry()
    {
        try
        {
            if (_host is null || !_host.Settings.Mounts.Any(m => m.Permanent) || !File.Exists(StartupRegistration.AgentPath)) return;
            var normal = Core.AppPaths.ResolveDefaultDataRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (!string.Equals(Path.GetFullPath(_host.Paths.DataRoot).TrimEnd('\\'), normal, StringComparison.OrdinalIgnoreCase)) return;
            var reg = new StartupRegistration();
            var cmd = StartupRegistration.CommandFor(StartupRegistration.AgentPath, _host.Paths);
            var state = reg.State(cmd);
            if (state is StartupState.NotRegistered or StartupState.PointsElsewhere)
            {
                reg.Register(cmd);
                _host.LoggerFactory.CreateLogger("App").LogInformation("Windows sign-in entry for permanent drives {Action} ({Command})",
                    state == StartupState.NotRegistered ? "restored" : "re-pointed to this copy", cmd);
            }
            else if (state == StartupState.Registered && !reg.HasApprovalMark())
            {
                reg.EnsureApproved();
                _host.LoggerFactory.CreateLogger("App").LogInformation("Windows sign-in entry for permanent drives marked enabled in Startup apps");
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _host?.LoggerFactory.CreateLogger("App").LogWarning("Could not repair the Windows sign-in entry: {Error}", e.Message);
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shuttingDown || _host is null) return;
        e.Cancel = true;
        var permanent = _host.Settings.Mounts.Where(m => m.Permanent).Select(m => m.Id).ToHashSet();
        // Only drives that are really mounted matter here; a failed drive (e.g. expired login) must not trigger the question.
        var others = _host.Mounts.Sessions.Where(s => !permanent.Contains(s.Id) && s.State is Core.MountState.Mounted or Core.MountState.Degraded or Core.MountState.Starting).ToList();
        var keepOthers = _host.Settings.KeepMountsAfterExit;
        if (others.Count > 0)
        {
            var running = _host.CurrentLaunch is { State: Games.LaunchState.Running };
            var answer = MessageBox.Show(MainWindow!,
                (running ? "A game is still running.\n\n" : "") +
                (permanent.Count > 0 ? "Drives set to \"Keep Cloud SSD Permanent\" stay mounted.\n\n" : "") +
                "Keep the other cloud drives mounted after closing CloudSSD?\n\nYes: they stay available until you sign out of Windows.\nNo: they are unmounted (pending uploads get up to 60 s to finish).",
                "Close CloudSSD", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, keepOthers || running ? MessageBoxResult.Yes : MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel) return;
            keepOthers = answer == MessageBoxResult.Yes;
        }
        _shuttingDown = true;
        _host.CompletionUiAvailable = false;
        MainWindow!.IsEnabled = false;
        MainWindow.Title = "CloudSSD — closing…";
        var startAgent = false;
        try
        {
            if (!keepOthers)
                foreach (var s in others) await _host.UnmountAsync(s.Id);
            // Anything still mounted is handed to the background agent, which supervises it while the window is closed.
            startAgent = _host.Mounts.Sessions.Count > 0 || permanent.Count > 0;
            await _host.ShutdownAsync(keepMounts: true);
            await _host.DisposeAsync(); // releases the data-folder lock for the agent
        }
        catch (Exception ex)
        {
            _shuttingDown = false;
            _host.CompletionUiAvailable = true;
            MainWindow.IsEnabled = true;
            MainWindow.Title = "CloudSSD";
            MessageBox.Show(MainWindow, Diagnostics.Redactor.Redact(ex.Message), "Drive kept mounted", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (startAgent)
        {
            try { AgentControlClient.Launch(_host.Paths); }
            catch (Exception ex)
            {
                MessageBox.Show("The drives stay mounted, but the background agent could not be started: " + Diagnostics.Redactor.Redact(ex.Message) +
                                "\n\nThey will not be restarted automatically if they stop.", "CloudSSD", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        _single?.ReleaseMutex();
        Shutdown();
    }
}
