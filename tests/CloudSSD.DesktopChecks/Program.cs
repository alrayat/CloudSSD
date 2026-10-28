using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.Desktop;
using CloudSSD.Desktop.Controls;
using CloudSSD.Desktop.ViewModels;
using CloudSSD.Desktop.Views;
using CloudSSD.Games;

namespace CloudSSD.DesktopChecks;

/// <summary>Real WPF views and services, using a new local profile. No account sign-in or cloud transfers.</summary>
internal static class Program
{
    private static int _checks;
    private static readonly List<string> Errors = [];

    [STAThread]
    public static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? ".work/desktop-checks");
        Directory.CreateDirectory(output);
        var profile = Path.Combine(output, "profile-" + Guid.NewGuid().ToString("N"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/CloudSSD;component/Themes/CloudSSD.xaml")
        });
        app.Resources["Vis"] = new BoolToVisibility();
        app.Resources["NotVis"] = new BoolToVisibility { Invert = true };
        app.Resources["NotBool"] = new BoolInverse();
        app.Resources["Tone"] = new ToneToBrush();
        app.DispatcherUnhandledException += (_, e) => { Errors.Add(e.Exception.ToString()); e.Handled = true; };
        using var listener = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        AppHost? host = null;
        MainWindow? shell = null;
        try
        {
            host = AppHost.Create(new AppPaths(profile));
            Check(host.TryAcquireInstanceLock(), "isolated profile lock");
            host.Settings.RestoreMountsOnStartup = false;
            host.Settings.FirstRunCompleted = true;
            // Initialize local dependency discovery and real metrics, with no saved accounts or mounts.
            Wait(host.InitializeAsync(restoreMounts: false, adoptMounts: false));
            var vm = new MainViewModel(host);
            shell = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            app.MainWindow = shell;
            shell.Show(); Pump();
            var pageContent = (ContentControl)shell.FindName("PageContent");
            var maximizeButton = (Button)shell.FindName("MaximizeButton");
            var maximizeIcon = (TextBlock)shell.FindName("MaximizeIcon");
            var minimizeButton = (Button)shell.FindName("MinimizeButton");
            var closeButton = (Button)shell.FindName("CloseWindowButton");
            var chrome = WindowChrome.GetWindowChrome(shell);
            Check(chrome is { CaptionHeight: 46, ResizeBorderThickness.Left: > 0 }, "native drag/resize configuration");
            Check(shell.WindowStyle == WindowStyle.None && shell.ResizeMode == ResizeMode.CanResize, "desktop frame");

            foreach (var page in vm.Pages)
            {
                vm.Selected = page; page.OnShown(); Pump(150);
                Check(pageContent.Content == page, "navigation: " + page.Title);
                Check(FindVisual<UserControl>(pageContent) != null, "view loaded: " + page.Title);
                Capture(shell, Path.Combine(output, Slug(page.Title) + ".png"));
            }

            Check(shell.InputBindings.Count == 8, "eight keyboard navigation bindings");
            vm.NavigatePageCommand.Execute("3"); Pump();
            Check(vm.Selected is GameLibraryViewModel, "keyboard navigation command");
            vm.NavigatePageCommand.Execute("99");
            Check(vm.Selected is GameLibraryViewModel, "invalid navigation is ignored");

            vm.Selected = vm.Pages[0]; Pump();
            var pageScroller = FindVisual<ScrollViewer>(FindVisual<UserControl>(pageContent)!);
            var beforeScroll = pageScroller!.VerticalOffset;
            var scrollbar = AllVisual<ScrollBar>(pageScroller).First(b => b.Orientation == Orientation.Vertical);
            ScrollBar.PageDownCommand.Execute(null, scrollbar); Pump();
            Check(pageScroller.VerticalOffset > beforeScroll, "scrollbar page-down interaction");
            pageScroller.ScrollToTop();

            // Settings use real persistence in this isolated profile.
            var settings = vm.Pages.OfType<SettingsViewModel>().Single();
            vm.Selected = settings; settings.OnShown();
            settings.DeclaredMbps = "83";
            Wait(settings.SaveCommand.ExecuteAsync(null)); Pump();
            Check(new JsonSettingsStore(host.Paths.SettingsFile).Load().Network.DeclaredDownloadMbps == 83, "settings save/reload");
            settings.DeclaredMbps = "invalid";
            Wait(settings.SaveCommand.ExecuteAsync(null));
            Check(settings.MessageTone == "error", "settings validation");
            var tabs = FindVisual<TabControl>(pageContent)!;
            foreach (TabItem tab in tabs.Items)
            {
                tabs.SelectedItem = tab; Pump();
                Check(tab.IsSelected, "settings tab: " + tab.Header);
                Capture(shell, Path.Combine(output, "settings-" + Slug(tab.Header.ToString()!) + ".png"));
            }

            // Local fixture only: production views remain backed by the real game library.
            var game = new GameEntry { Name = "Desktop check fixture", MountId = "unmounted-check", RootPath = "fixture", Executable = "fixture.exe" };
            host.Library.Save(game);
            var library = vm.Pages.OfType<GameLibraryViewModel>().Single();
            vm.Selected = library; library.OnShown(); Pump();
            Check(library.HasSelection && library.Selected?.Entry.Id == game.Id, "library selection from SQLite");
            Capture(shell, Path.Combine(output, "game-library-selection.png"));
            library.SearchText = "DESKTOP CHECK"; Pump();
            Check(library.FilteredGames.Cast<GameItem>().Count() == 1 && library.Selected?.Entry.Id == game.Id, "case-insensitive library search");
            library.SearchText = "no matching title"; Pump();
            Check(library.NoSearchResults && !library.HasSelection, "library empty search state");
            Check(host.Library.All().Any(g => g.Id == game.Id), "search does not change persisted games");
            library.SearchText = ""; Pump();
            Check(!library.NoSearchResults && library.Selected?.Entry.Id == game.Id, "clear search restores selection");

            // All dialogs instantiate with the same real host; no affirmative transfer or auth buttons are clicked.
            Window[] dialogs = [new FirstRunWindow(host), new AddDriveWindow(host, null), new AddGameWindow(host),
                new ConnectDriveWindow(host), new ConnectTeraBoxWindow(host), new TeraBoxWebSignInWindow(host),
                new GameSettingsWindow(host, game), new FullLocalWindow(host, game), new ImportGameWindow(host),
                new PrepareWindow(host, game), new CompletionActionsWindow(host)];
            foreach (var dialog in dialogs)
            {
                dialog.Owner = shell; dialog.Show(); Pump(120);
                Check(WindowFrame.GetEnabled(dialog), "dialog frame: " + dialog.GetType().Name);
                Check(WindowChrome.GetWindowChrome(dialog)?.CaptionHeight == 46, "dialog native chrome: " + dialog.GetType().Name);
                if (dialog is AddGameWindow)
                {
                    SystemCommands.MaximizeWindowCommand.Execute(null, dialog); Pump();
                    Check(dialog.WindowState == WindowState.Maximized, "dialog maximize command");
                    Check(AllVisual<TextBlock>(dialog).Any(t => t.Text == "\uE923"), "dialog restore glyph follows window state");
                    SystemCommands.MaximizeWindowCommand.Execute(null, dialog); Pump();
                    Check(dialog.WindowState == WindowState.Normal, "dialog restore command");
                }
                Capture(dialog, Path.Combine(output, Slug(dialog.GetType().Name) + ".png"));
                dialog.Close(); Pump();
            }
            var picker = new CompletionActionPicker();
            Check(((ComboBox)picker.FindName("Choice")).Items.Count == 4, "all completion action choices");

            vm.Selected = vm.Pages[0];
            maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(shell.WindowState == WindowState.Maximized, "maximize button");
            Check(maximizeIcon.Text == "\uE923", "restore icon follows state");
            maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(shell.WindowState == WindowState.Normal, "restore button");
            minimizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(shell.WindowState == WindowState.Minimized, "minimize button");
            SystemCommands.RestoreWindow(shell); Pump();
            var oldWidth = shell.Width; shell.Width = oldWidth - 120; Pump();
            Check(shell.ActualWidth < oldWidth, "window resizes through WPF");
            Capture(shell, Path.Combine(output, "dashboard-compact.png"));
            shell.Width = 1040; shell.Height = 700;
            foreach (var page in vm.Pages)
            {
                vm.Selected = page; page.OnShown(); Pump();
                Check(FindVisual<UserControl>(pageContent)!.ActualWidth <= shell.ActualWidth, "compact layout: " + page.Title);
                Capture(shell, Path.Combine(output, "compact-" + Slug(page.Title) + ".png"));
            }
            shell.Width = 1440; shell.Height = 900;

            // Default No and title-bar dismissal preserve destructive-confirmation semantics.
            ScheduleDialogButton("No", Path.Combine(output, "confirmation.png"));
            var result = CloudMessageBox.Show(shell, "Local test confirmation.", "Confirmation check", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            Check(result == MessageBoxResult.No, "confirmation No result");
            ScheduleDialogClose();
            result = CloudMessageBox.Show(shell, "Local cancellation check.", "Dismissal check", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            Check(result == MessageBoxResult.No, "title-bar confirmation dismissal is safe");
            ScheduleDialogButton("Cancel");
            result = CloudMessageBox.Show(shell, "Local cancellation check.", "Cancellation check", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
            Check(result == MessageBoxResult.Cancel, "confirmation Cancel result");

            if (args.Contains("--interactive"))
            {
                vm.Selected = vm.Pages[0]; Pump();
                var workArea = SystemParameters.WorkArea;
                shell.Width = Math.Min(1180, workArea.Width - 20);
                shell.Height = Math.Min(740, workArea.Height - 20);
                shell.Left = workArea.Left + (workArea.Width - shell.Width) / 2;
                shell.Top = workArea.Top + (workArea.Height - shell.Height) / 2;
                Pump();
                var interactive = new DispatcherFrame();
                shell.Closed += (_, _) => interactive.Continue = false;
                Console.WriteLine("READY: interactive isolated CloudSSD window; close it to finish.");
                Dispatcher.PushFrame(interactive);
            }
            else closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(!shell.IsVisible, "close button");
            Check(Errors.Count == 0, "no unhandled UI exceptions");
            Check(listener.Messages.Count == 0, "no binding errors");
            File.WriteAllText(Path.Combine(output, "results.txt"), $"Passed {_checks} checks.\nMouse-driven drag, edge resize and authenticated cloud workflows were not exercised by this check.\n");
            Console.WriteLine($"PASS: {_checks} desktop checks; images and results: {output}");
            return 0;
        }
        catch (Exception e)
        {
            var error = e + "\n" + string.Join("\n", Errors) + "\n" + string.Join("\n", listener.Messages);
            File.WriteAllText(Path.Combine(output, "failure.txt"), error);
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            shell?.Close();
            if (host != null) Wait(host.DisposeAsync().AsTask());
            app.Shutdown();
        }
    }

    private static void Check(bool success, string label)
    {
        if (!success) throw new InvalidOperationException("FAILED: " + label);
        _checks++; Console.WriteLine("PASS " + label);
    }
    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (!task.IsCompleted && DateTime.UtcNow < until) Pump(25);
        if (!task.IsCompleted) throw new TimeoutException("Desktop check operation timed out.");
        task.GetAwaiter().GetResult();
    }
    private static void Pump(int milliseconds = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string file)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file); png.Save(stream);
    }
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private static IEnumerable<T> AllVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var nested in AllVisual<T>(child)) yield return nested;
        }
    }
    private static void ScheduleDialogButton(string label, string? screenshot = null)
    {
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().Last();
            dialog.UpdateLayout();
            if (screenshot != null) Capture(dialog, screenshot);
            var button = AllVisual<Button>(dialog).Single(b => b.Content as string == label);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
    }
    private static void ScheduleDialogClose() => Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
        new Action(() => Application.Current.Windows.Cast<Window>().Last().Close()));
    private static string Slug(string value) => string.Join('-', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
