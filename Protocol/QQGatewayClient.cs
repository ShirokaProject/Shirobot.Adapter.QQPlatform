using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal sealed class QQGatewayClient(
    QQPlatformConfig config, QQOpenApiClient api, QQTokenProvider tokens,
    Func<GatewayPayload, Task> onDispatch, Action<QQUser?> onReady, IConsoleLogger logger,
    Action<GatewayPayload>? onReceive = null, Action<string>? onFatal = null)
{
    private const int DispatchQueueCapacity = 1024;
    private static readonly TimeSpan QuickDisconnectThreshold = TimeSpan.FromSeconds(5);
    private const int MaxQuickDisconnects = 3;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _lifetime;
    private Task? _runner;
    private Task? _worker;
    private Channel<GatewayPayload>? _queue;
    private string? _sessionId;
    private long? _sequence;
    private bool _connectedSuccessfully;
    private int _quickDisconnects;

    public async Task StartAsync()
    {
        if (_runner is not null) throw new InvalidOperationException("Gateway is already running.");
        _lifetime = new CancellationTokenSource();
        _queue = Channel.CreateBounded<GatewayPayload>(new BoundedChannelOptions(DispatchQueueCapacity)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(() => DispatchLoopAsync(_queue.Reader, _lifetime.Token));
        _runner = RunAsync(_lifetime.Token);
        try
        {
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(config.StartupTimeoutSeconds)).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_lifetime is null) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _queue?.Writer.TryComplete();
        foreach (var task in new[] { _runner, _worker })
        {
            if (task is null) continue;
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        _lifetime = null;
        _runner = null;
        _worker = null;
        _queue = null;
    }

    /// <summary>Events run one at a time and in order, but never stall the socket reader or the heartbeat.</summary>
    private async Task DispatchLoopAsync(ChannelReader<GatewayPayload> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try { await onDispatch(payload).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.Error($"QQ Official event {payload.EventType} dispatch failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var delaySeconds = config.ReconnectMinimumSeconds;
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan? forcedDelay = null;
            var openedAt = DateTimeOffset.UtcNow;
            try
            {
                await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (QQAuthenticationException ex)
            {
                _ready.TrySetException(ex);
                logger.Error(ex.Message);
                onFatal?.Invoke(ex.Message);
                break;
            }
            catch (QQGatewayClosedException ex)
            {
                var action = QQGatewayClosePolicy.Decide(ex.Code);
                if (action.Fatal)
                {
                    _ready.TrySetException(ex);
                    logger.Error($"QQ Official gateway stopped: {action.Reason}. Contact the QQ platform; not reconnecting.");
                    onFatal?.Invoke(action.Reason);
                    break;
                }
                logger.Warning($"QQ Official gateway closed: {action.Reason}.");
                if (action.ClearSession) { _sessionId = null; _sequence = null; }
                if (action.RefreshToken) tokens.Invalidate();
                forcedDelay = action.Delay;
            }
            catch (Exception ex) { logger.Warning($"QQ Official gateway disconnected: {ex.Message}"); }

            // A connection that was healthy resets the backoff; one that dies at once counts towards a long pause.
            if (_connectedSuccessfully)
            {
                delaySeconds = config.ReconnectMinimumSeconds;
                _connectedSuccessfully = false;
            }
            if (DateTimeOffset.UtcNow - openedAt < QuickDisconnectThreshold && ++_quickDisconnects >= MaxQuickDisconnects)
            {
                logger.Warning("QQ Official gateway keeps disconnecting right after connecting; pausing before the next attempt.");
                _quickDisconnects = 0;
                forcedDelay ??= QQGatewayClosePolicy.RateLimitDelay;
            }
            else if (DateTimeOffset.UtcNow - openedAt >= QuickDisconnectThreshold) _quickDisconnects = 0;

            try { await Task.Delay(forcedDelay ?? TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            delaySeconds = Math.Min(config.ReconnectMaximumSeconds, delaySeconds * 2);
        }
    }

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        var uri = await api.GetGatewayAsync(cancellationToken).ConfigureAwait(false);
        var token = await tokens.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        var hello = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        if (hello.Op != 10) throw new InvalidDataException("QQ gateway did not send Hello.");
        var interval = hello.Data.Deserialize<HelloData>(QQJson.Options)?.HeartbeatInterval ?? 0;
        if (interval <= 0) throw new InvalidDataException("QQ gateway sent an invalid heartbeat interval.");

        var sendGate = new SemaphoreSlim(1, 1);
        async Task SendAsync(JsonObject envelope, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, QQJson.Options);
            await sendGate.WaitAsync(ct).ConfigureAwait(false);
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { sendGate.Release(); }
        }

        if (_sessionId is not null && _sequence is not null)
            await SendAsync(new JsonObject { ["op"] = 6, ["d"] = new JsonObject
                { ["token"] = $"QQBot {token}", ["session_id"] = _sessionId, ["seq"] = _sequence } }, cancellationToken).ConfigureAwait(false);
        else
            await SendAsync(new JsonObject { ["op"] = 2, ["d"] = new JsonObject
                { ["token"] = $"QQBot {token}", ["intents"] = config.EffectiveIntents,
                  ["shard"] = new JsonArray(config.ShardId, config.ShardCount),
                  ["properties"] = new JsonObject { ["$os"] = Environment.OSVersion.Platform.ToString(),
                      ["$browser"] = "shirobot", ["$device"] = "shirobot" } } }, cancellationToken).ConfigureAwait(false);

        using var connected = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var awaitingAck = false;
        var ackTimedOut = false;
        var heartbeat = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
            while (await timer.WaitForNextTickAsync(connected.Token).ConfigureAwait(false))
            {
                // A silent, half-open connection never closes by itself: no ACK for a whole interval means it is dead.
                if (Volatile.Read(ref awaitingAck))
                {
                    ackTimedOut = true;
                    await connected.CancelAsync().ConfigureAwait(false);
                    return;
                }
                Volatile.Write(ref awaitingAck, true);
                await SendAsync(new JsonObject { ["op"] = 1, ["d"] = _sequence }, connected.Token).ConfigureAwait(false);
            }
        }, connected.Token);

        try
        {
            while (!connected.IsCancellationRequested)
            {
                var payload = await ReceiveAsync(socket, connected.Token).ConfigureAwait(false);
                if (payload.Sequence is long sequence) _sequence = sequence;
                switch (payload.Op)
                {
                    case 0:
                        if (payload.EventType == "READY")
                        {
                            var ready = payload.Data.Deserialize<ReadyData>(QQJson.Options);
                            _sessionId = ready?.SessionId;
                            _connectedSuccessfully = true;
                            onReady(ready?.User);
                            _ready.TrySetResult();
                        }
                        else if (payload.EventType == "RESUMED")
                        {
                            _connectedSuccessfully = true;
                            _ready.TrySetResult();
                        }
                        // Time-sensitive work (button acknowledgements) must not wait behind earlier events.
                        onReceive?.Invoke(payload);
                        await _queue!.Writer.WriteAsync(payload, connected.Token).ConfigureAwait(false);
                        break;
                    case 1: await SendAsync(new JsonObject { ["op"] = 1, ["d"] = _sequence }, connected.Token).ConfigureAwait(false); break;
                    case 7: throw new IOException("QQ gateway requested reconnect.");
                    case 9:
                        if (payload.Data.ValueKind != JsonValueKind.True)
                        {
                            _sessionId = null; _sequence = null;
                            tokens.Invalidate();
                        }
                        throw new IOException("QQ gateway rejected the session.");
                    case 11: Volatile.Write(ref awaitingAck, false); break;
                }
            }
        }
        catch (OperationCanceledException) when (ackTimedOut && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException("QQ gateway did not acknowledge the heartbeat.");
        }
        finally
        {
            await connected.CancelAsync().ConfigureAwait(false);
            try { await heartbeat.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            sendGate.Dispose();
        }
    }

    private static async Task<GatewayPayload> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), cancellationToken).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close)
                throw new QQGatewayClosedException(
                    (part.CloseStatus ?? socket.CloseStatus) is { } status ? (int)status : null,
                    part.CloseStatusDescription ?? socket.CloseStatusDescription);
            buffer.Write(chunk, 0, part.Count);
            if (buffer.Length > 1024 * 1024) throw new InvalidDataException("QQ gateway payload exceeds 1 MiB.");
        } while (!part.EndOfMessage);
        return JsonSerializer.Deserialize<GatewayPayload>(buffer.ToArray(), QQJson.Options)
            ?? throw new InvalidDataException("QQ gateway payload is empty.");
    }
}
