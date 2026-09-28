using System.Net.WebSockets;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal sealed class QQGatewayClient(
    QQPlatformConfig config, QQOpenApiClient api, QQTokenProvider tokens,
    Func<GatewayPayload, Task> onDispatch, Action<QQUser?> onReady, IConsoleLogger logger)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _lifetime;
    private Task? _runner;
    private string? _sessionId;
    private long? _sequence;

    public async Task StartAsync()
    {
        if (_runner is not null) throw new InvalidOperationException("Gateway is already running.");
        _lifetime = new CancellationTokenSource();
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
        if (_runner is not null)
        {
            try { await _runner.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        _lifetime = null;
        _runner = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var delaySeconds = config.ReconnectMinimumSeconds;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
                delaySeconds = config.ReconnectMinimumSeconds;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (QQAuthenticationException ex)
            {
                _ready.TrySetException(ex);
                logger.Error(ex.Message);
                break;
            }
            catch (Exception ex) { logger.Warning($"QQ Official gateway disconnected: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false); }
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
        var interval = hello.Data.Deserialize<HelloData>()?.HeartbeatInterval ?? 0;
        if (interval <= 0) throw new InvalidDataException("QQ gateway sent an invalid heartbeat interval.");

        var sendGate = new SemaphoreSlim(1, 1);
        async Task SendAsync(object envelope, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
            await sendGate.WaitAsync(ct).ConfigureAwait(false);
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { sendGate.Release(); }
        }

        if (_sessionId is not null && _sequence is not null)
            await SendAsync(new { op = 6, d = new { token = $"QQBot {token}", session_id = _sessionId, seq = _sequence } }, cancellationToken).ConfigureAwait(false);
        else
            await SendAsync(new { op = 2, d = new { token = $"QQBot {token}", intents = config.Intents,
                shard = new[] { config.ShardId, config.ShardCount },
                properties = new Dictionary<string, string> { ["$os"] = Environment.OSVersion.Platform.ToString(), ["$browser"] = "shirobot", ["$device"] = "shirobot" } } }, cancellationToken).ConfigureAwait(false);

        using var connected = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
            while (await timer.WaitForNextTickAsync(connected.Token).ConfigureAwait(false))
                await SendAsync(new { op = 1, d = _sequence }, connected.Token).ConfigureAwait(false);
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
                            var ready = payload.Data.Deserialize<ReadyData>();
                            _sessionId = ready?.SessionId;
                            onReady(ready?.User);
                            _ready.TrySetResult();
                        }
                        else if (payload.EventType == "RESUMED") _ready.TrySetResult();
                        await onDispatch(payload).ConfigureAwait(false);
                        break;
                    case 1: await SendAsync(new { op = 1, d = _sequence }, connected.Token).ConfigureAwait(false); break;
                    case 7: throw new IOException("QQ gateway requested reconnect.");
                    case 9:
                        if (payload.Data.ValueKind != JsonValueKind.True) { _sessionId = null; _sequence = null; }
                        throw new IOException("QQ gateway rejected the session.");
                    case 11: break;
                }
            }
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
            if (part.MessageType == WebSocketMessageType.Close) throw new IOException("QQ gateway closed the socket.");
            buffer.Write(chunk, 0, part.Count);
            if (buffer.Length > 1024 * 1024) throw new InvalidDataException("QQ gateway payload exceeds 1 MiB.");
        } while (!part.EndOfMessage);
        return JsonSerializer.Deserialize<GatewayPayload>(buffer.ToArray())
            ?? throw new InvalidDataException("QQ gateway payload is empty.");
    }
}
