using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xcord.Bridge.Discord;

sealed class DiscordRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Discord local RPC over the Windows named pipe (\\.\pipe\discord-ipc-N).
/// Wire format: [opcode: u32 LE][length: u32 LE][JSON payload]. Responses are matched to
/// requests by nonce; frames without a pending nonce are DISPATCH events.
/// </summary>
sealed class IpcClient : IAsyncDisposable
{
    const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;

    readonly string _clientId;
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    NamedPipeClientStream? _pipe;
    CancellationTokenSource? _readCts;
    int _disconnected;

    public IpcClient(string clientId) => _clientId = clientId;

    /// <summary>Raised on the read-loop thread for every DISPATCH event (evt, data).</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>Raised once when the pipe closes or Discord sends CLOSE.</summary>
    public event Action<string>? Disconnected;

    public bool IsConnected => _pipe?.IsConnected == true && Volatile.Read(ref _disconnected) == 0;

    /// <summary>Why the last <see cref="ConnectAsync"/> handshake was refused (e.g. "Invalid Client ID").</summary>
    public string? RejectReason { get; private set; }

    public static bool IsDiscordPipePresent() => FindPipes().Any();

    static IEnumerable<string> FindPipes()
    {
        string[] all;
        try { all = Directory.GetFiles(@"\\.\pipe\"); }
        catch { yield break; }
        for (var i = 0; i < 10; i++)
        {
            var name = $"discord-ipc-{i}";
            if (all.Any(p => p.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase)))
                yield return name;
        }
    }

    /// <summary>Connects and handshakes. Returns the READY data (contains the local user) or null.</summary>
    public async Task<JsonElement?> ConnectAsync(CancellationToken ct)
    {
        foreach (var name in FindPipes())
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(1000, ct);
                await WriteFrameAsync(pipe, OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = _clientId }, ct);
                var (op, payload) = await ReadFrameAsync(pipe, ct);

                if (op == OpFrame && payload.TryGetProperty("evt", out var evt) && evt.GetString() == "READY")
                {
                    _pipe = pipe;
                    _disconnected = 0;
                    _readCts = new CancellationTokenSource();
                    _ = Task.Run(() => ReadLoopAsync(pipe, _readCts.Token));
                    Log.Info($"IPC connected on {name}");
                    return payload.GetProperty("data").Clone();
                }

                Log.Error($"IPC handshake on {name} rejected: op={op} {payload}");
                RejectReason = payload.TryGetProperty("message", out var m) ? m.GetString() : payload.ToString();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { pipe.Dispose(); throw; }
            catch (Exception ex) { Log.Error($"IPC connect {name}", ex); }
            pipe.Dispose();
        }
        return null;
    }

    /// <summary>Sends a command and waits for the response with the same nonce. Throws on ERROR.</summary>
    public async Task<JsonElement> SendAsync(string cmd, JsonObject? args = null, string? evt = null, TimeSpan? timeout = null)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Not connected to Discord");
        var nonce = Guid.NewGuid().ToString("N");
        var payload = new JsonObject { ["cmd"] = cmd, ["nonce"] = nonce, ["args"] = args ?? new JsonObject() };
        if (evt is not null) payload["evt"] = evt;

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        try
        {
            await WriteFrameAsync(pipe, OpFrame, payload, CancellationToken.None);
            var response = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

            if (response.TryGetProperty("evt", out var e) && e.GetString() == "ERROR")
            {
                var data = response.GetProperty("data");
                var code = data.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                var msg = data.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                throw new DiscordRpcException(code, $"{cmd}: {msg} ({code})");
            }
            return response.TryGetProperty("data", out var d) ? d : default;
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    public Task SubscribeAsync(string evt, JsonObject? args = null) => SendAsync("SUBSCRIBE", args, evt);

    public Task UnsubscribeAsync(string evt, JsonObject? args = null) => SendAsync("UNSUBSCRIBE", args, evt);

    async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        var reason = "pipe closed";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (op, payload) = await ReadFrameAsync(pipe, ct);
                switch (op)
                {
                    case OpFrame:
                        Dispatch(payload);
                        break;
                    case OpPing:
                        await WriteFrameAsync(pipe, OpPong, JsonNode.Parse(payload.GetRawText())!.AsObject(), ct);
                        break;
                    case OpClose:
                        reason = payload.TryGetProperty("message", out var m) ? m.GetString() ?? "closed" : "closed";
                        return;
                }
            }
        }
        catch (OperationCanceledException) { reason = "disconnected"; }
        catch (EndOfStreamException) { }
        catch (Exception ex) { reason = ex.Message; Log.Error("IPC read loop", ex); }
        finally
        {
            Teardown(reason);
        }
    }

    void Dispatch(JsonElement payload)
    {
        if (payload.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String
            && _pending.TryRemove(n.GetString()!, out var tcs))
        {
            tcs.TrySetResult(payload);
            return;
        }

        if (payload.TryGetProperty("evt", out var evt) && evt.ValueKind == JsonValueKind.String)
        {
            var data = payload.TryGetProperty("data", out var d) ? d : default;
            try { EventReceived?.Invoke(evt.GetString()!, data); }
            catch (Exception ex) { Log.Error($"Event handler {evt}", ex); }
        }
    }

    void Teardown(string reason)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 1) return;
        Log.Info($"IPC disconnected: {reason}");
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        foreach (var kv in _pending)
            kv.Value.TrySetException(new IOException("Discord connection lost"));
        _pending.Clear();
        Disconnected?.Invoke(reason);
    }

    async Task WriteFrameAsync(Stream stream, int op, JsonObject payload, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, 8);

        await _writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(frame, ct);
            await stream.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    static async Task<(int Op, JsonElement Payload)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (len is < 0 or > 16 * 1024 * 1024) throw new InvalidDataException($"Bad frame length {len}");

        var body = new byte[len];
        await stream.ReadExactlyAsync(body, ct);
        using var doc = JsonDocument.Parse(body);
        return (op, doc.RootElement.Clone());
    }

    public ValueTask DisposeAsync()
    {
        _readCts?.Cancel();
        Teardown("disposed");
        return ValueTask.CompletedTask;
    }
}
