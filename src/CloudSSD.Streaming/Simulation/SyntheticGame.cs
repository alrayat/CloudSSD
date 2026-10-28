using CloudSSD.Core;

namespace CloudSSD.Streaming.Simulation;

/// <summary>One read in an access trace. ThinkMs is CPU/GPU work the game does before issuing this read.</summary>
public sealed record TraceRead(string Path, long Offset, int Length, double ThinkMs, string Phase);

public sealed record GameTrace(string Name, IReadOnlyDictionary<string, long> Files, IReadOnlyList<TraceRead> Reads)
{
    public long UniqueBytes(long blockSize) =>
        Reads.SelectMany(r => BlockMath.BlocksFor(r.Offset, r.Length, blockSize).Select(b => (r.Path, b))).Distinct().LongCount() * blockSize;
}

/// <summary>
/// Deterministic synthetic game access traces with the structure seen in real titles: executable/DLL page-ins,
/// config reads, archive index reads (header + tail), a sequential shader-cache read, scattered menu assets, then a
/// level load with long sequential runs plus scattered level-specific blocks. Sessions of the same game share the
/// startup pattern with jitter (skips, swaps, noise reads) and pick different levels, so prediction has to generalize.
/// These traces are synthetic and documented as such; they are not recordings of any commercial game.
/// </summary>
public static class SyntheticGame
{
    public static GameTrace Session(int gameSeed, int sessionIndex, int? level = null, double scale = 1.0)
    {
        var g = new Random(gameSeed);           // game structure (fixed across sessions)
        var s = new Random(gameSeed * 7919 + sessionIndex * 104729 + 17); // per-session jitter
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["game.exe"] = 48 * ByteSize.MiB,
            ["engine.dll"] = 24 * ByteSize.MiB,
            ["shaders.cache"] = (long)(256 * ByteSize.MiB * scale),
        };
        for (int i = 0; i < 6; i++) files[$"bin/lib{i}.dll"] = (1 + g.Next(4)) * ByteSize.MiB;
        for (int i = 0; i < 10; i++) files[$"config/settings{i}.ini"] = 4096;
        for (int i = 0; i < 6; i++) files[$"data/archive{i:00}.pak"] = (long)((2 + g.Next(5)) * ByteSize.GiB * scale);

        var reads = new List<(TraceRead R, bool Essential)>();
        void Add(string path, long off, int len, double think, string phase, bool essential = true) =>
            reads.Add((new TraceRead(path, Math.Clamp(off, 0, Math.Max(0, files[path] - len)), len, think, phase), essential));

        // 1. Executable and DLL page-ins (random pages, fixed per game).
        foreach (var exe in new[] { "game.exe", "engine.dll" }.Concat(Enumerable.Range(0, 6).Select(i => $"bin/lib{i}.dll")))
        {
            Add(exe, 0, 4096, 0.3, "startup");
            var pages = exe == "game.exe" ? 48 : exe == "engine.dll" ? 24 : 8;
            for (int p = 0; p < pages; p++) Add(exe, (long)(g.NextDouble() * files[exe]) / 4096 * 4096, 65536, 0.4, "startup");
        }
        // 2. Config files.
        for (int i = 0; i < 10; i++) Add($"config/settings{i}.ini", 0, 4096, 0.2, "startup");
        // 3. Archive headers and tail indexes.
        for (int i = 0; i < 6; i++)
        {
            var pak = $"data/archive{i:00}.pak";
            Add(pak, 0, 4096, 0.3, "startup");
            var idx = files[pak] - 8 * ByteSize.MiB;
            for (long o = 0; o < 8 * ByteSize.MiB; o += 256 * 1024) Add(pak, idx + o, 256 * 1024, 0.2, "startup");
        }
        // 4. Shader cache: sequential.
        for (long o = 0; o < (long)(64 * ByteSize.MiB * scale); o += 256 * 1024) Add("shaders.cache", o, 256 * 1024, 0.15, "startup");
        // 5. Menu assets: scattered but identical every launch.
        for (int i = 0; i < 400; i++)
        {
            var pak = $"data/archive{g.Next(6):00}.pak";
            var len = 64 * 1024 * (1 + g.Next(8));
            Add(pak, (long)(g.NextDouble() * (files[pak] - 16 * ByteSize.MiB)), len, 1.5, "menu");
        }

        // Per-level structure (fixed per game), choose level for this session.
        var levels = Enumerable.Range(0, 3).Select(_ =>
        {
            var pak = $"data/archive{g.Next(6):00}.pak";
            var start = (long)(g.NextDouble() * (files[pak] - 400 * ByteSize.MiB));
            var scattered = Enumerable.Range(0, 200).Select(_ =>
            {
                var p2 = $"data/archive{g.Next(6):00}.pak";
                return (p2, (long)(g.NextDouble() * (files[p2] - ByteSize.MiB)));
            }).ToList();
            return (pak, start, scattered);
        }).ToList();

        // Jitter the startup/menu part: skips, local swaps, noise reads.
        var jittered = new List<TraceRead>();
        foreach (var (r, _) in reads)
        {
            if (s.NextDouble() < 0.03) continue;
            jittered.Add(r);
            if (s.NextDouble() < 0.05)
            {
                var pak = $"data/archive{s.Next(6):00}.pak";
                jittered.Add(new TraceRead(pak, (long)(s.NextDouble() * (files[pak] - ByteSize.MiB)), 65536, 1, "noise"));
            }
        }
        for (int i = 1; i < jittered.Count; i++)
            if (s.NextDouble() < 0.05) (jittered[i - 1], jittered[i]) = (jittered[i], jittered[i - 1]);

        // 6. Level load.
        var lv = levels[level ?? s.Next(3)];
        var regionLen = (long)(300 * ByteSize.MiB * scale);
        for (long o = 0; o < regionLen; o += 128 * 1024)
        {
            if (s.NextDouble() < 0.02) continue;
            jittered.Add(new TraceRead(lv.pak, lv.start + o, 128 * 1024, 0.25, "level"));
        }
        foreach (var (p2, off) in lv.scattered)
            if (s.NextDouble() > 0.03) jittered.Add(new TraceRead(p2, off, 64 * 1024 * (1 + s.Next(4)), 0.8, "level"));

        return new GameTrace($"synthetic-{gameSeed}-s{sessionIndex}", files, jittered);
    }
}
