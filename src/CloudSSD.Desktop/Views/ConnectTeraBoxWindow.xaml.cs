using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using CloudSSD.App;
using CloudSSD.Diagnostics;
using CloudSSD.TeraBox;
using Microsoft.Win32;

namespace CloudSSD.Desktop.Views;

public partial class ConnectTeraBoxWindow : Window
{
    private readonly AppHost _host;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public ConnectTeraBoxWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Refresh();
    }

    public TeraBoxUser? User { get; private set; }

    private void Refresh()
    {
        WebRootHint.Text = "Uses the TeraBox website's own interface with your signed-in session. TeraBox's terms do not allow this, so TeraBox could limit your account. " +
            $"Files go to your configured TeraBox folder: {_host.Settings.TeraBox.WebRoot}.";
        var client = _host.TeraBox.Client;
        NeedKeysBox.Visibility = client is null ? Visibility.Visible : Visibility.Collapsed;
        SignInBox.Visibility = client is null ? Visibility.Collapsed : Visibility.Visible;
        if (client != null) KeyInfo.Text = $"TeraBox app credentials loaded (files are kept in TeraBox under “{client.AppRoot}”).";
    }

    private void SignInWeb(object s, RoutedEventArgs e)
    {
        if (_busy) return;
        var w = new TeraBoxWebSignInWindow(_host) { Owner = this };
        if (w.ShowDialog() != true || w.User is null) return;
        User = w.User;
        DialogResult = true;
    }

    private void OpenDocs(object s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://www.terabox.com/integrations/docs?lang=en") { UseShellExecute = true });

    private void LoadKeys(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose terabox-client.json", Filter = "TeraBox app credentials (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _host.TeraBox.ImportClientFile(dlg.FileName);
            Status.Text = "Credentials saved (encrypted for your Windows account).";
            Refresh();
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException or InvalidOperationException)
        {
            ShowError("That file is not valid TeraBox app credentials: " + ex.Message);
        }
    }

    private async void SignIn(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _cts = new CancellationTokenSource();
        SignInButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Status.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        try
        {
            Status.Text = "Asking TeraBox for a sign-in code…";
            var dev = await _host.TeraBox.StartLoginAsync(_cts.Token);
            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource = new MemoryStream(dev.QrPng);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            QrImage.Source = img;
            QrBox.Visibility = Visibility.Visible;
            Status.Text = $"Waiting for approval in the TeraBox app (code valid for {dev.ExpiresIn.TotalMinutes:0} minutes)…";
            User = await _host.TeraBox.FinishLoginAsync(dev, _cts.Token);
            _host.SaveSettings();
            DialogResult = true;
        }
        catch (OperationCanceledException) { Status.Text = "Cancelled."; }
        catch (Exception ex) { ShowError(Redactor.Redact(ex.Message)); }
        finally
        {
            _busy = false;
            SignInButton.IsEnabled = true;
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
