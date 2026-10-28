using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CloudSSD.Games;

/// <summary>
/// Notices a library game running from its cloud drive however it was started. Store-launched games (Steam, Rockstar
/// Games Launcher, Epic) restart themselves through their launcher, so the process CloudSSD starts exits within
/// seconds while the real game runs as the launcher's child. The watcher instead looks at every running process and
/// reports a game as running while any process whose image lives under the game's folder is alive, and as ended after
/// <see cref="Grace"/> without one (launchers briefly hand over between processes).
/// </summary>
public sealed class GameProcessWatcher : IDisposable
{
    public sealed record Watched(string GameId, string Folder);

    private readonly Func<IReadOnlyList<Watched>> _games;
    private readonly Action<string, string> _started;   // game id, executable
    private readonly Action<string> _ended;              // game id
    private readonly Func<IEnumerable<string>> _imagePaths;
    private readonly Dictionary<string, DateTime> _lastSeen = new(StringComparer.Ordinal);
    private readonly Timer? _timer;
    private int _ticking;

    public GameProcessWatcher(Func<IReadOnlyList<Watched>> games, Action<string, string> started, Action<string> ended,
        TimeSpan? interval = null, Func<IEnumerable<string>>? imagePaths = null, bool autoStart = true)
    {
        _games = games;
        _started = started;
        _ended = ended;
        _imagePaths = imagePaths ?? RunningImagePaths;
        if (autoStart)
        {
            var i = interval ?? TimeSpan.FromSeconds(3);
            _timer = new Timer(_ => Tick(DateTime.UtcNow), null, i, i);
        }
    }

    public TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(15);

    public IReadOnlyCollection<string> Running { get { lock (_lastSeen) return _lastSeen.Keys.ToList(); } }

    public void Tick(DateTime now)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var games = _games();
            if (games.Count == 0 && _lastSeen.Count == 0) return;
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in _imagePaths())
                foreach (var g in games)
                    if (!seen.ContainsKey(g.GameId) && IsUnder(path, g.Folder)) seen[g.GameId] = path;
            lock (_lastSeen)
            {
                foreach (var (id, exe) in seen)
                {
                    if (!_lastSeen.ContainsKey(id)) _started(id, exe);
                    _lastSeen[id] = now;
                }
                foreach (var id in _lastSeen.Where(kv => !seen.ContainsKey(kv.Key) && now - kv.Value >= Grace).Select(kv => kv.Key).ToList())
                {
                    _lastSeen.Remove(id);
                    _ended(id);
                }
            }
        }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    public static bool IsUnder(string path, string folder)
    {
        var f = folder.TrimEnd('\\', '/');
        return f.Length > 0 && path.Length > f.Length && path.StartsWith(f, StringComparison.OrdinalIgnoreCase)
               && path[f.Length] is '\\' or '/';
    }

    // ------------------------------------------------------------------ process images

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Full image paths of the processes this user can query (limited-information access works for most).</summary>
    public static IEnumerable<string> RunningImagePaths()
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var h = OpenProcess(ProcessQueryLimitedInformation, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new StringBuilder(1024);
                    var size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) yield return sb.ToString(0, size);
                }
                finally { CloseHandle(h); }
            }
        }
    }

    public void Dispose() => _timer?.Dispose();
}
