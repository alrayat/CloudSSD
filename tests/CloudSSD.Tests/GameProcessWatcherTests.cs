using CloudSSD.Games;

namespace CloudSSD.Tests;

public sealed class GameProcessWatcherTests
{
    [Fact]
    public void Reports_a_game_while_any_process_runs_from_its_folder_and_ends_after_the_grace_period()
    {
        var running = new List<string>();
        var events = new List<string>();
        using var w = new GameProcessWatcher(
            () => [new("gta", @"T:\SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced"), new("other", @"T:\Games\Other")],
            (id, exe) => events.Add($"start {id} {Path.GetFileName(exe)}"), id => events.Add($"end {id}"),
            imagePaths: () => running, autoStart: false) { Grace = TimeSpan.FromSeconds(10) };
        var t = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

        running.Add(@"C:\Program Files (x86)\Steam\steam.exe");
        w.Tick(t);
        Assert.Empty(events);

        // Steam starts the stub, which hands over to the Rockstar launcher (outside the folder) and then the game.
        running.Add(@"T:\SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced\PlayGTAV.exe");
        w.Tick(t.AddSeconds(3));
        running.RemoveAt(1);
        running.Add(@"C:\Program Files\Rockstar Games\Launcher\Launcher.exe");
        w.Tick(t.AddSeconds(6));   // between processes: still running (grace)
        running.Add(@"t:\steamlibrary\steamapps\common\grand theft auto v enhanced\GTA5_Enhanced.exe");
        w.Tick(t.AddSeconds(9));
        Assert.Equal(["start gta PlayGTAV.exe"], events);
        Assert.Contains("gta", w.Running);

        running.RemoveAll(p => p.Contains("Grand Theft", StringComparison.OrdinalIgnoreCase));
        w.Tick(t.AddSeconds(12));
        Assert.Single(events);
        w.Tick(t.AddSeconds(30));
        Assert.Equal(["start gta PlayGTAV.exe", "end gta"], events);
        Assert.Empty(w.Running);
    }

    [Theory]
    [InlineData(@"T:\Games\Foo\foo.exe", @"T:\Games\Foo", true)]
    [InlineData(@"T:\Games\Foo\bin\x64\foo.exe", @"T:\Games\Foo\", true)]
    [InlineData(@"T:\Games\FooBar\foo.exe", @"T:\Games\Foo", false)]
    [InlineData(@"T:\Games\Foo", @"T:\Games\Foo", false)]
    [InlineData(@"C:\x.exe", "", false)]
    public void Folder_match_is_by_whole_path_segments(string path, string folder, bool expected) =>
        Assert.Equal(expected, GameProcessWatcher.IsUnder(path, folder));

    [Fact]
    public void Lists_real_process_images_including_this_one()
    {
        var self = Environment.ProcessPath!;
        Assert.Contains(GameProcessWatcher.RunningImagePaths(), p => string.Equals(p, self, StringComparison.OrdinalIgnoreCase));
    }
}
