using System.Diagnostics;
using System.Windows;
using CloudSSD.App;
using CloudSSD.Diagnostics;
using CloudSSD.Mount;
using Microsoft.Win32;

namespace CloudSSD.Desktop.Views;

public partial class ConnectDriveWindow : Window
{
    private readonly AppHost _host;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private GoogleClientFile? _key;
    private readonly GoogleClientFile? _bundled = GoogleClientFile.FindBundled();
    private string? _fixUrl;

    private void OpenFix(object s, RoutedEventArgs e) { if (_fixUrl != null) Open(_fixUrl); }

    public ConnectDriveWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SetKey(GoogleClientFile.FindDownloaded());
    }

    public string RemoteName { get; private set; } = "";
    /// <summary>True when the login only has read permission; the drive must then be mounted read-only.</summary>
    public bool ReadOnlyScope { get; private set; }

    private string NextName()
    {
        var name = "gdrive";
        for (int i = 2; _host.Remotes.Any(r => r.Name == name); i++) name = "gdrive" + i;
        return name;
    }

    private void SetKey(GoogleClientFile? key)
    {
        _key = key;
        KeyFoundBox.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
        GetKeyBox.Visibility = key is null ? Visibility.Visible : Visibility.Collapsed;
        TesterButton.Visibility = key?.TestUsersUrl is null ? Visibility.Collapsed : Visibility.Visible;
        if (key != null)
        {
            KeyFileText.Text = $"{Path.GetFileName(key.Path)}{(key.ProjectId != null ? $" · project {key.ProjectId}" : "")}. " +
                               "First time? Add your Gmail as a tester or Google will say “Access blocked”.";
            ModeText.Text = "Uses your own Google key from the file above.";
        }
        else if (_bundled != null)
        {
            ModeText.Text = "Uses the CloudSSD Google sign-in that came with this installation. Optional: your own key below.";
        }
        else
        {
            ModeText.Text = "Uses rclone's built-in shared Google login (Google's page says “rclone”). It is shared by all rclone users, so Google may " +
                            "slow it down (“rate limited”), and rclone is retiring it during 2026. Your own key below avoids that.";
        }
    }

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void OpenConsole(object s, RoutedEventArgs e) => Open("https://console.cloud.google.com/auth/clients");
    private void OpenTesters(object s, RoutedEventArgs e) { if (_key?.TestUsersUrl is { } u) Open(u); }

    private void FindKey(object s, RoutedEventArgs e)
    {
        var key = GoogleClientFile.FindDownloaded();
        if (key is null)
        {
            ShowError("No Google key file (client_secret_….json) found in Downloads or on the Desktop yet. In Google Cloud open your Desktop client and click Download JSON.");
            return;
        }
        Status.Text = "";
        SetKey(key);
    }

    private void LoadKey(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose the Google key file you downloaded",
            Filter = "Google OAuth client (*.json)|*.json",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        if (dlg.ShowDialog(this) != true) return;
        var key = GoogleClientFile.TryLoad(dlg.FileName);
        if (key is null)
        {
            ShowError("That file is not a Google OAuth client file (it needs a client_id and client_secret).");
            return;
        }
        Status.Text = "";
        SetKey(key);
    }

    private void AdvancedChanged(object sender, RoutedEventArgs e) =>
        AdvancedPanel.Visibility = AdvancedBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>One click: own key if a key file was found, else the key shipped with the program, else rclone's built-in client.</summary>
    private async void QuickConnect(object sender, RoutedEventArgs e)
    {
        var key = _key ?? _bundled;
        await RunAsync(new GoogleDriveConnector.Request(NextName(), key?.ClientId ?? "", key?.ClientSecret ?? "", ReadOnlyBox.IsChecked == true, null));
    }

    private async void Connect(object sender, RoutedEventArgs e) =>
        await RunAsync(new GoogleDriveConnector.Request(NextName(), ClientIdBox.Text.Trim(), SecretBox.Password, ReadOnlyBox.IsChecked == true,
            string.IsNullOrWhiteSpace(RootBox.Text) ? null : RootBox.Text.Trim()));

    private async Task RunAsync(GoogleDriveConnector.Request req)
    {
        if (_busy) return;
        if (_host.Rclone is null)
        {
            ShowError("rclone was not found; see Diagnostics.");
            return;
        }
        _busy = true;
        _cts = new CancellationTokenSource();
        Status.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        FixButton.Visibility = Visibility.Collapsed;
        QuickButton.IsEnabled = ConnectButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        var ownKey = !req.UsesSharedClient && req.ClientId != _bundled?.ClientId;
        var progress = new Progress<string>(m => Status.Text = m + (!ownKey ? "" :
            "  If Google shows “Access blocked”, click “Add me as a tester”, add your Gmail, then press Cancel and sign in again."));
        try
        {
            await _host.Drive.ConnectAsync(_host.Rclone, req, progress, _cts.Token);
            if (_host.Settings.EncryptRcloneConfig)
            {
                Status.Text = "Encrypting the saved login (Windows DPAPI)…";
                await _host.RcloneConfig.EnsureEncryptedAsync(_host.Rclone, _host.CliPath, _cts.Token);
            }
            RemoteName = req.RemoteName;
            ReadOnlyScope = req.ReadOnlyScope;
            SecretBox.Clear();
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Cancelled.";
        }
        catch (GoogleSetupException setup)
        {
            Status.Foreground = (System.Windows.Media.Brush)FindResource("Warning");
            Status.Text = setup.Message;
            _fixUrl = setup.FixUrl;
            FixButton.Content = setup.FixLabel;
            FixButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            var msg = Redactor.Redact(ex.Message);
            if (req.UsesSharedClient && (msg.Contains("client", StringComparison.OrdinalIgnoreCase) || msg.Contains("403") || msg.Contains("401")))
                msg += "  Google may no longer accept rclone's shared login — use your own Google key (box above).";
            ShowError(msg);
        }
        finally
        {
            _busy = false;
            QuickButton.IsEnabled = ConnectButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string text)
    {
        Status.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
        Status.Text = text;
    }

    private void Cancel(object sender, RoutedEventArgs e)
    {
        if (_busy && _cts is { IsCancellationRequested: false }) _cts.Cancel();
        else DialogResult = false;
    }
}
