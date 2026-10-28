using System.Windows;
using CloudSSD.App;
using CloudSSD.Core;
using Microsoft.Win32;

namespace CloudSSD.Desktop.Views;

public partial class AddGameWindow : Window
{
    private sealed record DriveChoice(string Id, string Root, string Label);

    public AddGameWindow(AppHost host)
    {
        InitializeComponent();
        var drives = host.Mounts.Sessions.Where(s => s.State == MountState.Mounted)
            .Select(s => new DriveChoice(s.Id, s.Root, $"{s.DriveLetter}:  {s.Definition.DisplayName}")).ToList();
        DriveBox.ItemsSource = drives;
        DriveBox.SelectedIndex = drives.Count > 0 ? 0 : -1;
    }

    public string? MountId { get; private set; }
    public string? Folder { get; private set; }
    public string? GameName { get; private set; }

    private void Browse(object sender, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is not DriveChoice d) return;
        var dlg = new OpenFolderDialog { Title = "Select the game's folder on the cloud drive", InitialDirectory = d.Root };
        if (dlg.ShowDialog() != true) return;
        if (!dlg.FolderName.StartsWith(d.Root, StringComparison.OrdinalIgnoreCase))
        {
            Status.Text = $"The folder must be on drive {d.Root}.";
            return;
        }
        FolderBox.Text = dlg.FolderName[d.Root.Length..].Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = Path.GetFileName(dlg.FolderName.TrimEnd('\\'));
    }

    private async void Ok(object sender, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is not DriveChoice d)
        {
            Status.Text = "No mounted drive.";
            return;
        }
        var rel = FolderBox.Text.Trim().Replace('\\', '/').Trim('/');
        var button = sender as System.Windows.Controls.Button;
        if (button != null) button.IsEnabled = false;
        try
        {
            if (!await BoundedFileSystem.Shared.RunAsync(() => Directory.Exists(Path.Combine(d.Root, rel.Replace('/', '\\'))), TimeSpan.FromSeconds(5)))
            {
                Status.Text = "That folder does not exist on the drive.";
                return;
            }
            if (!IsVisible) return;
            MountId = d.Id;
            Folder = rel;
            GameName = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim();
            DialogResult = true;
        }
        catch (TimeoutException ex) { Status.Text = ex.Message; }
        finally { if (button != null) button.IsEnabled = true; }
    }
}
