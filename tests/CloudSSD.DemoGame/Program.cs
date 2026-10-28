using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace CloudSSD.DemoGame;

/// <summary>
/// CloudSSD demo game: a DRM-free test workload that behaves like a game's loader. It reads asset archives with a
/// fixed, game-like pattern (archive headers and tail indexes, scattered menu assets, then a level with a long
/// sequential region plus scattered reads), shows a loading screen and records its own load time.
///
///   CloudSSD.DemoGame.exe [--level N] [--autoexit] [--generate &lt;dir&gt;] [--results &lt;file&gt;]
/// </summary>
internal static class Program
{
    public const int Archives = 4;
    public const long ArchiveSize = 96L * 1024 * 1024;

    [STAThread]
    private static int Main(string[] args)
    {
        if (Arg(args, "--generate") is { } dir) return Generate(dir);
        ApplicationConfiguration.Initialize();
        var level = int.TryParse(Arg(args, "--level"), out var l) ? l : 0;
        var results = Arg(args, "--results") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudSSD-DemoGame", "results.jsonl");
        using var form = new LoaderForm(AppContext.BaseDirectory, level, args.Contains("--autoexit"), results);
        Application.Run(form);
        return form.ExitCode;
    }

    private static string? Arg(string[] a, string n)
    {
        var i = Array.IndexOf(a, n);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    /// <summary>Creates the game's data files (deterministic content) next to a copy of the executable.</summary>
    private static int Generate(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        var buf = new byte[1 << 20];
        for (int a = 0; a < Archives; a++)
        {
            var rng = new Random(1000 + a);
            using var fs = File.Create(Path.Combine(dir, "data", $"archive{a:00}.pak"));
            for (long done = 0; done < ArchiveSize; done += buf.Length)
            {
                rng.NextBytes(buf);
                fs.Write(buf);
            }
        }
        File.WriteAllText(Path.Combine(dir, "config", "settings.ini"), "[video]\nwidth=1920\nheight=1080\n[audio]\nvolume=80\n");
        Console.WriteLine($"Generated demo game data in {dir}");
        return 0;
    }

    /// <summary>The read plan. Startup and menu reads are identical every launch; the level depends on --level.</summary>
    public static List<(string File, long Offset, int Length, string Phase)> Plan(int level)
    {
        var plan = new List<(string, long, int, string)>();
        plan.Add(("config/settings.ini", 0, 4096, "startup"));
        for (int a = 0; a < Archives; a++)
        {
            var f = $"data/archive{a:00}.pak";
            plan.Add((f, 0, 4096, "startup"));
            for (long o = ArchiveSize - 2 * 1024 * 1024; o < ArchiveSize; o += 256 * 1024) plan.Add((f, o, 256 * 1024, "startup"));
        }
        var menu = new Random(77);
        for (int i = 0; i < 120; i++)
            plan.Add(($"data/archive{menu.Next(Archives):00}.pak", (long)(menu.NextDouble() * (ArchiveSize - 4 * 1024 * 1024)), 64 * 1024 * (1 + menu.Next(4)), "menu"));
        var lv = new Random(500 + level);
        var levelArchive = $"data/archive{lv.Next(Archives):00}.pak";
        var start = (long)(lv.NextDouble() * (ArchiveSize - 40L * 1024 * 1024));
        for (long o = 0; o < 24L * 1024 * 1024; o += 128 * 1024) plan.Add((levelArchive, start + o, 128 * 1024, "level"));
        for (int i = 0; i < 60; i++)
            plan.Add(($"data/archive{lv.Next(Archives):00}.pak", (long)(lv.NextDouble() * (ArchiveSize - 1024 * 1024)), 64 * 1024, "level"));
        return plan;
    }
}

internal sealed class LoaderForm : Form
{
    private readonly string _root;
    private readonly int _level;
    private readonly bool _autoExit;
    private readonly string _results;
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Bottom, Height = 8, Maximum = 1000 };
    private readonly Label _label = new() { Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 14), TextAlign = ContentAlignment.MiddleCenter };

    public int ExitCode { get; private set; }

    public LoaderForm(string root, int level, bool autoExit, string results)
    {
        _root = root;
        _level = level;
        _autoExit = autoExit;
        _results = results;
        Text = "CloudSSD Demo Game";
        BackColor = Color.FromArgb(12, 14, 18);
        Size = new Size(720, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_label);
        Controls.Add(_bar);
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var plan = Program.Plan(_level);
        var sw = Stopwatch.StartNew();
        var phases = new Dictionary<string, double>();
        long bytes = 0;
        double worst = 0;
        string? error = null;
        try
        {
            await Task.Run(() =>
            {
                var handles = new Dictionary<string, FileStream>();
                try
                {
                    var buf = new byte[512 * 1024];
                    for (int i = 0; i < plan.Count; i++)
                    {
                        var (file, off, len, phase) = plan[i];
                        if (!handles.TryGetValue(file, out var fs))
                            handles[file] = fs = new FileStream(Path.Combine(_root, file.Replace('/', '\\')), FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
                        var t0 = sw.Elapsed.TotalMilliseconds;
                        fs.Seek(off, SeekOrigin.Begin);
                        int read = 0;
                        while (read < len)
                        {
                            var n = fs.Read(buf, 0, Math.Min(buf.Length, len - read));
                            if (n == 0) break;
                            read += n;
                        }
                        var dt = sw.Elapsed.TotalMilliseconds - t0;
                        worst = Math.Max(worst, dt);
                        phases[phase] = phases.GetValueOrDefault(phase) + dt;
                        bytes += read;
                        if (i % 8 == 0)
                        {
                            var p = i;
                            BeginInvoke(() =>
                            {
                                _bar.Value = (int)(1000L * p / plan.Count);
                                _label.Text = $"Loading level {_level}…  {100.0 * p / plan.Count:0}%";
                            });
                        }
                    }
                }
                finally
                {
                    foreach (var h in handles.Values) h.Dispose();
                }
            });
        }
        catch (IOException e)
        {
            error = e.Message;
            ExitCode = 3;
        }
        var total = sw.Elapsed.TotalMilliseconds;
        _bar.Value = 1000;
        _label.Text = error is null
            ? $"Level {_level} loaded in {total / 1000:0.00} s\n{bytes / 1048576.0:0} MiB read · longest wait {worst:0} ms"
            : $"Loading failed: {error}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_results)!);
            File.AppendAllText(_results, JsonSerializer.Serialize(new
            {
                time = DateTime.Now, root = _root, level = _level, loadMs = total, bytes, reads = plan.Count, worstReadMs = worst, phases, error
            }) + Environment.NewLine);
        }
        catch (IOException) { }
        if (_autoExit)
        {
            await Task.Delay(1500);
            Close();
        }
    }
}
