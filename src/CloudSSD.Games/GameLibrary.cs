using System.Text.Json;
using CloudSSD.Core;
using Microsoft.Data.Sqlite;

namespace CloudSSD.Games;

/// <summary>Game library persisted in the CloudSSD SQLite database.</summary>
public sealed class GameLibrary
{
    private readonly string _cs;

    public GameLibrary(string databasePath)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS games(id TEXT PRIMARY KEY, json TEXT NOT NULL, updated TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    public event Action? Changed;

    public List<GameEntry> All()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM games ORDER BY updated DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<GameEntry>();
        while (r.Read())
        {
            try
            {
                if (JsonSerializer.Deserialize<GameEntry>(r.GetString(0), JsonSettingsStore.Options) is { } g) list.Add(g);
            }
            catch (JsonException) { /* skip damaged row */ }
        }
        return list;
    }

    public GameEntry? Get(string id) => All().FirstOrDefault(g => g.Id == id);

    public void Save(GameEntry g)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO games(id, json, updated) VALUES($i,$j,$u) ON CONFLICT(id) DO UPDATE SET json=$j, updated=$u";
        cmd.Parameters.AddWithValue("$i", g.Id);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(g, JsonSettingsStore.Options));
        cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString("o"));
        cmd.ExecuteNonQuery();
        Changed?.Invoke();
    }

    /// <summary>Removes the library entry only. Cloud files, local copies and save games are never deleted here.</summary>
    public void Remove(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM games WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.ExecuteNonQuery();
        Changed?.Invoke();
    }
}
