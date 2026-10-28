using CloudSSD.Core;
using Microsoft.Data.Sqlite;

namespace CloudSSD.Streaming;

/// <summary>Everything observed in one game session (application reads only, paths relative to the game root).</summary>
public sealed class SessionRecorder
{
    private readonly Dictionary<BlockKey, (double FirstMs, int Reads)> _blocks = new();
    private readonly object _gate = new();

    public SessionRecorder(DateTime start) => Start = start;

    public DateTime Start { get; }
    public long BytesRead;
    public long Hits;
    public long Misses;
    public double StallMs;
    public double LongestStallMs;
    public TransitionModel Transitions { get; } = new();

    /// <summary>
    /// Records an application read. First-access times are stored in game-progress time (wall time minus time spent
    /// waiting on cloud misses), so a launch profiled on a slow link describes the same startup as on a fast one and the
    /// startup window is not filled with network waiting.
    /// </summary>
    public void Record(BlockKey block, DateTime at)
    {
        lock (_gate)
        {
            var ms = Math.Max(0, (at - Start).TotalMilliseconds - StallMs);
            _blocks[block] = _blocks.TryGetValue(block, out var v) ? (v.FirstMs, v.Reads + 1) : (ms, 1);
        }
        Transitions.Observe(block, at);
    }

    public IReadOnlyList<(BlockKey Block, double FirstMs, int Reads)> Blocks()
    {
        lock (_gate) return _blocks.Select(kv => (kv.Key, kv.Value.FirstMs, kv.Value.Reads)).OrderBy(x => x.FirstMs).ToList();
    }

    public int BlockCount { get { lock (_gate) return _blocks.Count; } }

    public bool HasSeen(BlockKey block) { lock (_gate) return _blocks.ContainsKey(block); }
}

public sealed record ProfileSummary(string GameId, int Sessions, int DistinctBlocks, long StartupBytesEstimate, DateTime? LastSession, long BlockSize);

public sealed record PredictorRecord(string Predictor, long Issued, long Useful, long Wasted, bool Disabled)
{
    public double? Accuracy => Useful + Wasted == 0 ? null : Useful / (double)(Useful + Wasted);
}

/// <summary>SQLite persistence for per-game access profiles. Paths are stored relative to the game folder.</summary>
public sealed class ProfileStore
{
    private readonly string _connectionString;

