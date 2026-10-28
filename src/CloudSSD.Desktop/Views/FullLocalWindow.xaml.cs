using System.Windows;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using CloudSSD.Games;
using Microsoft.Win32;

namespace CloudSSD.Desktop.Views;

public partial class FullLocalWindow : Window
{
    private readonly AppHost _host;
    private readonly GameEntry _game;
    private readonly long _total;
    private CancellationTokenSource? _cts;
    private bool _running;

    public FullLocalWindow(AppHost host, GameEntry game)
    {
        _host = host;
        _game = game;
        InitializeComponent();
        TitleText.Text = $"Install {game.Name} locally";
        _total = game.Compatibility?.TotalBytes ?? 0;
        var mbps = host.Settings.Network.EffectiveMbps;
        Facts.Text = _total > 0
            ? $"Size: {ByteSize.Format(_total)} in {game.Compatibility!.FileCount:N0} files. At {mbps:0} Mbps ({Bandwidth.BytesPerSecondToMBps(Bandwidth.MbpsToBytesPerSecond(mbps)):0.0} MB/s) this takes about {Bandwidth.FormatDuration(Bandwidth.EstimateTransfer(_total, mbps))}."
            : "Size unknown — analyze the game first.";
        DestBox.Text = game.LocalRoot ?? Path.Combine(host.Paths.LocalGamesRootFor(host.Settings), Sanitize(game.Name) + "-" + game.Id);
        ApproveBox.Content = $"I approve downloading {ByteSize.Format(_total)} from my cloud storage to this PC.";
    }

    private static string Sanitize(string s) => new(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).Take(40).ToArray());

    private void Browse(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Choose where to install the game" };
        if (d.ShowDialog() == true) DestBox.Text = Path.Combine(d.FolderName, Sanitize(_game.Name));
    }

    private void DestChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateSpace();

    private bool UpdateSpace()
    {
        try
        {
            var (fits, free, _) = FullLocalInstaller.CheckSpace(DestBox.Text, _total, _host.Settings.Cache.MinFreeSpaceBytes);
            SpaceText.Text = $"{ByteSize.Format(free)} free on {DiskInfo.VolumeRoot(DestBox.Text)} · keeps {ByteSize.Format(_host.Settings.Cache.MinFreeSpaceBytes)} free for Windows." +
                             (fits ? "" : " NOT ENOUGH SPACE.");
            SpaceText.Foreground = (System.Windows.Media.Brush)FindResource(fits ? "Muted" : "Danger");
            return fits;
        }
        catch (Exception ex)
        {
            SpaceText.Text = ex.Message;
            return false;
        }
    }

    private void ApproveChanged(object sender, RoutedEventArgs e) => StartButton.IsEnabled = ApproveBox.IsChecked == true && !_running && _total > 0;

    private async void Start(object sender, RoutedEventArgs e)
    {
        if (ApproveBox.IsChecked != true || _host.Rclone is null) return;
        if (!UpdateSpace())
        {
            Status.Text = "Not enough free space for this game plus the protected reserve.";
            return;
        }
        var mount = _host.Settings.Mounts.FirstOrDefault(m => m.Id == _game.MountId);
        if (mount is null)
        {
            Status.Text = "The game's cloud drive is not configured.";
            return;
        }
        _running = true;
        StartButton.IsEnabled = false;
        ApproveBox.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        CloseButton.Content = "Stop";
        _cts = new CancellationTokenSource();
        var progress = new Progress<LocalCopyProgress>(p =>
        {
            Bar.Value = p.TotalBytes > 0 ? p.Bytes / (double)p.TotalBytes : 0;
            Status.Text = $"{ByteSize.Format(p.Bytes)} of {ByteSize.Format(p.TotalBytes)} · {Bandwidth.FormatRate(p.SpeedBytesPerSec)}" +
                          (p.EtaSeconds is { } eta ? $" · {Bandwidth.FormatDuration(TimeSpan.FromSeconds(eta))} left" : "") + (p.Errors > 0 ? $" · {p.Errors} error(s), retrying" : "");
        });
        try
        {
            var dest = DestBox.Text.Trim();
            var r = await _host.InstallLocalAsync(_game, dest, progress, _cts.Token, AfterCompletion.Action);
            if (r.Success)
            {
                _game.LocalRoot = dest;
                _game.Mode = ExecutionMode.FullLocal;
                _game.Readiness = ReadinessStatus.ReadyToLaunch;
                _game.ReadinessDetail = $"Installed locally at {dest}.";
                _host.Library.Save(_game);
                Status.Text = $"Done. {_game.Name} now runs from {dest} (Full Local mode).";
                Bar.Value = 1;
            }
            else Status.Text = "rclone reported a problem: " + Mount.RcloneConfigService.ExplainError(r.StdErr) + " Run it again to resume.";
        }
        catch (OperationCanceledException) { Status.Text = "Stopped. Already-copied files are kept; start again to resume."; }
        catch (Exception ex) { Status.Text = Redactor.Redact(ex.Message); }
        _running = false;
        CloseButton.Content = "Close";
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        if (_running) _cts?.Cancel();
        else Close();
    }
}
