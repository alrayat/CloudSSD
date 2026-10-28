using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudSSD.Core;

namespace CloudSSD.Streaming.GameAware;

/// <summary>
/// A message from an authorized game integration (official modding API, plugin, or the game's own telemetry).
/// Protocol: one JSON object per line on the named pipe \\.\pipe\cloudssd-telemetry-&lt;gameId&gt;.
/// <code>
/// {"type":"position","x":10.5,"y":0,"z":-3.2,"region":"downtown"}
/// {"type":"region","region":"harbor"}
/// {"type":"fasttravel","region":"airport"}
/// {"type":"loading","state":"begin"} / {"type":"loading","state":"end"}
/// </code>
/// </summary>
public sealed record TelemetryMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("region")] string? Region = null,
    [property: JsonPropertyName("x")] double? X = null,
    [property: JsonPropertyName("y")] double? Y = null,
    [property: JsonPropertyName("z")] double? Z = null,
    [property: JsonPropertyName("state")] string? State = null);

public interface IGameTelemetrySource
{
    event Action<TelemetryMessage>? Message;
}

/// <summary>
/// Receives telemetry over a named pipe restricted to the current Windows user (PipeOptions.CurrentUserOnly). Accepts one
/// client at a time; malformed lines are ignored. Nothing is injected into the game: the game or its plugin connects.
/// </summary>
public sealed class NamedPipeTelemetryServer : IGameTelemetrySource, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public NamedPipeTelemetryServer(string gameId)
    {
        PipeName = "cloudssd-telemetry-" + new string(gameId.Where(char.IsLetterOrDigit).ToArray());
        _loop = Task.Run(LoopAsync);
    }

    public string PipeName { get; }
    public long MessagesReceived;
    public long MalformedLines;
    public bool ClientConnected { get; private set; }
    public event Action<TelemetryMessage>? Message;

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                ClientConnected = true;
                using var reader = new StreamReader(server, Encoding.UTF8);
                while (!_cts.IsCancellationRequested && await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    if (line.Length > 4096) { Interlocked.Increment(ref MalformedLines); continue; }
                    TelemetryMessage? msg = null;
                    try { msg = JsonSerializer.Deserialize<TelemetryMessage>(line); } catch (JsonException) { }
                    if (msg?.Type is null) { Interlocked.Increment(ref MalformedLines); continue; }
                    Interlocked.Increment(ref MessagesReceived);
                    Message?.Invoke(msg);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { /* client disconnected; wait for the next one */ }
            finally { ClientConnected = false; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch (Exception) { }
        _cts.Dispose();
    }
}

/// <summary>
/// Learns which blocks belong to which world region from real co-occurrence: a block is associated with a region only
/// after it was read during at least <see cref="MinVisits"/> separate visits to that region AND at least
/// <see cref="MinDominance"/> of its reads happened in that region. Shared data (UI, common textures) is therefore
/// never attributed to a single region. Nothing is assumed about archive layout.
/// </summary>
public sealed class RegionAssetMap
{
    private sealed class BlockStats
    {
        public readonly Dictionary<string, HashSet<int>> VisitsByRegion = new();
        public readonly Dictionary<string, int> ReadsByRegion = new();
        public int TotalReads;
    }

    private readonly Dictionary<BlockKey, BlockStats> _blocks = new();
    private readonly Dictionary<(string, string), int> _transitions = new();
    private readonly Dictionary<string, int> _departures = new();
    private readonly Dictionary<string, (double X, double Z, int N)> _centroids = new();
    private readonly object _gate = new();
    private int _visit;

    public int MinVisits { get; init; } = 2;
    public double MinDominance { get; init; } = 0.7;
    public string? CurrentRegion { get; private set; }

    public void EnterRegion(string region)
    {
        lock (_gate)
        {
            if (region == CurrentRegion) return;
            if (CurrentRegion != null)
            {
                _transitions[(CurrentRegion, region)] = _transitions.GetValueOrDefault((CurrentRegion, region)) + 1;
                _departures[CurrentRegion] = _departures.GetValueOrDefault(CurrentRegion) + 1;
            }
            CurrentRegion = region;
            _visit++;
        }
    }

    public void ObservePosition(string region, double x, double z)
    {
        lock (_gate)
        {
            var c = _centroids.GetValueOrDefault(region);
            _centroids[region] = ((c.X * c.N + x) / (c.N + 1), (c.Z * c.N + z) / (c.N + 1), c.N + 1);
        }
    }

    public void ObserveRead(BlockKey block)
    {
        lock (_gate)
        {
            if (CurrentRegion is null) return;
            if (!_blocks.TryGetValue(block, out var s)) _blocks[block] = s = new BlockStats();
            s.TotalReads++;
            s.ReadsByRegion[CurrentRegion] = s.ReadsByRegion.GetValueOrDefault(CurrentRegion) + 1;
            if (!s.VisitsByRegion.TryGetValue(CurrentRegion, out var v)) s.VisitsByRegion[CurrentRegion] = v = new HashSet<int>();
            v.Add(_visit);
        }
    }