    public ProfileStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS profiles(game_id TEXT PRIMARY KEY, block_size INTEGER NOT NULL, updated TEXT);
            CREATE TABLE IF NOT EXISTS sessions(id INTEGER PRIMARY KEY AUTOINCREMENT, game_id TEXT NOT NULL, started TEXT NOT NULL,
                duration_ms REAL, blocks INTEGER, bytes_read INTEGER, hits INTEGER, misses INTEGER, stall_ms REAL, longest_stall_ms REAL,
                useful_bytes INTEGER, wasted_bytes INTEGER, mode TEXT);
            CREATE INDEX IF NOT EXISTS ix_sessions_game ON sessions(game_id, id);
            CREATE TABLE IF NOT EXISTS session_blocks(session_id INTEGER NOT NULL, path TEXT NOT NULL, block INTEGER NOT NULL,
                first_ms REAL NOT NULL, reads INTEGER NOT NULL, PRIMARY KEY(session_id, path, block));
            CREATE TABLE IF NOT EXISTS transitions(game_id TEXT NOT NULL, from_path TEXT NOT NULL, from_block INTEGER NOT NULL,
                to_path TEXT NOT NULL, to_block INTEGER NOT NULL, count INTEGER NOT NULL,
                PRIMARY KEY(game_id, from_path, from_block, to_path, to_block));
            CREATE TABLE IF NOT EXISTS occurrences(game_id TEXT NOT NULL, path TEXT NOT NULL, block INTEGER NOT NULL, count INTEGER NOT NULL,
                PRIMARY KEY(game_id, path, block));
            CREATE TABLE IF NOT EXISTS predictor_stats(game_id TEXT NOT NULL, predictor TEXT NOT NULL, issued INTEGER, useful INTEGER,
                wasted INTEGER, disabled INTEGER, PRIMARY KEY(game_id, predictor));
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public long SaveSession(string gameId, long blockSize, SessionRecorder rec, DateTime end, PredictionAccounting? accounting, string mode,
        IReadOnlyCollection<string>? disabledPredictors = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO profiles(game_id, block_size, updated) VALUES($g,$b,$u) ON CONFLICT(game_id) DO UPDATE SET updated=$u";
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$b", blockSize);
            cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        var totals = accounting?.Totals() ?? (0, 0, null);
        long sessionId;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sessions(game_id, started, duration_ms, blocks, bytes_read, hits, misses, stall_ms, longest_stall_ms, useful_bytes, wasted_bytes, mode)
                VALUES($g,$s,$d,$bl,$br,$h,$m,$st,$ls,$ub,$wb,$mode); SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$s", rec.Start.ToString("o"));
            cmd.Parameters.AddWithValue("$d", (end - rec.Start).TotalMilliseconds);
            cmd.Parameters.AddWithValue("$bl", rec.BlockCount);
            cmd.Parameters.AddWithValue("$br", rec.BytesRead);
            cmd.Parameters.AddWithValue("$h", rec.Hits);
            cmd.Parameters.AddWithValue("$m", rec.Misses);
            cmd.Parameters.AddWithValue("$st", rec.StallMs);
            cmd.Parameters.AddWithValue("$ls", rec.LongestStallMs);
            cmd.Parameters.AddWithValue("$ub", totals.Item1);
            cmd.Parameters.AddWithValue("$wb", totals.Item2);
            cmd.Parameters.AddWithValue("$mode", mode);
            sessionId = (long)cmd.ExecuteScalar()!;
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO session_blocks(session_id, path, block, first_ms, reads) VALUES($s,$p,$b,$f,$r)";
            var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pp = cmd.Parameters.Add("$p", SqliteType.Text);
            var pb = cmd.Parameters.Add("$b", SqliteType.Integer);
            var pf = cmd.Parameters.Add("$f", SqliteType.Real);
            var pr = cmd.Parameters.Add("$r", SqliteType.Integer);
            cmd.Prepare();
            foreach (var (block, first, reads) in rec.Blocks())
            {
                ps.Value = sessionId; pp.Value = block.Path; pb.Value = block.Index; pf.Value = first; pr.Value = reads;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO transitions(game_id, from_path, from_block, to_path, to_block, count) VALUES($g,$fp,$fb,$tp,$tb,$c)
                ON CONFLICT(game_id, from_path, from_block, to_path, to_block) DO UPDATE SET count = count + $c
                """;
            cmd.Parameters.AddWithValue("$g", gameId);
            var fp = cmd.Parameters.Add("$fp", SqliteType.Text);
            var fb = cmd.Parameters.Add("$fb", SqliteType.Integer);
            var tp = cmd.Parameters.Add("$tp", SqliteType.Text);
            var tb = cmd.Parameters.Add("$tb", SqliteType.Integer);
            var cc = cmd.Parameters.Add("$c", SqliteType.Integer);
            cmd.Prepare();
            foreach (var (from, to, count) in rec.Transitions.Edges())
            {
                fp.Value = from.Path; fb.Value = from.Index; tp.Value = to.Path; tb.Value = to.Index; cc.Value = count;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO occurrences(game_id, path, block, count) VALUES($g,$p,$b,$c)
                ON CONFLICT(game_id, path, block) DO UPDATE SET count = count + $c
                """;
            cmd.Parameters.AddWithValue("$g", gameId);
            var pp = cmd.Parameters.Add("$p", SqliteType.Text);
            var pb = cmd.Parameters.Add("$b", SqliteType.Integer);
            var pc = cmd.Parameters.Add("$c", SqliteType.Integer);
            cmd.Prepare();
            foreach (var (block, count) in rec.Transitions.Occurrences())
            {
                pp.Value = block.Path; pb.Value = block.Index; pc.Value = count;
                cmd.ExecuteNonQuery();
            }
        }
        if (accounting != null)
        {
            foreach (var (name, s) in accounting.Snapshot())
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO predictor_stats(game_id, predictor, issued, useful, wasted, disabled) VALUES($g,$p,$i,$u,$w,$d)
                    ON CONFLICT(game_id, predictor) DO UPDATE SET issued=issued+$i, useful=useful+$u, wasted=wasted+$w, disabled=$d
                    """;
                cmd.Parameters.AddWithValue("$g", gameId);
                cmd.Parameters.AddWithValue("$p", name);
                cmd.Parameters.AddWithValue("$i", s.IssuedBlocks);
                cmd.Parameters.AddWithValue("$u", s.UsefulBlocks);
                cmd.Parameters.AddWithValue("$w", s.WastedBlocks);
                cmd.Parameters.AddWithValue("$d", s.AutoDisabled || disabledPredictors?.Contains(name) == true ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return sessionId;
    }

    /// <summary>Per-block history over the most recent <paramref name="lastSessions"/> sessions (median first-access time).</summary>
    public (int Sessions, List<BlockHistory> Blocks) LoadHistory(string gameId, int lastSessions = 5)
    {
        using var c = Open();
        var ids = new List<long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM sessions WHERE game_id=$g ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$n", lastSessions);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        if (ids.Count == 0) return (0, []);
        var times = new Dictionary<BlockKey, List<double>>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT path, block, first_ms FROM session_blocks WHERE session_id IN ({string.Join(',', ids)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var k = new BlockKey(r.GetString(0), r.GetInt64(1));
                if (!times.TryGetValue(k, out var l)) times[k] = l = new List<double>();
                l.Add(r.GetDouble(2));
            }
        }
        var list = times.Select(kv =>
        {
            var t = kv.Value.OrderBy(x => x).ToList();
            var median = t.Count % 2 == 1 ? t[t.Count / 2] : (t[t.Count / 2 - 1] + t[t.Count / 2]) / 2;
            return new BlockHistory(kv.Key, t.Count, median);
        }).ToList();
        return (ids.Count, list);
    }

    public TransitionModel LoadTransitions(string gameId, int maxSuccessorsPerBlock = 8)
    {
        var model = new TransitionModel();
        using var c = Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT path, block, count FROM occurrences WHERE game_id=$g";
            cmd.Parameters.AddWithValue("$g", gameId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) model.AddOccurrences(new BlockKey(r.GetString(0), r.GetInt64(1)), r.GetInt32(2));
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT from_path, from_block, to_path, to_block, count FROM (
                  SELECT *, ROW_NUMBER() OVER (PARTITION BY from_path, from_block ORDER BY count DESC) AS rn
                  FROM transitions WHERE game_id=$g) WHERE rn <= $k
                """;
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.Parameters.AddWithValue("$k", maxSuccessorsPerBlock);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                model.AddEdge(new BlockKey(r.GetString(0), r.GetInt64(1)), new BlockKey(r.GetString(2), r.GetInt64(3)), r.GetInt32(4));
        }
        return model;
    }

