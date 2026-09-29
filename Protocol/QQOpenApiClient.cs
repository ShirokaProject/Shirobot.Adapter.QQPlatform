using System.Net.Http.Json;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal sealed class QQOpenApiClient
{
    private readonly QQApiTransport _transport;
    private readonly QQChunkedUploadClient _chunked;
    private readonly Dictionary<string, long> _acknowledged = [];
    private readonly Queue<(string Id, long Serial)> _acknowledgementOrder = new();
    private long _acknowledgementSerial;

    public QQOpenApiClient(HttpClient http, QQPlatformConfig config, QQTokenProvider tokens,
        HttpClient? uploadHttp = null)
    {
        _transport = new QQApiTransport(http, config, tokens);
        _chunked = new QQChunkedUploadClient(_transport, uploadHttp ?? http);
    }

    public async Task<Uri> GetGatewayAsync(CancellationToken cancellationToken)
    {
        using var response = await _transport.SendAsync(HttpMethod.Get, QQApiRoutes.Gateway, null, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<GatewayResponse>(cancellationToken).ConfigureAwait(false);
        if (body?.Url is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("wss" or "ws"))
            throw new InvalidDataException("QQ gateway response has no valid WebSocket URL.");
        return uri;
    }

    public async Task<SentMessage> SendMessageAsync(Channel channel, QQSendRequest message, CancellationToken cancellationToken = default)
        => await SendMessageAsync(QQApiRoutes.Messages(channel), message, cancellationToken).ConfigureAwait(false);

    public async Task<SentMessage> SendMessageAsync(string route, QQSendRequest message, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Post, route, message, cancellationToken).ConfigureAwait(false);
        var sent = await response.Content.ReadFromJsonAsync<QQSendResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("QQ message response is empty.");
        if (string.IsNullOrWhiteSpace(sent.Id))
            throw new InvalidDataException("QQ message response contains no message ID (it may be pending audit).");
        var result = new SentMessage(sent.Id);
        if (sent.Timestamp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(sent.Timestamp.GetString(), out var timestamp))
            result = result with { Timestamp = timestamp };
        else if (sent.Timestamp.ValueKind == JsonValueKind.Number && sent.Timestamp.TryGetInt64(out var seconds))
            result = result with { Timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds) };
        return result;
    }

    public async Task SendTypingAsync(Channel channel, QQSendRequest message, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Post, QQApiRoutes.Messages(channel), message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> SendStreamFrameAsync(Channel channel, QQStreamRequest frame, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Post, QQApiRoutes.StreamMessages(channel), frame, cancellationToken).ConfigureAwait(false);
        var sent = await response.Content.ReadFromJsonAsync<QQSendResponse>(cancellationToken).ConfigureAwait(false);
        return sent?.Id;
    }

    public async Task<string> UploadRemoteAsync(Channel channel, int fileType, Uri uri,
        string? fileName = null, CancellationToken cancellationToken = default)
    {
        var uploaded = await PostRemoteAsync(channel, fileType, uri, false, fileName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uploaded?.FileInfo))
            throw new InvalidDataException("QQ media upload response contains no file_info.");
        return uploaded.FileInfo;
    }

    public async Task<SentMessage> SendRemoteAsync(Channel channel, int fileType, Uri uri,
        string? fileName = null, CancellationToken cancellationToken = default)
    {
        var uploaded = await PostRemoteAsync(channel, fileType, uri, true, fileName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uploaded.Id))
            throw new InvalidDataException("QQ direct media send response contains no message ID.");
        return new SentMessage(uploaded.Id);
    }

    public async Task<string> UploadLocalAsync(Channel channel, int fileType, string path,
        string fileName, CancellationToken cancellationToken = default)
    {
        var uploaded = await _chunked.UploadAsync(channel, fileType, path, fileName, false, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uploaded.FileInfo))
            throw new InvalidDataException("QQ local upload response contains no file_info.");
        return uploaded.FileInfo;
    }

    public async Task<SentMessage> SendLocalAsync(Channel channel, int fileType, string path,
        string fileName, CancellationToken cancellationToken = default)
    {
        var uploaded = await _chunked.UploadAsync(channel, fileType, path, fileName, true, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uploaded.Id))
            throw new InvalidDataException("QQ direct local upload response contains no message ID.");
        return new SentMessage(uploaded.Id);
    }

    private async Task<QQUploadResponse> PostRemoteAsync(Channel channel, int fileType, Uri uri,
        bool sendMessage, string? fileName, CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https"))
            throw new NotSupportedException("QQ Official media upload requires an HTTP(S) URL.");
        using var response = await _transport.SendAsync(HttpMethod.Post, QQApiRoutes.Files(channel),
            new QQUploadRequest(fileType, uri.AbsoluteUri, sendMessage, fileName), cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<QQUploadResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("QQ media upload response is empty.");
    }

    public async Task<string?> GetGroupNameAsync(string groupOpenId, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Get, QQApiRoutes.GroupInfo(groupOpenId), null, cancellationToken).ConfigureAwait(false);
        var info = await response.Content.ReadFromJsonAsync<QQGroupInfo>(cancellationToken).ConfigureAwait(false);
        if (info is null || !string.Equals(info.GroupOpenId, groupOpenId, StringComparison.Ordinal))
            throw new InvalidDataException("QQ group info response has a mismatched group_openid.");
        return info.GroupName;
    }

    public async Task AcknowledgeInteractionAsync(string interactionId,
        QOfficialInteractionResponseCode code = QOfficialInteractionResponseCode.Success,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interactionId);
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        long serial;
        lock (_acknowledged)
        {
            if (_acknowledged.ContainsKey(interactionId)) return;
            serial = ++_acknowledgementSerial;
            _acknowledged.Add(interactionId, serial);
            _acknowledgementOrder.Enqueue((interactionId, serial));
            while (_acknowledgementOrder.Count > 4096)
            {
                var old = _acknowledgementOrder.Dequeue();
                if (_acknowledged.TryGetValue(old.Id, out var current) && current == old.Serial)
                    _acknowledged.Remove(old.Id);
            }
        }
        try
        {
            using var response = await _transport.SendAsync(HttpMethod.Put, QQApiRoutes.Interaction(interactionId),
                new { code = (int)code }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_acknowledged)
            {
                if (_acknowledged.TryGetValue(interactionId, out var current) && current == serial)
                    _acknowledged.Remove(interactionId);
            }
            throw;
        }
    }


    public async Task DeleteMessageAsync(Channel channel, string messageId, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Delete,
            QQApiRoutes.Message(channel, messageId), null, cancellationToken).ConfigureAwait(false);
    }
}
