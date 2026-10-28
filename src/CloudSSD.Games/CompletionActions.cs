using System.Text.Json;
using System.Text.RegularExpressions;
using CloudSSD.Core;
using Microsoft.Data.Sqlite;

namespace CloudSSD.Games;

public enum CompletionAction { DoNothing, LaunchGame, ShutDown, Restart }
public enum CompletionState { Waiting, Countdown, Done, Cancelled, Failed }
public sealed record CompletionJob(string Id, string Name, CompletionAction Action, string? GameId,
    string? SteamManifest = null, uint? SteamAppId = null, bool TransferComplete = false,
    CompletionState State = CompletionState.Waiting, DateTime? DueUtc = null, string? Detail = null);

/// <summary>One-shot completion intent; independent of transfer progress and never rewrites Steam manifests.</summary>
public sealed class CompletionJournal
{
    private readonly string _connection;
    private readonly object _gate = new();
    public CompletionJournal(string database)
    {
        _connection = new SqliteConnectionStringBuilder { DataSource = database, DefaultTimeout = 10 }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS completion_jobs(id TEXT PRIMARY KEY, json TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connection); c.Open(); return c; }
    public IReadOnlyList<CompletionJob> All()
    {
        lock (_gate)
        {
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT json FROM completion_jobs";
            using var r = cmd.ExecuteReader(); var jobs = new List<CompletionJob>();
            while (r.Read())
                try { if (JsonSerializer.Deserialize<CompletionJob>(r.GetString(0), JsonSettingsStore.Options) is { } j) jobs.Add(j); }
                catch (JsonException) { }
            return jobs;
        }
    }
    public void Save(CompletionJob job)
    {
        lock (_gate)
        {
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO completion_jobs VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=$json";
            cmd.Parameters.AddWithValue("$id", job.Id);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(job, JsonSettingsStore.Options));
            cmd.ExecuteNonQuery();
        }
    }
    public void Cancel(string id) => Update(id, j => j with { State = CompletionState.Cancelled, DueUtc = null, Detail = "Cancelled." });
    public void MarkComplete(string id) => Update(id, j => j with { TransferComplete = true });
    public void Update(string id, Func<CompletionJob, CompletionJob> change)
    {
        lock (_gate) if (All().FirstOrDefault(j => j.Id == id) is { } j) Save(change(j));
    }
    internal bool Consume(CompletionJob expected)
    {
        lock (_gate)
        {
            if (All().FirstOrDefault(j => j.Id == expected.Id) != expected) return false;
            // Persist before the external side effect: a crash must never replay a power action.
            Save(expected with { State = CompletionState.Done, DueUtc = null, Detail = "Action requested." });
            return true;
        }
    }
}

/// <summary>Read-only, fail-closed final installation check. Downloaded bytes alone are insufficient.</summary>
public static class SteamInstallCompletion
{
    public static bool IsInstalled(string text, uint expectedAppId)
    {
        // Reject partial writes/truncated manifests rather than interpreting a few surviving fields as success.
        var depth = 0; var quoted = false; var escaped = false;
        foreach (var ch in text)
        {
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') quoted = false;
            }
            else if (ch == '"') quoted = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth < 0) return false;
        }
        if (quoted || depth != 0 || !Regex.IsMatch(text, "^\\s*\"AppState\"\\s*\\{")) return false;
        string? Field(string key)
        {
            var matches = Regex.Matches(text, "\"" + Regex.Escape(key) + "\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return matches.Count == 1 ? matches[0].Groups[1].Value : null;
        }
        if (!uint.TryParse(Field("appid"), out var app) || app != expectedAppId || Field("StateFlags") != "4" ||
            !ulong.TryParse(Field("buildid"), out var build) || build == 0 ||
            !long.TryParse(Field("SizeOnDisk"), out var size) || size <= 0 ||
            !Regex.IsMatch(text, "\"InstalledDepots\"\\s*\\{\\s*\"[0-9]+\"")) return false;
        foreach (var pair in new[] { ("BytesDownloaded", "BytesToDownload"), ("BytesStaged", "BytesToStage") })
        {
            var done = Field(pair.Item1); var total = Field(pair.Item2);
            if (done == null && total == null) continue;
            if (!long.TryParse(done, out var d) || !long.TryParse(total, out var t) || d < 0 || t < 0 || d < t) return false;
        }
        return Field("FullValidateAfterNextUpdate") is null or "0";
    }
}

public sealed class CompletionCoordinator(CompletionJournal journal,
    Func<CompletionJob, CancellationToken, Task<(bool Ready, string Detail)>> ready,
    Func<CompletionJob, CancellationToken, Task> execute,
    TimeSpan? countdown = null)
{
    private readonly SemaphoreSlim _tick = new(1);
    public void Recover()
    {
        foreach (var job in journal.All().Where(j => j.State == CompletionState.Countdown))
            journal.Save(job with { State = CompletionState.Waiting, DueUtc = null, Detail = "Rechecking after restart." });
    }
    public async Task TickAsync(DateTime utcNow, CancellationToken ct = default)
    {
        if (!await _tick.WaitAsync(0, ct)) return;
        try
        {
            foreach (var job in journal.All().Where(j => j.State is CompletionState.Waiting or CompletionState.Countdown))
            {
                ct.ThrowIfCancellationRequested();
                if (job.Action == CompletionAction.DoNothing) { journal.Cancel(job.Id); continue; }
                try
                {
                    var state = await ready(job, ct);
                    var next = !state.Ready ? job with { State = CompletionState.Waiting, DueUtc = null, Detail = state.Detail }
                        : job.State != CompletionState.Countdown ? job with { State = CompletionState.Countdown,
                            DueUtc = utcNow + (countdown ?? TimeSpan.FromSeconds(60)), Detail = "Ready. Cancel to keep the PC running." } : job;
                    // A cancellation/replacement during an awaited readiness check wins.
                    journal.Update(job.Id, current => current == job ? next : current);
                    if (next.State != CompletionState.Countdown || next.DueUtc > utcNow) continue;
                    ct.ThrowIfCancellationRequested();
                    if (!journal.Consume(next)) continue;
                    if (next.Action is CompletionAction.ShutDown or CompletionAction.Restart)
                        foreach (var other in journal.All().Where(j => j.Id != next.Id &&
                            j.State is CompletionState.Waiting or CompletionState.Countdown &&
                            j.Action is CompletionAction.ShutDown or CompletionAction.Restart)) journal.Cancel(other.Id);
                    await execute(next, ct);
                    // One power request per tick. Other actions remain cancellable until the next check.
                    if (next.Action is CompletionAction.ShutDown or CompletionAction.Restart) break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { journal.Update(job.Id, j => j.State == CompletionState.Cancelled ? j : j with {
                    State = CompletionState.Failed, DueUtc = null, Detail = e.Message }); }
            }
        }
        finally { _tick.Release(); }
    }
}