    public List<PredictorRecord> LoadPredictorStats(string gameId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT predictor, issued, useful, wasted, disabled FROM predictor_stats WHERE game_id=$g";
        cmd.Parameters.AddWithValue("$g", gameId);
        using var r = cmd.ExecuteReader();
        var list = new List<PredictorRecord>();
        while (r.Read()) list.Add(new PredictorRecord(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4) == 1));
        return list;
    }

    public ProfileSummary Summary(string gameId, TimeSpan startupWindow)
    {
        long blockSize = ByteSize.MiB;
        DateTime? last = null;
        using (var c = Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT block_size FROM profiles WHERE game_id=$g";
            cmd.Parameters.AddWithValue("$g", gameId);
            if (cmd.ExecuteScalar() is long b) blockSize = b;
            cmd.CommandText = "SELECT MAX(started) FROM sessions WHERE game_id=$g";
            if (cmd.ExecuteScalar() is string s && DateTime.TryParse(s, out var dt)) last = dt;
        }
        var (sessions, blocks) = LoadHistory(gameId);
        var startup = blocks.Count(b => b.MedianFirstAccessMs <= startupWindow.TotalMilliseconds && (b.SessionsSeen + 0.5) / (sessions + 1.0) > 0.5);
        return new ProfileSummary(gameId, sessions, blocks.Count, startup * blockSize, last, blockSize);
    }

    public List<(DateTime Started, double DurationMs, long Hits, long Misses, double StallMs, long UsefulBytes, long WastedBytes, string Mode)> Sessions(string gameId, int max = 20)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT started, duration_ms, hits, misses, stall_ms, useful_bytes, wasted_bytes, mode FROM sessions WHERE game_id=$g ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$g", gameId);
        cmd.Parameters.AddWithValue("$n", max);
        using var r = cmd.ExecuteReader();
        var list = new List<(DateTime, double, long, long, double, long, long, string)>();
        while (r.Read())
            list.Add((DateTime.Parse(r.GetString(0)), r.GetDouble(1), r.GetInt64(2), r.GetInt64(3), r.GetDouble(4), r.GetInt64(5), r.GetInt64(6), r.IsDBNull(7) ? "" : r.GetString(7)));
        return list;
    }

    public void DeleteProfile(string gameId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var sql in new[]
                 {
                     "DELETE FROM session_blocks WHERE session_id IN (SELECT id FROM sessions WHERE game_id=$g)",
                     "DELETE FROM sessions WHERE game_id=$g", "DELETE FROM transitions WHERE game_id=$g",
                     "DELETE FROM occurrences WHERE game_id=$g", "DELETE FROM predictor_stats WHERE game_id=$g",
                     "DELETE FROM profiles WHERE game_id=$g"
                 })
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$g", gameId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
