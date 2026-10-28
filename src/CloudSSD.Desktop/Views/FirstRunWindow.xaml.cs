using System.Globalization;
using System.Windows;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.Desktop.ViewModels;
using CloudSSD.Diagnostics;
using Microsoft.Win32;

namespace CloudSSD.Desktop.Views;

public partial class FirstRunWindow : Window
{
    private readonly AppHost _host;
    private int _cacheInfoVersion;

    public FirstRunWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Deps.ItemsSource = host.Dependencies.Select(d => new KeyValueRow(d.Name, d.Detail + (d.Remedy != null ? " → " + d.Remedy : ""),
            d.State switch { DependencyState.Ok => "ok", DependencyState.Warning => "warn", _ => "error" })).ToList();
        CacheBox.Text = host.Paths.CacheRootFor(host.Settings);
        _ = SuggestCacheAsync(CacheBox.Text);
        BudgetBox.ItemsSource = new[] { "10 GB", "20 GB", "30 GB", "40 GB", "60 GB", "100 GB" };
        BudgetBox.SelectedItem = $"{host.Settings.Cache.BudgetBytes / ByteSize.GiB} GB";
        if (BudgetBox.SelectedItem is null) BudgetBox.SelectedIndex = 1;
        MbpsBox.Text = host.Settings.Network.DeclaredDownloadMbps.ToString(CultureInfo.InvariantCulture);
        MbpsBox.TextChanged += (_, _) => UpdateEstimate();
        UpdateEstimate();
    }

    private async Task SuggestCacheAsync(string initial)
    {
        try
        {
            var suggested = await BoundedFileSystem.Shared.RunAsync(() => SuggestCacheRoot(_host), TimeSpan.FromSeconds(3));
            if (CacheBox.Text == initial) CacheBox.Text = suggested;
        }
        catch (TimeoutException) { } // the current local folder remains editable
    }

    /// <summary>Suggests the fixed NTFS drive with the most free space (the system drive is often nearly full).</summary>
    public static string SuggestCacheRoot(AppHost host)
    {
        var current = host.Paths.CacheRootFor(host.Settings);
        try
        {
            var best = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
            var (curFree, _) = DiskInfo.FreeSpace(current);
            if (best != null && best.AvailableFreeSpace > curFree * 1.5 && !current.StartsWith(best.Name, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(best.Name, "CloudSSD", "cache");
        }
        catch (Exception) { }
        return current;
    }

    private void UpdateEstimate()
    {
        if (!double.TryParse(MbpsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps) || !double.IsFinite(mbps) || mbps <= 0) return;
        var bps = Bandwidth.MbpsToBytesPerSecond(mbps);
        Estimate.Text = $"{mbps:0} Mbps = {Bandwidth.BytesPerSecondToMBps(bps):0.00} MB/s at best. Downloading 10 GB takes about {Bandwidth.FormatDuration(Bandwidth.EstimateTransfer(10 * ByteSize.GiB, mbps))}; " +
                        "CloudSSD measures the real speed during Optimize & Prepare.";
    }

    private async void CacheChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var version = ++_cacheInfoVersion;
        var path = CacheBox.Text;
        try
        {
            var text = await BoundedFileSystem.Shared.RunAsync(() =>
            {
                var (free, total) = DiskInfo.FreeSpace(path);
                var (fs, sparse) = DiskInfo.VolumeFileSystem(path);
                return $"{ByteSize.Format(free)} free of {ByteSize.Format(total)} · {fs}{(sparse ? " with sparse files" : " (no sparse-file support)")}";
            }, TimeSpan.FromSeconds(3));
            if (version == _cacheInfoVersion) CacheInfo.Text = text;
        }
        catch (Exception ex) { if (version == _cacheInfoVersion) CacheInfo.Text = ex.Message; }
    }

    private void Browse(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Choose a folder on an NTFS drive for the cache" };
        if (d.ShowDialog() == true) CacheBox.Text = Path.Combine(d.FolderName, "CloudSSD-cache");
    }

    private async void Ok(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ReserveBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var reserve) || !double.IsFinite(reserve) || reserve < 2 || reserve >= (long.MaxValue - ByteSize.GiB) / (double)ByteSize.GiB
            || !double.TryParse(MbpsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps) || !double.IsFinite(mbps) || mbps <= 0)
        {
            Status.Text = "Reserve must be at least 2 GB and internet speed a positive number.";
            return;
        }
        string validatedRoot;
        var button = (System.Windows.Controls.Button)sender;
        button.IsEnabled = false;
        try
        {
            var requestedRoot = CacheBox.Text.Trim();
            validatedRoot = await BoundedFileSystem.Shared.RunAsync(() =>
            {
                if (!Path.IsPathFullyQualified(requestedRoot) || requestedRoot.StartsWith(@"\\"))
                    throw new IOException("Choose a local NTFS drive for the cache.");
                var full = Path.GetFullPath(requestedRoot);
                var (format, sparse) = DiskInfo.VolumeFileSystem(full);
                if (!format.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || !sparse)
                    throw new IOException("The cache needs a local NTFS drive with sparse-file support.");
                var (free, _) = DiskInfo.FreeSpace(full);
                if (free <= (long)(reserve * ByteSize.GiB) + ByteSize.GiB)
                    throw new IOException("Choose a drive with more free space, or lower the reserved space. At least 1 GiB beyond the reserve is needed.");
                Directory.CreateDirectory(full);
                return full;
            }, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Status.Text = "Cannot use that folder: " + ex.Message;
            return;
        }
        finally { button.IsEnabled = true; }
        var s = _host.Settings;
        s.Cache.CacheRoot = validatedRoot;
        s.Cache.BudgetBytes = long.Parse(((string)BudgetBox.SelectedItem).Split(' ')[0]) * ByteSize.GiB;
        s.Cache.MinFreeSpaceBytes = (long)(reserve * ByteSize.GiB);
        s.Network.DeclaredDownloadMbps = mbps;
        s.LocalGamesRoot ??= Path.Combine(Path.GetDirectoryName(s.Cache.CacheRoot.TrimEnd('\\'))!, "local-games");
        s.FirstRunCompleted = true;
        _host.SaveSettings();
        _host.RefreshDependencies();
        DialogResult = true;
    }

    private void Later(object sender, RoutedEventArgs e) => DialogResult = false;
}
