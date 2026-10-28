using System.Text.Json;
using CloudSSD.SimCloud;
using CloudSSD.TeraBox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudSSD.Tests;

/// <summary>The capability probe end to end against the mock's website surface, with Range support on and off.</summary>
public sealed class TeraBoxProbeTests
{
    private static async Task<ProbeReport> RunAgainstWebMock(Action<TeraBoxMockServer> configure)
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudssd-cli-probe-" + Guid.NewGuid().ToString("N")[..8]);
        await using var mock = new TeraBoxMockServer(Path.Combine(root, "cloud"), Path.Combine(root, "state"));
        configure(mock);
        var store = new TeraBoxWebSessionStore(Path.Combine(root, "web.key"));
        store.Save(new TeraBoxWebSession(mock.BaseUrl, mock.WebCookieHeader, "Mozilla/5.0 (test)", null, DateTimeOffset.UtcNow));
        using var http = TeraBoxWebApi.CreateHttpClient();
        var api = new TeraBoxWebApi(store, http, NullLogger.Instance);
        try
        {
            var report = await new TeraBoxProbe(api, Path.Combine(root, "tmp"), NullLogger.Instance) { RandomReads = 6, Pagination = true }.RunAsync();
            Assert.False(Directory.Exists(mock.DiskPath(report.ProbeFolder)), "the probe removes its own folder");
            return report;
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Probe_passes_on_a_range_capable_connection_and_redacts()
    {
        var r = await RunAgainstWebMock(_ => { });
        Assert.True(r.Failures == 0, r.ToMarkdown());
        Assert.Equal("web (unofficial)", r.Transport);
        Assert.Contains(r.Checks, c => c.Name == "range: across shard boundary" && c.Outcome == ProbeOutcome.Pass);
        Assert.Contains(r.Checks, c => c.Name.StartsWith("pagination") && c.Outcome == ProbeOutcome.Pass);
        Assert.Contains(r.Checks, c => c.Name == "empty file" && c.Detail.StartsWith("refused"));
        Assert.Equal(3, r.RandomReadLatency.Count);
        var json = JsonSerializer.Serialize(r, ProbeReport.Json);
        Assert.DoesNotContain("ndus", json);
        Assert.DoesNotContain("/file/", json); // no download URLs
    }

    [Fact]
    public async Task Throughput_probe_reassembles_parallel_ranges_exactly()
    {
        var root = Path.Combine(Path.GetTempPath(), "cloudssd-cli-probe-tp-" + Guid.NewGuid().ToString("N")[..8]);
        await using var mock = new TeraBoxMockServer(Path.Combine(root, "cloud"), Path.Combine(root, "state"));
        var store = new TeraBoxWebSessionStore(Path.Combine(root, "web.key"));
        store.Save(new TeraBoxWebSession(mock.BaseUrl, mock.WebCookieHeader, "Mozilla/5.0 (test)", null, DateTimeOffset.UtcNow));
        using var http = TeraBoxWebApi.CreateHttpClient();
        try
        {
            var r = await new TeraBoxProbe(new TeraBoxWebApi(store, http, NullLogger.Instance), Path.Combine(root, "tmp"), NullLogger.Instance)
                .ThroughputAsync(9, [2, 4], [4]);
            Assert.True(r.Failures == 0, r.ToMarkdown());
            Assert.Contains(r.Checks, c => c.Name == "4 parallel x 4 MiB ranges" && c.Detail.Contains("hash ok"));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Probe_reports_a_connection_that_ignores_Range_as_failing()
    {
        var r = await RunAgainstWebMock(m => m.DlinkSupportsRange = false);
        Assert.Contains(r.Checks, c => c.Name == "range: middle 1 MiB" && c.Outcome == ProbeOutcome.Fail && c.Detail.Contains("ignored Range"));
    }
}
