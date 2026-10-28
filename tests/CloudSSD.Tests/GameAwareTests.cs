using System.IO.Pipes;
using System.Text;
using CloudSSD.Core;
using CloudSSD.Streaming;
using CloudSSD.Streaming.GameAware;

namespace CloudSSD.Tests;

public class GameAwareTests
{
    private sealed class FakeSource : IGameTelemetrySource
    {
        public event Action<TelemetryMessage>? Message;
        public void Send(TelemetryMessage m) => Message?.Invoke(m);
    }

    private static BlockKey B(string region, int i) => new($"data/{region}.pak", i);

    private static void Visit(RegionAssetMap map, string region, params BlockKey[] blocks)
    {
        map.EnterRegion(region);
        foreach (var b in blocks) map.ObserveRead(b);
    }

    [Fact]
    public void Blocks_are_associated_only_after_repeated_dominant_reads()
    {
        var map = new RegionAssetMap();
        var shared = new BlockKey("data/ui.pak", 0);
        Visit(map, "downtown", B("downtown", 1), shared);
        Assert.Empty(map.ValidatedBlocks("downtown")); // one visit is not evidence
        Visit(map, "harbor", B("harbor", 1), shared);
        Visit(map, "downtown", B("downtown", 1), shared);
        var v = map.ValidatedBlocks("downtown").Select(x => x.Block).ToList();
        Assert.Contains(B("downtown", 1), v);
        Assert.DoesNotContain(shared, v); // read in several regions → never attributed to one
    }

    [Fact]
    public void Entering_a_region_schedules_its_blocks_and_likely_neighbours_and_fast_travel_cancels()
    {
        var map = new RegionAssetMap();
        for (int i = 0; i < 3; i++)
        {
            Visit(map, "downtown", B("downtown", 1), B("downtown", 2));
            Visit(map, "harbor", B("harbor", 1));
            Visit(map, "airport", B("airport", 1));
        }
        var scheduled = new List<(Prediction P, string Tag)>();
        var cancels = 0;
        var src = new FakeSource();
        using var ctl = new GameAwareController(map, src, (p, tag) => { scheduled.Add((p, tag)); return EnqueueResult.Queued; }, _ => cancels++);

        src.Send(new TelemetryMessage("region", "downtown"));
        Assert.Contains(scheduled, s => s.P.Block == B("downtown", 1) && s.P.Priority == Priority.P1Imminent);
        Assert.Contains(scheduled, s => s.P.Block == B("harbor", 1) && s.P.Priority == Priority.P2Likely); // downtown → harbor seen 3×

        scheduled.Clear();
        src.Send(new TelemetryMessage("fasttravel", "airport"));
        Assert.True(cancels >= 2);
        Assert.All(scheduled, s => Assert.Equal(Priority.P1Imminent, s.P.Priority)); // no neighbour guesses after teleport
        Assert.Contains(scheduled, s => s.P.Block == B("airport", 1));
        Assert.Equal(1, ctl.FastTravels);
    }

    [Fact]
    public void Heading_raises_regions_ahead_of_the_player()
    {
        var map = new RegionAssetMap();
        map.ObservePosition("east", 100, 0);
        map.ObservePosition("west", -100, 0);
        foreach (var r in new[] { "east", "west" })
        {
            map.EnterRegion("center");
            map.EnterRegion(r);
        }
        var east = map.NextRegions("center", heading: (1, 0), position: (0, 0)).Single(x => x.Region == "east").Probability;
        var west = map.NextRegions("center", heading: (1, 0), position: (0, 0)).Single(x => x.Region == "west").Probability;
        Assert.True(east > west);
    }

    [Fact]
    public async Task Named_pipe_server_receives_json_lines_and_ignores_garbage()
    {
        await using var server = new NamedPipeTelemetryServer("test" + Guid.NewGuid().ToString("N")[..6]);
        var got = new List<TelemetryMessage>();
        server.Message += m => { lock (got) got.Add(m); };
        await using (var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(5000);
            var bytes = Encoding.UTF8.GetBytes("{\"type\":\"position\",\"x\":1,\"y\":0,\"z\":2,\"region\":\"a\"}\nnot json\n{\"type\":\"fasttravel\",\"region\":\"b\"}\n");
            await client.WriteAsync(bytes);
            await client.FlushAsync();
        }
        for (int i = 0; i < 50 && server.MessagesReceived < 2; i++) await Task.Delay(50);
        Assert.Equal(2, server.MessagesReceived);
        Assert.Equal(1, server.MalformedLines);
        Assert.Equal("fasttravel", got[1].Type);
        Assert.Equal("a", got[0].Region);
    }
}
