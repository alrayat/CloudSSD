using CloudSSD.Games;

namespace CloudSSD.Tests;

public sealed class CompletionActionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudssd-cli-completion-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
    public CompletionActionsTests() => Directory.CreateDirectory(_root);
    private CompletionJournal Journal() => new(Path.Combine(_root, "jobs.db"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
    private CompletionJob Job(CompletionAction action = CompletionAction.ShutDown) => new("a", "Game", action, "game");

    [Fact]
    public void Steam_selector_uses_only_complete_cached_manifest_bytes_and_never_sparse_holes()
    {
        var rel = Path.Combine("Games", "SteamLibrary", "steamapps", "appmanifest_123.acf");
        var data = Path.Combine(_root, "vfs", "test", rel);
        var meta = Path.Combine(_root, "vfsMeta", "test", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(data)!); Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.WriteAllText(data, "abcdefgh");
        File.WriteAllText(meta, "{\"Size\":8,\"Rs\":[{\"Pos\":0,\"Size\":4}]}");
        Assert.Null(SteamManifestCache.Read(_root, "Games", "SteamLibrary/steamapps/appmanifest_123.acf"));
        Assert.Empty(SteamManifestCache.List(_root, "Games"));
        File.WriteAllText(meta, "{\"Size\":8,\"Rs\":[{\"Pos\":0,\"Size\":8}]}");
        Assert.Equal("abcdefgh", SteamManifestCache.Read(_root, "Games", "SteamLibrary/steamapps/appmanifest_123.acf"));
        Assert.Equal("SteamLibrary/steamapps/appmanifest_123.acf", Assert.Single(SteamManifestCache.List(_root, "Games")).RelativePath);
        File.WriteAllText(meta, "{interrupted");
        Assert.Null(SteamManifestCache.Read(_root, "Games", "SteamLibrary/steamapps/appmanifest_123.acf"));
    }

    [Theory]
    [InlineData("1026", "0", "1", false)] // GTA: downloaded, staged, still verifying.
    [InlineData("4", "10", "1", false)]
    [InlineData("4", "0", "0", false)]
    [InlineData("4", "10", "0", true)]
    [InlineData("6", "10", "0", false)]
    public void Steam_requires_a_final_verified_installed_state(string flags, string build, string validate, bool expected)
    {
        var manifest = $"\"AppState\" {{ \"appid\" \"123\" \"StateFlags\" \"{flags}\" \"buildid\" \"{build}\" " +
            $"\"SizeOnDisk\" \"1000\" \"FullValidateAfterNextUpdate\" \"{validate}\" " +
            "\"BytesDownloaded\" \"1000\" \"BytesToDownload\" \"1000\" \"BytesStaged\" \"1000\" \"BytesToStage\" \"1000\" " +
            "\"InstalledDepots\" { \"124\" { \"manifest\" \"999\" } } }";
        Assert.Equal(expected, SteamInstallCompletion.IsInstalled(manifest, 123));
        Assert.False(SteamInstallCompletion.IsInstalled(manifest, 456));
        Assert.False(SteamInstallCompletion.IsInstalled(manifest[..^2], 123));
        Assert.False(SteamInstallCompletion.IsInstalled(manifest.Replace("\"1000\" \"BytesToDownload\"", "\"100\" \"BytesToDownload\""), 123));
        Assert.False(SteamInstallCompletion.IsInstalled(manifest.Replace("\"124\" {", ""), 123));
    }

    [Fact]
    public async Task Paused_or_failed_transfer_never_executes_and_default_does_nothing()
    {
        var j = Journal(); j.Save(Job()); j.Save(Job(CompletionAction.DoNothing) with { Id = "default", TransferComplete = true });
        var calls = 0;
        var c = new CompletionCoordinator(j, (job, _) => Task.FromResult((job.TransferComplete, "Waiting")),
            (_, _) => { calls++; return Task.CompletedTask; });
        await c.TickAsync(_now); await c.TickAsync(_now.AddDays(1));
        Assert.Equal(0, calls); Assert.Equal(CompletionState.Waiting, j.All().Single(x => x.Id == "a").State);
    }

    [Fact]
    public async Task Writes_resuming_reset_the_countdown_and_power_runs_once_after_drain()
    {
        var j = Journal(); j.Save(Job() with { TransferComplete = true });
        var drained = true; var calls = 0;
        var c = new CompletionCoordinator(j, (_, _) => Task.FromResult((drained, "Pending cloud writes")),
            (_, _) => { calls++; return Task.CompletedTask; });
        await c.TickAsync(_now);
        await c.TickAsync(_now.AddSeconds(59)); Assert.Equal(0, calls);
        drained = false; await c.TickAsync(_now.AddSeconds(60));
        Assert.Null(j.All().Single().DueUtc);
        drained = true; await c.TickAsync(_now.AddSeconds(90));
        await c.TickAsync(_now.AddSeconds(149)); Assert.Equal(0, calls);
        await c.TickAsync(_now.AddSeconds(150)); await c.TickAsync(_now.AddDays(1)); Assert.Equal(1, calls);
        var reopened = Journal(); new CompletionCoordinator(reopened, (_, _) => Task.FromResult((true, "")), (_, _) => { calls++; return Task.CompletedTask; }).Recover();
        Assert.Equal(CompletionState.Done, reopened.All().Single().State);
    }

    [Fact]
    public async Task Cancellation_wins_even_while_readiness_check_is_awaiting()
    {
        var j = Journal(); j.Save(Job()); var calls = 0;
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var c = new CompletionCoordinator(j, async (_, _) => { entered.SetResult(); await release.Task; return (true, ""); },
            (_, _) => { calls++; return Task.CompletedTask; }, TimeSpan.Zero);
        var tick = c.TickAsync(_now); await entered.Task; j.Cancel("a"); release.SetResult(); await tick;
        Assert.Equal(0, calls); Assert.Equal(CompletionState.Cancelled, j.All().Single().State);
    }

    [Fact]
    public async Task Restart_preserves_intent_but_restarts_the_full_countdown()
    {
        var j = Journal(); j.Save(Job(CompletionAction.Restart) with { TransferComplete = true,
            State = CompletionState.Countdown, DueUtc = _now.AddHours(-1) });
        j = Journal(); var calls = 0;
        var c = new CompletionCoordinator(j, (job, _) => Task.FromResult((job.TransferComplete, "")),
            (_, _) => { calls++; return Task.CompletedTask; });
        c.Recover(); await c.TickAsync(_now); Assert.Equal(0, calls);
        Assert.Equal(_now.AddSeconds(60), j.All().Single().DueUtc);
        await c.TickAsync(_now.AddSeconds(60)); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Executor_failure_is_recorded_without_automatic_replay()
    {
        var j = Journal(); j.Save(Job()); var calls = 0;
        var c = new CompletionCoordinator(j, (_, _) => Task.FromResult((true, "")),
            (_, _) => { calls++; throw new IOException("Windows declined"); }, TimeSpan.Zero);
        await c.TickAsync(_now); await c.TickAsync(_now.AddDays(1));
        Assert.Equal(1, calls); Assert.Equal(CompletionState.Failed, j.All().Single().State);
    }

    [Fact]
    public async Task Multiple_power_selections_cannot_repeatedly_request_shutdown_after_a_restart()
    {
        var j = Journal(); j.Save(Job()); j.Save(Job(CompletionAction.Restart) with { Id = "second" }); var calls = 0;
        var c = new CompletionCoordinator(j, (_, _) => Task.FromResult((true, "")),
            (_, _) => { calls++; return Task.CompletedTask; }, TimeSpan.Zero);
        await c.TickAsync(_now); await c.TickAsync(_now.AddMinutes(1));
        Assert.Equal(1, calls); Assert.Single(j.All(), x => x.State == CompletionState.Cancelled);
    }
}
