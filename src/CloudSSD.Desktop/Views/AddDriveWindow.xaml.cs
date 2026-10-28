using System.Windows;
using System.Windows.Controls;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using CloudSSD.Mount;

namespace CloudSSD.Desktop.Views;

public partial class AddDriveWindow : Window
{
    private readonly AppHost _host;

    public AddDriveWindow(AppHost host, string? remote)
    {
        _host = host;
        InitializeComponent();
        AccountBox.ItemsSource = host.Remotes;
        AccountBox.SelectedItem = host.Remotes.FirstOrDefault(r => r.Name == remote) ?? host.Remotes.FirstOrDefault();
        var used = DriveLetterAllocator.UsedLetters();
        var taken = host.Settings.Mounts.Select(m => m.DriveLetter?.ToUpperInvariant()).Where(l => l != null).ToHashSet();
        var letters = new List<string> { "Auto" };
        letters.AddRange(DriveLetterAllocator.FreeLetters(used).Select(c => c.ToString()).Where(l => !taken.Contains(l)));
        LetterBox.ItemsSource = letters;
        var preferred = DriveLetterAllocator.Choose(null, used.Concat(taken.Select(t => t![0])).ToHashSet());
        LetterBox.SelectedItem = preferred?.ToString() ?? "Auto";
    }

    public MountDefinition? Result { get; private set; }

    private void AccountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccountBox.SelectedItem is RemoteEntry r && string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = r.Name;
        Folders.ItemsSource = null;
    }

    private async void Browse(object sender, RoutedEventArgs e)
    {
        if (AccountBox.SelectedItem is not RemoteEntry r || _host.Rclone is null) return;
        BrowseStatus.Text = "Listing…";
        try
        {
            var items = await _host.RcloneConfig.ListDirectoryAsync(_host.Rclone, r.Name, PathBox.Text.Trim());
            var dirs = items.Where(i => i.IsDir).Select(i => i.Name).OrderBy(n => n).ToList();
            Folders.ItemsSource = dirs;
            var files = items.Count(i => !i.IsDir);
            BrowseStatus.Text = $"{dirs.Count} folder(s), {files} file(s) ({ByteSize.Format(items.Where(i => !i.IsDir && i.Size > 0).Sum(i => i.Size))}) in '{(PathBox.Text.Length == 0 ? "/" : PathBox.Text)}'";
        }
        catch (Exception ex)
        {
            BrowseStatus.Text = Redactor.Redact(ex.Message);
        }
    }

    private void FolderOpen(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Folders.SelectedItem is not string name) return;
        PathBox.Text = string.IsNullOrWhiteSpace(PathBox.Text) ? name : PathBox.Text.TrimEnd('/') + "/" + name;
        Browse(sender, e);
    }

    private void Up(object sender, RoutedEventArgs e)
    {
        var p = PathBox.Text.Trim().TrimEnd('/');
        var i = p.LastIndexOf('/');
        PathBox.Text = i < 0 ? "" : p[..i];
        Browse(sender, e);
    }

    private void Ok(object sender, RoutedEventArgs e)
    {
        if (AccountBox.SelectedItem is not RemoteEntry r)
        {
            Status.Text = "Choose an account.";
            return;
        }
        var letter = LetterBox.SelectedItem as string;
        Result = new MountDefinition
        {
            DisplayName = string.IsNullOrWhiteSpace(NameBox.Text) ? r.Name : NameBox.Text.Trim(),
            RemoteName = r.Name,
            RemotePath = PathBox.Text.Trim().Trim('/'),
            DriveLetter = letter is null or "Auto" ? null : letter,
            ReadOnly = ReadOnlyBox.IsChecked == true,
            AutoMount = AutoBox.IsChecked == true,
        };
        DialogResult = true;
    }
}