    /// <summary>Blocks validated as belonging to <paramref name="region"/>, with confidence = dominance.</summary>
    public IReadOnlyList<(BlockKey Block, double Confidence)> ValidatedBlocks(string region)
    {
        lock (_gate)
        {
            var list = new List<(BlockKey, double)>();
            foreach (var (k, s) in _blocks)
            {
                if (!s.VisitsByRegion.TryGetValue(region, out var visits) || visits.Count < MinVisits) continue;
                var dominance = s.ReadsByRegion[region] / (double)s.TotalReads;
                if (dominance >= MinDominance) list.Add((k, dominance));
            }
            return list;
        }
    }

    /// <summary>Likely next regions from observed transitions, optionally weighted by movement heading toward region centroids.</summary>
    public IReadOnlyList<(string Region, double Probability)> NextRegions(string from, (double Dx, double Dz)? heading = null, (double X, double Z)? position = null)
    {
        lock (_gate)
        {
            var dep = _departures.GetValueOrDefault(from);
            if (dep == 0) return [];
            var result = new List<(string, double)>();
            foreach (var ((a, b), n) in _transitions)
            {
                if (a != from) continue;
                var p = n / (double)(dep + 1);
                if (heading is { } h && position is { } pos && _centroids.TryGetValue(b, out var c) && c.N > 0)
                {
                    var vx = c.X - pos.X;
                    var vz = c.Z - pos.Z;
                    var len = Math.Sqrt(vx * vx + vz * vz) * Math.Sqrt(h.Dx * h.Dx + h.Dz * h.Dz);
                    if (len > 1e-9)
                    {
                        var cos = (vx * h.Dx + vz * h.Dz) / len;
                        p *= 0.5 + 0.5 * Math.Max(0, cos) + 0.25; // moving toward a region raises it, away lowers it
                    }
                }
                result.Add((b, Math.Min(1, p)));
            }
            return result.OrderByDescending(x => x.Item2).ToList();
        }
    }

    public int KnownBlocks { get { lock (_gate) return _blocks.Count; } }
}

/// <summary>
/// Game-Aware prefetching for titles with an authorized telemetry integration. On region entry the region's validated
/// blocks are scheduled at P1 and likely next regions at P2; fast travel cancels speculative work and reprioritizes the
/// destination. Without telemetry this class does nothing — Universal Mode keeps working.
/// </summary>
public sealed class GameAwareController : IDisposable
{
    private readonly RegionAssetMap _map;
    private readonly Func<Prediction, string, EnqueueResult> _schedule;
    private readonly Func<string, int> _cancelTag;
    private readonly IGameTelemetrySource _source;
    private (double X, double Z)? _lastPos;
    private (double Dx, double Dz)? _heading;

    public GameAwareController(RegionAssetMap map, IGameTelemetrySource source, Func<Prediction, string, EnqueueResult> schedule, Func<string, int> cancelTag)
    {
        _map = map;
        _source = source;
        _schedule = schedule;
        _cancelTag = cancelTag;
        _source.Message += OnMessage;
    }

    public const string TagPrefix = "region:";
    public int Scheduled { get; private set; }
    public int FastTravels { get; private set; }
    public string? Region => _map.CurrentRegion;

    public void OnApplicationRead(BlockKey block) => _map.ObserveRead(block);

    private void OnMessage(TelemetryMessage m)
    {
        switch (m.Type.ToLowerInvariant())
        {
            case "position" when m.X is { } x && m.Z is { } z:
                if (_lastPos is { } p) _heading = (x - p.X, z - p.Z);
                _lastPos = (x, z);
                if (m.Region != null)
                {
                    _map.ObservePosition(m.Region, x, z);
                    if (m.Region != _map.CurrentRegion) EnterRegion(m.Region, fastTravel: false);
                }
                break;
            case "region" when m.Region != null:
                EnterRegion(m.Region, fastTravel: false);
                break;
            case "fasttravel" when m.Region != null:
                FastTravels++;
                EnterRegion(m.Region, fastTravel: true);
                break;
        }
    }

    private void EnterRegion(string region, bool fastTravel)
    {
        var previous = _map.CurrentRegion;
        _map.EnterRegion(region);
        // Neighbour predictions made for the old position are stale now.
        _cancelTag(TagPrefix);
        foreach (var (block, conf) in _map.ValidatedBlocks(region))
            if (_schedule(new Prediction(block, Priority.P1Imminent, conf, "region"), TagPrefix + region) == EnqueueResult.Queued) Scheduled++;
        if (fastTravel) return; // after a teleport, neighbours of the destination are not imminent yet
        foreach (var (next, p) in _map.NextRegions(region, _heading, _lastPos))
        {
            if (p < 0.3 || next == previous) continue;
            foreach (var (block, conf) in _map.ValidatedBlocks(next))
                if (_schedule(new Prediction(block, Priority.P2Likely, conf * p, "region"), TagPrefix + next) == EnqueueResult.Queued) Scheduled++;
        }
    }

    public void Dispose() => _source.Message -= OnMessage;
}
