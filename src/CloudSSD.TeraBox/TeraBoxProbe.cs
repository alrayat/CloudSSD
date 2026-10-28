using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudSSD.Core;
using Microsoft.Extensions.Logging;

namespace CloudSSD.TeraBox;

public enum ProbeOutcome { Pass, Fail, Info, Skipped }

public sealed record ProbeCheck(string Name, ProbeOutcome Outcome, string Detail, double Ms);

public sealed record LatencyStats(int Concurrency, int Samples, double P50Ms, double P95Ms, double P99Ms, double MaxMs, double MBps);

/// <summary>Redacted result of a capability probe: no cookies, tokens, signed URLs or file names outside the probe folder.</summary>
public sealed class ProbeReport
{
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Finished { get; set; }
    public string Transport { get; set; } = "";
    public string? Account { get; set; }
    public string? Plan { get; set; }
    public long? QuotaTotal { get; set; }
    public long? QuotaUsed { get; set; }
    public long? PlanFileLimit { get; set; }
    public string ProbeFolder { get; set; } = "";
    public List<ProbeCheck> Checks { get; } = new();
    public List<LatencyStats> RandomReadLatency { get; } = new();
    public double? SequentialDownloadMBps { get; set; }
    public double? UploadMBps { get; set; }
    public long ApiCalls { get; set; }
    public long Throttled { get; set; }
    public int Failures => Checks.Count(c => c.Outcome == ProbeOutcome.Fail);

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# TeraBox capability probe — {Started:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"- Transport: **{Transport}**; account: {Account ?? "?"}; plan: {Plan ?? "?"}");
        if (QuotaTotal is { } t) sb.AppendLine($"- Quota: {ByteSize.Format(QuotaUsed ?? 0)} used of {ByteSize.Format(t)} ({ByteSize.Format(t - (QuotaUsed ?? 0))} free)");
        sb.AppendLine($"- Per-file limit applied by the client: {(PlanFileLimit is { } l ? ByteSize.Format(l) : "unknown")}");
        sb.AppendLine($"- Sequential download: {(SequentialDownloadMBps is { } d ? $"{d:0.00} MB/s ({d * 8:0.0} Mbps)" : "not measured")}; upload: {(UploadMBps is { } u ? $"{u:0.00} MB/s" : "not measured")}");
        sb.AppendLine($"- API calls: {ApiCalls}, throttled responses: {Throttled}, duration: {(Finished - Started).TotalSeconds:0} s, failures: {Failures}");
        sb.AppendLine();
        sb.AppendLine("| Check | Result | Detail | ms |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in Checks) sb.AppendLine($"| {c.Name} | {c.Outcome} | {c.Detail.Replace("|", "/")} | {c.Ms:0} |");
        if (RandomReadLatency.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Random 64 KiB reads (time to the last byte):");
            sb.AppendLine();
            sb.AppendLine("| Concurrency | Samples | p50 ms | p95 ms | p99 ms | max ms | MB/s |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var s in RandomReadLatency) sb.AppendLine($"| {s.Concurrency} | {s.Samples} | {s.P50Ms:0} | {s.P95Ms:0} | {s.P99Ms:0} | {s.MaxMs:0} | {s.MBps:0.00} |");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Measures what a TeraBox connection really does (Gate A), in a dedicated folder with deterministic synthetic files, and
/// removes only that folder afterwards. Every ranged read is validated by status, Content-Range, length and the bytes
/// themselves; Accept-Ranges is recorded but never trusted on its own.
/// </summary>
public sealed class TeraBoxProbe(ITeraBoxTransport api, string workDir, ILogger log)
{
    public int RandomReads { get; init; } = 24;
    public bool Pagination { get; init; }
    public long MediumSize { get; init; } = 9L * 1024 * 1024 + 12_345; // crosses two 4 MiB shard boundaries

    private readonly ProbeReport _r = new();
    private string _folder = "";

    private static byte[] Deterministic(long size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    private async Task<T?> Check<T>(string name, Func<Task<(ProbeOutcome, string, T?)>> body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (o, d, v) = await body();
            _r.Checks.Add(new ProbeCheck(name, o, d, sw.Elapsed.TotalMilliseconds));
            log.LogInformation("probe {Name}: {Outcome} {Detail}", name, o, d);
            return v;
        }
        catch (Exception e)
        {
            var msg = Diagnostics.Redactor.Redact(e is TeraBoxException te ? $"TeraBox error {te.Errno}: {te.Message}" : $"{e.GetType().Name}: {e.Message}");
            _r.Checks.Add(new ProbeCheck(name, ProbeOutcome.Fail, msg, sw.Elapsed.TotalMilliseconds));
            log.LogWarning("probe {Name}: FAIL {Detail}", name, msg);
            return default;
        }
    }

    private Task Check(string name, Func<Task<(ProbeOutcome, string)>> body) =>
        Check<object>(name, async () => { var (o, d) = await body(); return (o, d, null); });

    private async Task<string> UploadBytes(string rel, byte[] data, bool overwrite = true)
    {
        var local = Path.Combine(workDir, Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(local, data);
        try { await api.UploadAsync(local, Abs(rel), overwrite, null, CancellationToken.None); }
        finally { File.Delete(local); }
        return Abs(rel);
    }

    private string Abs(string rel) => _folder + "/" + rel;

    private static string Sha(byte[] d) => Convert.ToHexString(SHA256.HashData(d));

    /// <summary>Downloads [from, to] through the transport and validates everything about the answer.</summary>
    private async Task<(bool Ok, string Detail, byte[]? Bytes, double Ms)> RangedRead(TeraBoxEntry e, byte[] expected, long from, long to)
    {
        var sw = Stopwatch.StartNew();
        var url = await api.DownloadUrlAsync(e, CancellationToken.None);
        using var resp = await api.OpenDownloadAsync(url, from, to, CancellationToken.None);
        var body = await resp.Content.ReadAsByteArrayAsync();
        var ms = sw.Elapsed.TotalMilliseconds;
        var cr = resp.Content.Headers.ContentRange;
        var acceptRanges = string.Join(",", resp.Headers.AcceptRanges);
        if (resp.StatusCode == HttpStatusCode.OK)
            return (false, $"server ignored Range: HTTP 200 with {body.Length} bytes (Accept-Ranges: '{acceptRanges}')", null, ms);
        if (resp.StatusCode != HttpStatusCode.PartialContent) return (false, $"HTTP {(int)resp.StatusCode}", null, ms);
        var want = expected.AsSpan((int)from, (int)(to - from + 1)).ToArray();
        var problems = new List<string>();
        if (cr is not { HasRange: true }) problems.Add("no Content-Range");
        else
        {
            if (cr.From != from || cr.To != to) problems.Add($"Content-Range {cr.From}-{cr.To} for request {from}-{to}");
            if (cr.HasLength && cr.Length != expected.Length) problems.Add($"total {cr.Length} != {expected.Length}");
        }
        if (resp.Content.Headers.ContentLength is { } cl && cl != want.Length) problems.Add($"Content-Length {cl} != {want.Length}");
        if (body.Length != want.Length) problems.Add($"{body.Length} bytes received, {want.Length} expected");
        else if (!body.AsSpan().SequenceEqual(want)) problems.Add("bytes differ from the uploaded content");
        return (problems.Count == 0, problems.Count == 0 ? $"206, Content-Range {cr!.From}-{cr.To}/{(cr.HasLength ? cr.Length : "*")}, bytes match" : string.Join("; ", problems), body, ms);
    }

    public async Task<ProbeReport> RunAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        _r.Transport = api.Kind;
        _folder = api.Abs(".cloudssd-cli-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _r.ProbeFolder = _folder;

        await Check("account", async () =>
        {
            var u = await api.UserAsync(ct);
            _r.Account = u.Name;
            _r.Plan = u.Plan;
            _r.PlanFileLimit = api.MaxFileSize;
            return (ProbeOutcome.Info, $"{u.Plan}; per-file limit used by the client: {(api.MaxFileSize is { } m ? ByteSize.Format(m) : "unknown")}");
        });
        await Check("quota", async () =>
        {
            var q = await api.QuotaAsync(ct);
            _r.QuotaTotal = q.Total;
            _r.QuotaUsed = q.Used;
            return (q.Total > 0 ? ProbeOutcome.Pass : ProbeOutcome.Fail, $"{ByteSize.Format(q.Used)} used of {ByteSize.Format(q.Total)}");
        });
        await Check("create probe folder", async () =>
        {
            await api.MkdirAsync(api.AppRoot, ct);
            await api.MkdirAsync(_folder, ct);
            return (await api.StatAsync(_folder, ct) is { IsDir: true } ? ProbeOutcome.Pass : ProbeOutcome.Fail, "dedicated folder for synthetic files");
        });

        var small = Deterministic(1024, 1);
        var medium = Deterministic(MediumSize, 2);
        TeraBoxEntry? mediumEntry = null;

        await Check("upload small (1 KiB)", async () =>
        {
            await UploadBytes("small.bin", small);
            var e = await api.StatAsync(Abs("small.bin"), ct);
            return (e?.Size == small.Length ? ProbeOutcome.Pass : ProbeOutcome.Fail, $"stored size {e?.Size}");
        });
        await Check("download small (link and hosts)", async () =>
        {
            var e = await api.StatAsync(Abs("small.bin"), ct) ?? throw new IOException("small.bin missing");
            var url = await api.DownloadUrlAsync(e, ct);
            using var resp = await api.OpenDownloadAsync(url, null, null, ct);
            var body = await resp.Content.ReadAsByteArrayAsync(ct);
            var ok = resp.StatusCode == HttpStatusCode.OK && Sha(body) == Sha(small);
            return (ok ? ProbeOutcome.Pass : ProbeOutcome.Fail,
                $"HTTP {(int)resp.StatusCode}, {body.Length} bytes{(ok ? ", content matches" : "")}; link host {SafeHost(url)}, final host {SafeHost(resp.RequestMessage?.RequestUri?.ToString() ?? "")}");
        });
        mediumEntry = await Check("upload multi-shard (" + ByteSize.Format(MediumSize) + ")", async () =>
        {
            var sw = Stopwatch.StartNew();
            await UploadBytes("medium.bin", medium);
            _r.UploadMBps = medium.Length / 1e6 / sw.Elapsed.TotalSeconds;
            var e = await api.StatAsync(Abs("medium.bin"), ct);
            var realMd5 = Convert.ToHexString(MD5.HashData(medium)).ToLowerInvariant();
            var md5Kind = e?.Md5 is null ? "absent" : string.Equals(e.Md5, realMd5, StringComparison.OrdinalIgnoreCase) ? "plain MD5 of the file"
                : TeraBoxWebApi.DecodeMd5(e.Md5).Equals(realMd5, StringComparison.OrdinalIgnoreCase) ? "scrambled MD5 of the file" : "not the file's MD5 (block-list hash?)";
            return (e?.Size == medium.Length ? ProbeOutcome.Pass : ProbeOutcome.Fail, $"stored size {e?.Size}, {_r.UploadMBps:0.00} MB/s; listing md5 field: {md5Kind}; fs_id present: {e?.FsId > 0}", e);
        });

        await Check("listing metadata", async () =>
        {
            var list = await api.ListAsync(_folder, ct);
            var names = string.Join(", ", list.Select(x => x.Name).OrderBy(x => x));
            var ok = list.Any(x => x.Name == "small.bin" && x.Size == 1024) && list.Any(x => x.Name == "medium.bin" && x.Size == medium.Length);
            var m = list.FirstOrDefault(x => x.Name == "medium.bin");
            return (ok ? ProbeOutcome.Pass : ProbeOutcome.Fail, $"entries: {names}; mtime {m?.Modified:u}");
        });

        if (mediumEntry != null)
        {
            await Check("full download hash", async () =>
            {
                var sw = Stopwatch.StartNew();
                var url = await api.DownloadUrlAsync(mediumEntry, ct);
                using var resp = await api.OpenDownloadAsync(url, null, null, ct);
                var body = await resp.Content.ReadAsByteArrayAsync(ct);
                _r.SequentialDownloadMBps = body.Length / 1e6 / sw.Elapsed.TotalSeconds;
                var host = SafeHost(url);
                return (resp.StatusCode == HttpStatusCode.OK && Sha(body) == Sha(medium) ? ProbeOutcome.Pass : ProbeOutcome.Fail,
                    $"HTTP {(int)resp.StatusCode}, {body.Length} bytes, SHA-256 {(Sha(body) == Sha(medium) ? "matches" : "DIFFERS")}, {_r.SequentialDownloadMBps:0.00} MB/s, link host {host}, Accept-Ranges '{string.Join(",", resp.Headers.AcceptRanges)}'");
            });

            var shard = 4L * 1024 * 1024;
            foreach (var (label, from, len) in new[]
                     {
                         ("first byte", 0L, 1L), ("across shard boundary", shard - 100, 200L), ("middle 1 MiB", MediumSize / 2, 1024L * 1024),
                         ("last byte", MediumSize - 1, 1L), ("tail 64 KiB", MediumSize - 65536, 65536L)
                     })
            {
                await Check($"range: {label}", async () =>
                {
                    var (ok, detail, _, _) = await RangedRead(mediumEntry, medium, from, from + len - 1);
                    return (ok ? ProbeOutcome.Pass : ProbeOutcome.Fail, detail);
                });
            }

            await Check("range beyond end of file", async () =>
            {
                var url = await api.DownloadUrlAsync(mediumEntry, ct);
                using var resp = await api.OpenDownloadAsync(url, MediumSize + 10, MediumSize + 20, ct);
                var body = await resp.Content.ReadAsByteArrayAsync(ct);
                var code = (int)resp.StatusCode;
                return (code == 416 ? ProbeOutcome.Pass : ProbeOutcome.Info, $"HTTP {code}, {body.Length} bytes" + (code == 416 ? "" : " (416 expected; the bridge answers 416 itself from the known size)"));
            });

            await Check("download link reuse", async () =>
            {
                var url = await api.DownloadUrlAsync(mediumEntry, ct);
                using var a = await api.OpenDownloadAsync(url, 0, 99, ct);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                using var b = await api.OpenDownloadAsync(url, 100, 199, ct);
                return (ProbeOutcome.Info, $"same link used twice 5 s apart: HTTP {(int)a.StatusCode} then {(int)b.StatusCode}");
            });

            foreach (var conc in new[] { 1, 2, 4 })
            {
                await Check($"random 64 KiB reads x{RandomReads} (concurrency {conc})", async () =>
                {
                    var rnd = new Random(conc);
                    var offsets = Enumerable.Range(0, RandomReads).Select(_ => (long)(rnd.NextDouble() * (MediumSize - 65536))).ToList();
                    var times = new List<double>();
                    var bad = 0;
                    var sw = Stopwatch.StartNew();
                    await Parallel.ForEachAsync(offsets, new ParallelOptions { MaxDegreeOfParallelism = conc, CancellationToken = ct }, async (off, _) =>
                    {
                        var (ok, _, _, ms) = await RangedRead(mediumEntry, medium, off, off + 65535);
                        lock (times) { times.Add(ms); if (!ok) bad++; }
                    });
                    times.Sort();
                    double P(double q) => times[Math.Min(times.Count - 1, (int)Math.Ceiling(q * times.Count) - 1)];
                    var stats = new LatencyStats(conc, times.Count, P(0.5), P(0.95), P(0.99), times[^1], RandomReads * 65536 / 1e6 / sw.Elapsed.TotalSeconds);
                    _r.RandomReadLatency.Add(stats);
                    return (bad == 0 ? ProbeOutcome.Pass : ProbeOutcome.Fail, $"p50 {stats.P50Ms:0} ms, p95 {stats.P95Ms:0} ms, p99 {stats.P99Ms:0} ms, {bad} invalid");
                });
            }

            await Check("object changed during a read", async () =>
            {
                var url = await api.DownloadUrlAsync(mediumEntry, ct);
                using var resp = await api.OpenDownloadAsync(url, null, null, ct);
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                var first = new byte[1024 * 1024];
                var got = 0;
                while (got < first.Length) { var n = await s.ReadAsync(first.AsMemory(got), ct); if (n == 0) break; got += n; }
                var replacement = Deterministic(MediumSize, 3);
                await UploadBytes("medium.bin", replacement);
                using var rest = new MemoryStream();
                string outcome;
                try { await s.CopyToAsync(rest, ct); outcome = "stream continued"; }
                catch (Exception e) when (e is IOException or HttpRequestException) { outcome = "stream failed: " + e.GetType().Name; }
                var all = first[..got].Concat(rest.ToArray()).ToArray();
                var which = all.Length == medium.Length && Sha(all) == Sha(medium) ? "old version, complete"
                    : all.Length == replacement.Length && Sha(all) == Sha(replacement) ? "new version" : $"{all.Length} bytes matching neither version completely";
                var after = await api.StatAsync(Abs("medium.bin"), ct);
                var old = await RangedRead(mediumEntry with { FsId = after?.FsId ?? mediumEntry.FsId }, replacement, 0, 1023);
                return (ProbeOutcome.Info, $"{outcome}; data seen: {which}; fs_id after overwrite {(after?.FsId == mediumEntry.FsId ? "unchanged" : "changed")}; new link serves new bytes: {old.Ok}");
            });
        }

        await Check("overwrite", async () =>
        {
            var v2 = Deterministic(2048, 4);
            await UploadBytes("small.bin", v2, overwrite: true);
            var e = await api.StatAsync(Abs("small.bin"), ct);
            var (ok, detail, _, _) = e is null ? (false, "missing", null, 0) : await RangedRead(e, v2, 0, v2.Length - 1);
            return (e?.Size == 2048 && ok ? ProbeOutcome.Pass : ProbeOutcome.Fail, $"size {e?.Size}; {detail}");
        });
        await Check("rename / move / copy / delete", async () =>
        {
            await api.MkdirAsync(Abs("sub"), ct);
            await api.RenameAsync(Abs("small.bin"), "renamed.bin", ct);
            await api.MoveAsync(Abs("renamed.bin"), Abs("sub"), "moved.bin", ct);
            await api.CopyAsync(Abs("sub/moved.bin"), _folder, "copy.bin", ct);
            await api.DeleteAsync(Abs("sub/moved.bin"), ct);
            var ok = await api.StatAsync(Abs("copy.bin"), ct) is { Size: 2048 } && await api.StatAsync(Abs("sub/moved.bin"), ct) is null
                     && await api.StatAsync(Abs("renamed.bin"), ct) is null;
            return (ok ? ProbeOutcome.Pass : ProbeOutcome.Fail, "renamed, moved into a sub-folder, copied back, original deleted");
        });
        await Check("missing object", async () =>
        {
            var e = await api.StatAsync(Abs("does-not-exist.bin"), ct);
            return (e is null ? ProbeOutcome.Pass : ProbeOutcome.Fail, e is null ? "reported as not found" : "unexpectedly found");
        });
        await Check("empty file", async () =>
        {
            try
            {
                await UploadBytes("empty.bin", []);
                var e = await api.StatAsync(Abs("empty.bin"), ct);
                return (ProbeOutcome.Info, $"accepted; stored size {e?.Size}");
            }
            catch (TeraBoxException e) { return (ProbeOutcome.Info, $"refused (error {e.Errno}): {e.Message}"); }
        });
        await Check("unicode and spaces in names", async () =>
        {
            var data = Encoding.UTF8.GetBytes("ñandú");
            await UploadBytes("Spaced ñandú (1).txt", data);
            var e = await api.StatAsync(Abs("Spaced ñandú (1).txt"), ct);
            return (e?.Size == data.Length ? ProbeOutcome.Pass : ProbeOutcome.Fail, $"stored size {e?.Size}");
        });
        if (Pagination)
        {
            await Check("pagination (105 entries)", async () =>
            {
                await api.MkdirAsync(Abs("many"), ct);
                for (int i = 0; i < 105; i++) await UploadBytes($"many/f{i:000}.txt", Encoding.ASCII.GetBytes(i.ToString(CultureInfo.InvariantCulture)));
                var list = await api.ListAsync(Abs("many"), ct);
                return (list.Count == 105 && list.Select(x => x.Name).Distinct().Count() == 105 ? ProbeOutcome.Pass : ProbeOutcome.Fail, $"{list.Count} entries listed");
            });
        }
        else _r.Checks.Add(new ProbeCheck("pagination", ProbeOutcome.Skipped, "run with --pagination (uploads 105 tiny files)", 0));
        _r.Checks.Add(new ProbeCheck("offsets beyond 4 GiB", ProbeOutcome.Skipped,
            "needs a > 4 GiB object; a free plan cannot store one natively. Covered through chunked storage (large-file milestone).", 0));

        await Check("cleanup (probe folder only)", async () =>
        {
            await api.DeleteAsync(_folder, ct);
            return (await api.StatAsync(_folder, ct) is null ? ProbeOutcome.Pass : ProbeOutcome.Fail, "probe folder deleted (it may stay in TeraBox's recycle bin)");
        });
        _r.ApiCalls = api.Calls;
        _r.Throttled = api.Throttled;
        _r.Finished = DateTimeOffset.Now;
        return _r;
    }

    /// <summary>
    /// Large-read throughput for planning chunked storage and preparation: uploads one deterministic file, then downloads
    /// all of it (a) as one stream and (b) as fixed-size ranges fetched with N parallel requests, for several N and range
    /// sizes. Every reassembled copy is hash-checked. Removes its folder afterwards.
    /// </summary>
    public async Task<ProbeReport> ThroughputAsync(int sizeMiB, int[] concurrencies, int[] rangeMiBs, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        _r.Transport = api.Kind;
        _folder = api.Abs(".cloudssd-cli-probe-tp-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _r.ProbeFolder = _folder;
        var data = Deterministic((long)sizeMiB * 1024 * 1024, 9);
        var sha = Sha(data);
        await api.MkdirAsync(api.AppRoot, ct);
        await api.MkdirAsync(_folder, ct);
        var entry = await Check($"upload {sizeMiB} MiB", async () =>
        {
            var sw = Stopwatch.StartNew();
            await UploadBytes("big.bin", data);
            _r.UploadMBps = data.Length / 1e6 / sw.Elapsed.TotalSeconds;
            var e = await api.StatAsync(Abs("big.bin"), ct);
            return (e?.Size == data.Length ? ProbeOutcome.Pass : ProbeOutcome.Fail, $"{_r.UploadMBps:0.00} MB/s", e);
        });
        if (entry != null)
        {
            await Check("1 stream, whole file (resumed on cuts)", async () =>
            {
                var buf = new byte[data.Length];
                var st = new FetchStats();
                var sw = Stopwatch.StartNew();
                await FetchResumingAsync(entry, 0, data.Length - 1, buf, st, firstWithoutRange: true, ct);
                var mbps = data.Length / 1e6 / sw.Elapsed.TotalSeconds;
                _r.SequentialDownloadMBps = mbps;
                var ok = Sha(buf) == sha;
                return (ok && st.Failed == 0 ? ProbeOutcome.Pass : ProbeOutcome.Fail,
                    $"{mbps:0.00} MB/s ({mbps * 8:0.0} Mbps) effective; {st.Describe()}; connections: {string.Join("; ", st.Connections.Take(12))}; hash {(ok ? "ok" : "DIFFERS")}");
            });
            foreach (var rangeMiB in rangeMiBs)
            foreach (var conc in concurrencies)
            {
                await Check($"{conc} parallel x {rangeMiB} MiB ranges", async () =>
                {
                    var chunk = (long)rangeMiB * 1024 * 1024;
                    var buf = new byte[data.Length];
                    var offsets = Enumerable.Range(0, (int)((data.Length + chunk - 1) / chunk)).Select(i => i * chunk).ToList();
                    var st = new FetchStats();
                    var sw = Stopwatch.StartNew();
                    await Parallel.ForEachAsync(offsets, new ParallelOptions { MaxDegreeOfParallelism = conc, CancellationToken = ct }, async (off, c) =>
                        await FetchResumingAsync(entry, off, Math.Min(data.Length, off + chunk) - 1, buf, st, firstWithoutRange: false, c));
                    var mbps = data.Length / 1e6 / sw.Elapsed.TotalSeconds;
                    var ok = Sha(buf) == sha;
                    return (ok && st.Failed == 0 ? ProbeOutcome.Pass : ProbeOutcome.Fail, $"{mbps:0.00} MB/s ({mbps * 8:0.0} Mbps) effective; {st.Describe()}; hash {(ok ? "ok" : "DIFFERS")}");
                });
            }
        }
        await Check("cleanup (probe folder only)", async () =>
        {
            await api.DeleteAsync(_folder, ct);
            return (await api.StatAsync(_folder, ct) is null ? ProbeOutcome.Pass : ProbeOutcome.Fail, "probe folder deleted");
        });
        _r.ApiCalls = api.Calls;
        _r.Throttled = api.Throttled;
        _r.Finished = DateTimeOffset.Now;
        return _r;
    }


    /// <summary>Per-check transfer statistics: requests, connections cut or stalled, longest healthy stream, errors seen.</summary>
    private sealed class FetchStats
    {
        public int Requests, Cuts, Stalls, Slowed, Failed;
        public readonly List<string> Connections = new();
        public long LongestBytesBeforeCut;
        public double LongestSecondsBeforeCut;
        public readonly HashSet<string> Errors = new();
        public string Describe() =>
            $"{Requests} requests, {Cuts} cut, {Stalls} stalled (no data for {StallSeconds} s), {Slowed} slowed below {SlowFloorKBps} KB/s, {Failed} gave up" +
            (Cuts + Stalls > 0 ? $"; cut after up to {LongestBytesBeforeCut / 1e6:0.0} MB / {LongestSecondsBeforeCut:0} s" : "") +
            (Errors.Count > 0 ? "; errors: " + string.Join(" | ", Errors.Take(3)) : "");
    }

    private const int StallSeconds = 20;
    /// <summary>A connection delivering less than this over <see cref="SlowWindowSeconds"/> is treated as throttled and replaced.</summary>
    private const int SlowFloorKBps = 32, SlowWindowSeconds = 15;

    private sealed class SlowConnectionException(double bytesPerSecond) : Exception("connection slowed down")
    {
        public double BytesPerSecond { get; } = bytesPerSecond;
    }

    private static string Innermost(Exception e)
    {
        while (e.InnerException != null) e = e.InnerException;
        return Diagnostics.Redactor.Redact($"{e.GetType().Name}: {e.Message}");
    }

    /// <summary>
    /// Downloads [from, to] into buf, resuming from the exact offset reached after a cut connection or a stall (no data for
    /// <see cref="StallSeconds"/> s), with a fresh link on the next attempt; at most 6 attempts. Every response is checked
    /// (206 + Content-Range start) before its bytes are used.
    /// </summary>
    private async Task FetchResumingAsync(TeraBoxEntry e, long from, long to, byte[] buf, FetchStats st, bool firstWithoutRange, CancellationToken ct)
    {
        var pos = from;
        for (int attempt = 0; pos <= to; attempt++)
        {
            if (attempt >= 12) { Interlocked.Increment(ref st.Failed); return; }
            Interlocked.Increment(ref st.Requests);
            var sw = Stopwatch.StartNew();
            long got = 0;
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(TimeSpan.FromSeconds(StallSeconds));
            try
            {
                var url = await api.DownloadUrlAsync(e, stall.Token);
                var whole = firstWithoutRange && attempt == 0;
                using var resp = await api.OpenDownloadAsync(url, whole ? null : pos, whole ? null : to, stall.Token);
                if (whole ? resp.StatusCode != HttpStatusCode.OK : resp.StatusCode != HttpStatusCode.PartialContent || resp.Content.Headers.ContentRange?.From != pos)
                {
                    lock (st) st.Errors.Add($"HTTP {(int)resp.StatusCode}");
                    await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct);
                    continue;
                }
                await using var stream = await resp.Content.ReadAsStreamAsync(stall.Token);
                var window = Stopwatch.StartNew();
                long windowBytes = 0;
                while (pos <= to)
                {
                    var n = await stream.ReadAsync(buf.AsMemory((int)pos, (int)Math.Min(256 * 1024, to - pos + 1)), stall.Token);
                    if (n == 0) throw new EndOfStreamException("connection closed before the end of the range");
                    pos += n;
                    got += n;
                    windowBytes += n;
                    stall.CancelAfter(TimeSpan.FromSeconds(StallSeconds)); // data arrived: reset the stall timer
                    if (window.Elapsed.TotalSeconds >= SlowWindowSeconds)
                    {
                        if (windowBytes < SlowFloorKBps * 1024L * SlowWindowSeconds) throw new SlowConnectionException(windowBytes / window.Elapsed.TotalSeconds);
                        window.Restart();
                        windowBytes = 0;
                    }
                }
                lock (st) st.Connections.Add($"{got / 1e6:0.0} MB in {sw.Elapsed.TotalSeconds:0} s (complete)");
            }
            catch (SlowConnectionException ex)
            {
                lock (st)
                {
                    st.Slowed++;
                    st.Connections.Add($"{got / 1e6:0.0} MB in {sw.Elapsed.TotalSeconds:0} s, then {ex.BytesPerSecond / 1024:0} KB/s");
                    if (got > st.LongestBytesBeforeCut) { st.LongestBytesBeforeCut = got; st.LongestSecondsBeforeCut = sw.Elapsed.TotalSeconds; }
                }
                log.LogInformation("probe transfer at offset {Pos} slowed to {Rate:0} KB/s after {Bytes} bytes / {Sec:0.0} s; reconnecting", pos, ex.BytesPerSecond / 1024, got, sw.Elapsed.TotalSeconds);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                var stalled = ex is OperationCanceledException;
                lock (st)
                {
                    st.Connections.Add($"{got / 1e6:0.0} MB in {sw.Elapsed.TotalSeconds:0} s, then {(stalled ? "stalled" : "cut")}");
                    if (stalled) st.Stalls++; else st.Cuts++;
                    if (got > st.LongestBytesBeforeCut) { st.LongestBytesBeforeCut = got; st.LongestSecondsBeforeCut = sw.Elapsed.TotalSeconds; }
                    st.Errors.Add(stalled ? "stalled" : Innermost(ex));
                }
                log.LogInformation("probe transfer at offset {Pos} {Kind} after {Bytes} bytes / {Sec:0.0} s: {Error}", pos, stalled ? "stalled" : "cut", got, sw.Elapsed.TotalSeconds, stalled ? "" : Innermost(ex));
            }
        }
    }

    /// <summary>Only the host of a link is recorded — never its path or query, which carry signatures.</summary>
    private static string SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";
}
