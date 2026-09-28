using System.Net.Http.Json;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal sealed class QQOpenApiClient(HttpClient http, QQPlatformConfig config, QQTokenProvider tokens)
{
    private readonly QQApiTransport _transport = new(http, config, tokens);

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
    {
        using var response = await _transport.SendAsync(HttpMethod.Post, QQApiRoutes.Messages(channel), message, cancellationToken).ConfigureAwait(false);
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

    public async Task<string> UploadRemoteAsync(Channel channel, int fileType, Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri.Scheme is not ("http" or "https"))
            throw new NotSupportedException("QQ Official media upload currently requires an HTTP(S) URL.");
        using var response = await _transport.SendAsync(HttpMethod.Post, QQApiRoutes.Files(channel),
            new QQUploadRequest(fileType, uri.AbsoluteUri, false), cancellationToken).ConfigureAwait(false);
        var uploaded = await response.Content.ReadFromJsonAsync<QQUploadResponse>(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uploaded?.FileInfo))
            throw new InvalidDataException("QQ media upload response contains no file_info.");
        return uploaded.FileInfo;
    }

    public async Task<string?> GetGroupNameAsync(string groupOpenId, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Get, QQApiRoutes.GroupInfo(groupOpenId), null, cancellationToken).ConfigureAwait(false);
        var info = await response.Content.ReadFromJsonAsync<QQGroupInfo>(cancellationToken).ConfigureAwait(false);
        if (info is null || !string.Equals(info.GroupOpenId, groupOpenId, StringComparison.Ordinal))
            throw new InvalidDataException("QQ group info response has a mismatched group_openid.");
        return info.GroupName;
    }

    public async Task AcknowledgeInteractionAsync(string interactionId, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Put, QQApiRoutes.Interaction(interactionId),
            new { code = 0 }, cancellationToken).ConfigureAwait(false);
    }


    public async Task DeleteMessageAsync(Channel channel, string messageId, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Delete,
            QQApiRoutes.Message(channel, messageId), null, cancellationToken).ConfigureAwait(false);
    }
}
