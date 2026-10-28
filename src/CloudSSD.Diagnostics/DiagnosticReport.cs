using System.IO.Compression;
using System.Text;

namespace CloudSSD.Diagnostics;

/// <summary>
/// Builds a sanitized ZIP for support. Every text entry passes through <see cref="Redactor"/>; the rclone config
/// file and DPAPI key are never included.
/// </summary>
public static class DiagnosticReport
{
    public static string Create(string outputZip, IReadOnlyDictionary<string, string> sections, IEnumerable<string> logFiles, int maxLinesPerLog = 4000)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputZip)!);
        if (File.Exists(outputZip)) File.Delete(outputZip);
        using var zip = ZipFile.Open(outputZip, ZipArchiveMode.Create);
        foreach (var (name, content) in sections)
            AddText(zip, name, Redactor.Redact(content));

        foreach (var log in logFiles)
        {
            if (!File.Exists(log)) continue;
            var name = Path.GetFileName(log);
            if (name.Contains("rclone.conf", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase)) continue;
            var lines = ReadTail(log, maxLinesPerLog);
            AddText(zip, "logs/" + name, Redactor.Redact(string.Join(Environment.NewLine, lines)));
        }
        return outputZip;
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    public static List<string> ReadTail(string path, int maxLines)
    {
        var q = new Queue<string>(maxLines);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        while (r.ReadLine() is { } line)
        {
            if (q.Count == maxLines) q.Dequeue();
            q.Enqueue(line);
        }
        return q.ToList();
    }
}
