using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CloudSSD.Diagnostics;

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Category, string Message, string? Exception);

/// <summary>In-memory ring of recent log entries for the Diagnostics page.</summary>
public sealed class MemoryLogSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _capacity;
    private long _errorCount;
    private long _warningCount;

    public MemoryLogSink(int capacity = 3000) => _capacity = capacity;

    public event Action<LogEntry>? EntryAdded;

    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public long WarningCount => Interlocked.Read(ref _warningCount);

    public void Add(LogEntry e)
    {
        _entries.Enqueue(e);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _)) { }
        if (e.Level >= LogLevel.Error) Interlocked.Increment(ref _errorCount);
        else if (e.Level == LogLevel.Warning) Interlocked.Increment(ref _warningCount);
        EntryAdded?.Invoke(e);
    }

    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();
}

/// <summary>
/// Logger provider writing redacted lines to a daily log file and to an in-memory sink. File writes are done on a
/// background thread so logging never blocks the UI or I/O paths.
/// </summary>
public sealed class AppLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly MemoryLogSink _sink;
    private readonly LogLevel _minLevel;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10_000);
    private readonly Thread _writer;

    private readonly string _prefix;

    public AppLoggerProvider(string directory, MemoryLogSink sink, LogLevel minLevel = LogLevel.Information, string filePrefix = "cloudssd")
    {
        _directory = directory;
        _sink = sink;
        _minLevel = minLevel;
        _prefix = filePrefix;
        Directory.CreateDirectory(directory);
        PruneOldLogs();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "cloudssd-cli-log-writer" };
        _writer.Start();
    }

    public string CurrentLogFile => Path.Combine(_directory, $"{_prefix}-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new AppLogger(this, categoryName);

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        if (level < _minLevel) return;
        var safeMsg = Redactor.Redact(message);
        var safeEx = ex is null ? null : Redactor.Redact(ex.ToString());
        var entry = new LogEntry(DateTime.Now, level, ShortCategory(category), safeMsg, safeEx);
        _sink.Add(entry);
        var line = new StringBuilder()
            .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
            .Append(LevelTag(level)).Append(' ')
            .Append(entry.Category).Append(": ").Append(safeMsg);
        if (safeEx != null) line.AppendLine().Append(safeEx);
        _queue.TryAdd(line.ToString());
    }

    private static string ShortCategory(string c)
    {
        var i = c.LastIndexOf('.');
        return i >= 0 ? c[(i + 1)..] : c;
    }

    private static string LevelTag(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(CurrentLogFile, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Log file temporarily locked (e.g. antivirus); drop the line rather than block.
            }
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_directory, _prefix + "-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class AppLogger(AppLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
