using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CloudSSD.App;
using CloudSSD.Core;
using CloudSSD.Games;

namespace CloudSSD.Desktop.Views;
public partial class CompletionActionsWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private sealed record SteamChoice(string Name, uint AppId, string Manifest)
    {
        public override string ToString() => Name;
    }
    public CompletionActionsWindow(AppHost host)
    {
        _host = host; InitializeComponent();
        _timer.Tick += (_, _) => Reload();
        Loaded += async (_, _) => { Reload(); _timer.Start(); await LoadGames(); };
        Closed += (_, _) => _timer.Stop();
    }
    private void Reload() => Jobs.ItemsSource = _host.CompletionActions.All().Reverse().Take(30).Select(j => new
    {
        j.Id, j.Name, Description = $"{j.Action}: {j.State}" +
            (j.DueUtc is { } due ? $" — in {Math.Max(0, (int)(due - DateTime.UtcNow).TotalSeconds)} seconds" : "") +
            (j.Detail != null ? ". " + Diagnostics.Redactor.Redact(j.Detail) : "")
    }).ToList();
    private async Task LoadGames()
    {
        Status.Text = "Looking for Steam installations on mounted cloud drives…";
        var choices = new List<SteamChoice>();
        foreach (var mount in _host.Mounts.Sessions.Where(m => m.State == MountState.Mounted))
        {
            try
            {
                var found = await BoundedFileSystem.Shared.RunAsync(() =>
                {
                    var list = new List<SteamChoice>();
                    foreach (var cached in SteamManifestCache.List(mount.CacheDir, mount.Definition.RemotePath))
                    {
                        var idMatch = Regex.Match(cached.Text, "\"appid\"\\s*\"([0-9]+)\"");
                        var nameMatch = Regex.Match(cached.Text, "\"name\"\\s*\"([^\"]+)\"");
                        if (uint.TryParse(idMatch.Groups[1].Value, out var id) && id > 0)
                            list.Add(new(nameMatch.Success ? nameMatch.Groups[1].Value : $"Steam game {id}", id,
                                Path.Combine(mount.Root, cached.RelativePath.Replace('/', '\\'))));
                    }
                    if (list.Count > 0) return list;
                    foreach (var apps in new[] { Path.Combine(mount.Root, "SteamLibrary", "steamapps"), Path.Combine(mount.Root, "steamapps") })
                    {
                        if (!Directory.Exists(apps)) continue;
                        foreach (var file in Directory.EnumerateFiles(apps, "appmanifest_*.acf").Take(200))
                        {
                            var text = File.ReadAllText(file);
                            string Field(string key) => Regex.Match(text, "\"" + key + "\"\\s*\"([^\"]*)\"").Groups[1].Value;
                            if (uint.TryParse(Field("appid"), out var id) && id > 0)
                                list.Add(new(string.IsNullOrEmpty(Field("name")) ? $"Steam game {id}" : Field("name"), id, file));
                        }
                    }
                    return list;
                }, TimeSpan.FromSeconds(8));
                choices.AddRange(found);
            }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { }
        }
        SteamGames.ItemsSource = choices.OrderBy(c => c.Name).ToList();
        SteamGames.SelectedIndex = choices.Count > 0 ? 0 : -1;
        Status.Text = choices.Count > 0 ? "The action waits for Steam's final installed state, including verification."
            : "Start an installation in Steam on a mounted cloud drive, then refresh.";
    }
    private async void RefreshGames(object s, RoutedEventArgs e) { if (s is Button b) b.IsEnabled = false; try { await LoadGames(); } finally { if (s is Button button) button.IsEnabled = true; } }
    private void Schedule(object s, RoutedEventArgs e)
    {
        if (SteamGames.SelectedItem is not SteamChoice game) { Status.Text = "Choose a Steam game first."; return; }
        _host.ArmCompletion("steam:" + game.Manifest.ToUpperInvariant(), game.Name, AfterCompletion.Action,
            steamManifest: game.Manifest, steamAppId: game.AppId);
        Status.Text = AfterCompletion.Action == CompletionAction.DoNothing ? "No action is scheduled for this game." : "Scheduled. You can cancel here at any time.";
        Reload();
    }
    private void CancelJob(object s, RoutedEventArgs e) { if (s is Button { Tag: string id }) _host.CancelCompletion(id); Reload(); }
    private void CloseClick(object s, RoutedEventArgs e) => Close();
}
