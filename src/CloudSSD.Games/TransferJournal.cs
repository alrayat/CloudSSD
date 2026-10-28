using System.Text.Json;
using CloudSSD.Core;
using Microsoft.Data.Sqlite;

namespace CloudSSD.Games;

public enum TransferKind { FullCache, FullLocal }
public enum TransferState { Running, Interrupted, Waiting, Paused, Completed, Failed }
public sealed record TransferJob(string Id, string GameId, TransferKind Kind, string? Destination, TransferState State,
    long Bytes, long Total, string? Detail, DateTime UpdatedUtc);

/// <summary>Durable intent and progress; actual downloaded ranges remain owned by rclone.</summary>
public sealed class TransferJournal
{
    private readonly string _connection;
    public TransferJournal(string database)
    {
        _connection = new SqliteConnectionStringBuilder { DataSource = database, DefaultTimeout = 10 }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS transfer_jobs(id TEXT PRIMARY KEY, json TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connection); c.Open(); return c; }
    public IReadOnlyList<TransferJob> All()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM transfer_jobs";
        using var reader = cmd.ExecuteReader();
        var jobs = new List<TransferJob>();
        while (reader.Read())
        {
            try { if (JsonSerializer.Deserialize<TransferJob>(reader.GetString(0), JsonSettingsStore.Options) is { } job) jobs.Add(job); }
            catch (JsonException) { /* A damaged job must not prevent recovery of the others. */ }
        }
        return jobs;
    }
    public void Save(TransferJob job)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO transfer_jobs VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=$json";
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(job, JsonSettingsStore.Options));
        cmd.ExecuteNonQuery();
    }
    public void RecoverInterrupted()
    {
        foreach (var job in All().Where(j => j.State == TransferState.Running))
            Save(job with { State = TransferState.Interrupted, Detail = "Resuming after the previous owner stopped.", UpdatedUtc = DateTime.UtcNow });
    }
}
