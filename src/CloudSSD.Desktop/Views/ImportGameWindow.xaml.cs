using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using CloudSSD.Games;
using Microsoft.Win32;

namespace CloudSSD.Desktop.Views;

public partial class ImportGameWindow : Window
{
    private readonly AppHost _host;
    private ImportPlan? _plan;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private string? _importedSource;

    public ImportGameWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        DriveBox.ItemsSource = host.Settings.Mounts.Where(m => !m.ReadOnly).ToList();
        DriveBox.SelectedItem = host.Settings.Mounts.FirstOrDefault(m => host.IsTeraBoxMount(m) && !m.ReadOnly) ?? host.Settings.Mounts.FirstOrDefault(m => !m.ReadOnly);
        if (DriveBox.Items.Count == 0) Status.Text = "Add a writable cloud drive first (Cloud Drives page).";
    }

    public GameEntry? ImportedGame { get; private set; }

    private void Browse(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the installed game's folder" };
        if (dlg.ShowDialog(this) != true) return;
        SourceBox.Text = dlg.FolderName;
        FolderBox.Text = "Games/" + Path.GetFileName(dlg.FolderName.TrimEnd('\\'));
    }

    private void InputsChanged(object s, RoutedEventArgs e)
    {
        _plan = null;
        if (ImportButton != null) ImportButton.IsEnabled = false;
        if (PlanBox != null) PlanBox.Visibility = Visibility.Collapsed;
    }

    private async void Check(object s, RoutedEventArgs e)
    {
        if (_busy || DriveBox.SelectedItem is not MountDefinition def || string.IsNullOrWhiteSpace(SourceBox.Text) || string.IsNullOrWhiteSpace(FolderBox.Text)) return;
        await RunBusy(async () =>
        {
            Status.Text = "Scanning the folder and checking cloud space…";
            var checkedPlan = await _host.PlanImportAsync(SourceBox.Text.Trim(), def, FolderBox.Text.Trim(), _cts!.Token);
            FolderBox.Text = checkedPlan.RemoteFolder;
            _plan = checkedPlan; // TextChanged invalidates the previous plan before this assignment.
            var lines = new List<string> { $"{_plan.Files.Count:N0} files, {ByteSize.Format(_plan.TotalBytes)} → {GameImporter.Destination(def, _plan.RemoteFolder)}" };
            lines.AddRange(_plan.Blockers.Select(b => "✗ " + b));
            lines.AddRange(_plan.Warnings.Select(w => "! " + w));
            if (_plan.CanStart) lines.Add("✓ Ready to import. Large games take a while: at 70 Mbps upload, 10 GB takes about 20 minutes.");
            PlanText.Text = string.Join("\n", lines);
            PlanText.Foreground = (System.Windows.Media.Brush)FindResource(_plan.CanStart ? "Text" : "Danger");
            PlanBox.Visibility = Visibility.Visible;
            ImportButton.IsEnabled = _plan.CanStart;
            Status.Text = "";
        });
    }

    private async void Import(object s, RoutedEventArgs e)
    {
        if (_busy || _plan is not { CanStart: true } plan || DriveBox.SelectedItem is not MountDefinition def) return;
        await RunBusy(async () =>
        {
            Progress.Visibility = Visibility.Visible;
            var progress = new Progress<ImportProgress>(p =>
            {
                Progress.Value = p.TotalBytes > 0 ? (double)p.Bytes / p.TotalBytes : 0;
                Status.Text = p.Stage switch
                {
                    ImportStage.Hashing => $"Fingerprinting files (SHA-256)… {ByteSize.Format(p.Bytes)} of {ByteSize.Format(p.TotalBytes)}",
                    ImportStage.Uploading => $"Uploading… {ByteSize.Format(p.Bytes)} of {ByteSize.Format(p.TotalBytes)}" + (p.SpeedBytesPerSec > 0 ? $" at {Bandwidth.FormatRate(p.SpeedBytesPerSec)}" : ""),
                    ImportStage.Verifying => $"Verifying the cloud copy ({p.Detail})…",
                    ImportStage.Publishing => "Finishing…",
                    _ => "Done."
                };
            });
            var (result, game) = await _host.ImportGameAsync(plan, def, FullVerifyBox.IsChecked == true, progress, _cts!.Token, AfterCompletion.Action);
            Status.Foreground = (System.Windows.Media.Brush)FindResource(result.Verified ? "Success" : "Danger");
            Status.Text = result.Message + (game != null ? $" Added “{game.Name}” to your library." : "");
            if (result.Verified)
            {
                ImportedGame = game;
                _importedSource = plan.SourceRoot;
                RecycleButton.Visibility = Visibility.Visible;
                ImportButton.IsEnabled = false;
                CloseButton.Content = "Close";
            }
        });
    }

    private async Task RunBusy(Func<Task> work)
    {
        _busy = true;
        _cts = new CancellationTokenSource();
        CheckButton.IsEnabled = ImportButton.IsEnabled = false;
        CloseButton.Content = "Stop";
        Status.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        try { await work(); }
        catch (OperationCanceledException) { Status.Text = "Stopped. Run the import again later to resume; finished files are skipped."; }
        catch (Exception ex)
        {
            Status.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
            Status.Text = Redactor.Redact(ex.Message);
        }
        finally
        {
            _busy = false;
            CheckButton.IsEnabled = true;
            if ((string)CloseButton.Content == "Stop") CloseButton.Content = "Cancel";
        }
    }

    /// <summary>Only after a verified import, and only after a second confirmation; files go to the Recycle Bin.</summary>
    private void RecycleLocal(object s, RoutedEventArgs e)
    {
        if (_importedSource is null) return;
        var ok = MessageBox.Show(this,
            $"Move the local folder to the Recycle Bin?\n\n{_importedSource}\n\nThe verified copy stays in the cloud. Save games stored inside this folder go with it; " +
            "saves in Documents or AppData are not touched. You can restore the folder from the Recycle Bin.",
            "Move local copy to Recycle Bin", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;
        var op = new ShFileOp
        {
            wFunc = 3 /* FO_DELETE */, pFrom = _importedSource + "\0\0",
            fFlags = 0x0040 /* FOF_ALLOWUNDO */ | 0x0004 /* FOF_SILENT */ | 0x0010 /* FOF_NOCONFIRMATION */
        };
        var rc = SHFileOperationW(ref op);
        Status.Text = rc == 0 && !op.fAnyOperationsAborted ? "The local copy is in the Recycle Bin." : $"Could not move the folder (code {rc}); nothing was deleted permanently.";
        RecycleButton.IsEnabled = false;
    }

    private void Close(object s, RoutedEventArgs e)
    {
        if (_busy && _cts is { IsCancellationRequested: false }) { _cts.Cancel(); return; }
        DialogResult = ImportedGame != null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOp op);
}
