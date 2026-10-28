using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudSSD.Core;

namespace CloudSSD.App.Persistence;

public sealed record AgentMountStatus(string Id, string Name, string Letter, string State, string? Message, bool Permanent);

public sealed record AgentStatus(int Pid, DateTimeOffset Started, IReadOnlyList<AgentMountStatus> Mounts, IReadOnlyList<string> Waiting);

public sealed record AgentResponse(bool Ok, string? Error = null, AgentStatus? Status = null);

/// <summary>
/// Local control channel of the background agent: a named pipe created with <see cref="PipeOptions.CurrentUserOnly"/>,
/// so only processes of the same Windows user can connect, and the client verifies that the server belongs to the same
/// user. No network port, no secrets on the wire. One JSON line in, one JSON line out.
/// Commands: <c>status</c>, <c>release</c> (detach from the drives and exit so the desktop app can take them over) and
/// <c>stop</c> (unmount all drives gracefully and exit; used by the uninstaller).
/// </summary>
public static class AgentPipe
{
    /// <summary>Pipe name per data folder (so a test or portable data folder never talks to the user's real agent).</summary>
    public static string NameFor(AppPaths paths)
    {
        var root = Path.GetFullPath(paths.DataRoot).TrimEnd('\\').ToLowerInvariant();
        return "cloudssd-agent-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16].ToLowerInvariant();
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed class AgentControlServer : IAsyncDisposable
{
    private readonly string _name;
    private readonly Func<string, Task<(AgentResponse Response, Func<Task>? After)>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public AgentControlServer(string pipeName, Func<string, Task<(AgentResponse, Func<Task>?)>> handler)
    {
        _name = pipeName;
        _handler = handler;
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Func<Task>? after = null;
            try
            {
                await using var server = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await reader.ReadLineAsync(timeout.Token);
                AgentResponse response;
                if (line is null || line.Length > 256) response = new AgentResponse(false, "Malformed request");
                else
                {
                    var cmd = JsonDocument.Parse(line).RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
                    (response, after) = await _handler(cmd);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentPipe.Json));
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or JsonException or OperationCanceledException) { /* client went away */ }
            if (after != null) await after();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch (Exception) { }
        _cts.Dispose();
    }
}

public static class AgentControlClient
{
    /// <summary>Sends one command; null if no agent is listening for this data folder.</summary>
    public static async Task<AgentResponse?> SendAsync(AppPaths paths, string cmd, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", AgentPipe.NameFor(paths), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
            await client.ConnectAsync(cts.Token);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { cmd }, AgentPipe.Json));
            var line = await reader.ReadLineAsync(cts.Token);
            return line is null ? null : JsonSerializer.Deserialize<AgentResponse>(line, AgentPipe.Json);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static Task<AgentResponse?> StatusAsync(AppPaths paths, CancellationToken ct = default) => SendAsync(paths, "status", TimeSpan.FromSeconds(2), ct);

    /// <summary>
    /// Asks a running agent to hand its drives over (they stay mounted) and exit. Returns true when no agent holds the
    /// data folder any more (also when none was running).
    /// </summary>
    public static async Task<bool> ReleaseAsync(AppPaths paths, TimeSpan wait, CancellationToken ct = default)
    {
        var r = await SendAsync(paths, "release", TimeSpan.FromSeconds(3), ct);
        if (r is { Ok: false }) return false;
        return await WaitForLockFreeAsync(paths, wait, ct);
    }

    /// <summary>Asks a running agent to unmount everything (uploads get up to 60 s) and exit.</summary>
    public static async Task<bool> StopAsync(AppPaths paths, TimeSpan wait, CancellationToken ct = default)
    {
        var r = await SendAsync(paths, "stop", TimeSpan.FromSeconds(3), ct);
        if (r is { Ok: false }) return false;
        return await WaitForLockFreeAsync(paths, wait, ct);
    }

    public static async Task<bool> WaitForLockFreeAsync(AppPaths paths, TimeSpan wait, CancellationToken ct = default)
    {
        var lockFile = Path.Combine(paths.DataRoot, "instance.lock");
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using (new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) return true;
            }
            catch (IOException) when (sw.Elapsed < wait) { await Task.Delay(200, ct); }
            catch (IOException) { return false; }
        }
    }

    /// <summary>Starts the agent (hidden, no window). It takes over drives left running by the desktop app.</summary>
    public static Process? Launch(AppPaths paths, string? agentExe = null, params string[] extraArgs)
    {
        agentExe ??= StartupRegistration.AgentPath;
        if (!File.Exists(agentExe)) throw new FileNotFoundException("CloudSSD.Agent.exe was not found next to CloudSSD.", agentExe);
        var psi = new ProcessStartInfo(agentExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(agentExe)! };
        psi.ArgumentList.Add("--home");
        psi.ArgumentList.Add(paths.DataRoot);
        foreach (var a in extraArgs) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }
}
