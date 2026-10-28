using CloudSSD.Core;
using CloudSSD.Games;

namespace CloudSSD.Tests;

public sealed class RecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudssd-cli-recovery-" + Guid.NewGuid().ToString("N"));
    public RecoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }

    [Fact]
    public void Transfer_intent_survives_reopening_and_paused_jobs_do_not_resume()
    {
        var file = Path.Combine(_root, "jobs.db");
        var journal = new TransferJournal(file);
        var job = new TransferJob("cache:a", "a", TransferKind.FullCache, null, TransferState.Running, 12345, 100000, "assets", DateTime.UtcNow);
        journal.Save(job);
        journal.Save(job with { Id = "local:b", GameId = "b", Kind = TransferKind.FullLocal, Destination = @"D:\Games\B", State = TransferState.Paused });
        journal = new TransferJournal(file);
        journal.RecoverInterrupted();
        Assert.Equal(TransferState.Interrupted, journal.All().Single(j => j.GameId == "a").State);
        var paused = journal.All().Single(j => j.GameId == "b");
        Assert.Equal(TransferState.Paused, paused.State);
        Assert.Equal(12345, paused.Bytes);
        Assert.Equal(@"D:\Games\B", paused.Destination);
    }

    [Fact]
    public void Corrupt_settings_recover_the_previous_atomic_backup()
    {
        var file = Path.Combine(_root, "settings.json");
        var store = new JsonSettingsStore(file);
        var settings = new AppSettings { FirstRunCompleted = true, Mounts = { new MountDefinition { Id = "persistent", DriveLetter = "T", Permanent = true } } };
        store.Save(settings);
        store.Save(settings);
        File.WriteAllText(file, "{interrupted");
        var recovered = new JsonSettingsStore(file).Load();
        Assert.True(recovered.FirstRunCompleted);
        Assert.Equal("persistent", Assert.Single(recovered.Mounts).Id);
        Assert.NotEmpty(Directory.GetFiles(_root, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Steam_game_is_identified_from_its_own_installed_manifest()
    {
        var apps = Path.Combine(_root, "steamapps");
        var game = Path.Combine(apps, "common", "A Game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(apps, "appmanifest_123.acf"), "\"AppState\" { \"appid\" \"123\" \"installdir\" \"Other Game\" }");
        Assert.Null(SteamLaunch.DetectAppId(game));
        File.WriteAllText(Path.Combine(apps, "appmanifest_456.acf"), "\"AppState\" { \"appid\" \"456\" \"installdir\" \"a game\" }");
        Assert.Equal(456u, SteamLaunch.DetectAppId(game));
        Assert.Null(SteamLaunch.DetectAppId(Path.Combine(_root, "portable")));
    }
}
