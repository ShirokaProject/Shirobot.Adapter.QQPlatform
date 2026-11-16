using System.Net.Http.Json;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

using ShiroBot.Adapter.QQPlatform.Transport;
using ShiroBot.Adapter.QQPlatform.Protocol.Support;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal sealed class QQOpenApiClient
{
    internal QQMaterialClient Materials { get; }
    private readonly QQApiTransport _transport;
    private readonly QQChunkedUploadClient _chunked;
    private readonly QQUploadCache _uploads = new();
    private readonly QQBoundedCache<string> _sentReferenceIndices = new(4096);
    private readonly Dictionary<string, Task> _acknowledged = [];
    private readonly Queue<(string Id, Task Task)> _acknowledgementOrder = [];


    public QQOpenApiClient(HttpClient http, QQPlatformConfig config, QQTokenProvider tokens,
        HttpClient? uploadHttp = null)
    {
        _transport = new QQApiTransport(http, config, tokens);
        Materials = new QQMaterialClient(_transport, uploadHttp ?? http);
        _chunked = new QQChunkedUploadClient(_transport, uploadHttp ?? http);
    }

    public async Task<Uri> GetGatewayAsync(CancellationToken cancellationToken)
    {
        using var response = await _transport.SendAsync(HttpMethod.Get, QQApiRoutes.Gateway, null, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<GatewayResponse>(QQJson.Options, cancellationToken).ConfigureAwait(false);
        if (body?.Url is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("wss" or "ws"))
            throw new InvalidDataException("QQ gateway response has no valid WebSocket URL.");
        return uri;
    }

    public async Task<QQUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Get, QQApiRoutes.CurrentUser, null, cancellationToken)
            .ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<QQUser>(QQJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("QQ current-user response is empty.");
    }

    public async Task<QQUser> GetUserInfoAsync(string userOpenId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userOpenId);
        using var response = await _transport.SendAsync(HttpMethod.Get,
            $"v2/users/{Uri.EscapeDataString(userOpenId)}/info", null, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<QQUser>(QQJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("QQ user-info response is empty.");
    }

    public async Task<SentMessage> SendMessageAsync(Channel channel, QQSendRequest message, CancellationToken cancellationToken = default)
        => await SendMessageAsync(QQApiRoutes.Messages(channel), message, cancellationToken).ConfigureAwait(false);

    public async Task<SentMessage> SendMessageAsync(string route, QQSendRequest message, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _transport.SendAsync(HttpMethod.Post, route, message, cancellationToken).ConfigureAwait(false);
            var sent = await response.Content.ReadFromJsonAsync<QQSendResponse>(QQJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("QQ message response is empty.");
            if (string.IsNullOrWhiteSpace(sent.Id))
                throw new InvalidDataException("QQ message response contains no message ID (it may be pending audit).");
            var result = new SentMessage(sent.Id);
            if (sent.Timestamp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(sent.Timestamp.GetString(), out var timestamp))
                result = result with { Timestamp = timestamp };
            else if (sent.Timestamp.ValueKind == JsonValueKind.Number && sent.Timestamp.TryGetInt64(out var seconds))
                result = result with { Timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds) };
            if (!string.IsNullOrWhiteSpace(sent.ExtInfo?.ReferenceIndex))
                _sentReferenceIndices.Set(sent.Id, sent.ExtInfo.ReferenceIndex, TimeSpan.FromMinutes(5));
            return result;
        }
        catch (QQApiException ex) when (ex.IsLimitExceeded)
        {
            throw new QLimitExceededException("发送消息", ex.ErrorCode, ex.Message);
        }
        catch (QQApiException ex) when (ex.IsPermissionDenied)
        {
            throw new QPermissionDeniedException("发送消息", ex.ErrorCode, ex.Message);
        }
    }

    internal string? TakeSentReferenceIndex(string messageId) =>
        _sentReferenceIndices.Take(messageId);

    /// <summary>Sends a protocol-level outgoing payload to a group or direct channel.</summary>
    public Task<SentMessage> SendAsync(Channel channel, QQOutboundContent content,
        QQReplyContext? reply = null, CancellationToken cancellationToken = default) =>
        SendAsync(QQApiRoutes.Messages(channel), content, reply, cancellationToken: cancellationToken);

    /// <summary>Sends a protocol-level outgoing payload to an explicit route (for channel scenes).</summary>
    public async Task<SentMessage> SendAsync(string route, QQOutboundContent content,
        QQReplyContext? reply = null, bool isChannel = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(content);
        var request = BuildRequest(content, isChannel);
        if (reply is not null) request = ApplyReply(request, reply);
        return await SendMessageAsync(route, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends to a channel-scene target while the protocol layer owns route construction.</summary>
    public Task<SentMessage> SendAsync(QOfficialMessageTarget target, QQOutboundContent content,
        QQReplyContext? reply = null, CancellationToken cancellationToken = default) =>
        SendAsync(MessageRoute(target), content, reply, target.Scene == QOfficialMessageScene.Channel,
            cancellationToken);

    public static string MessageRoute(QOfficialMessageTarget target) => QQApiRoutes.Messages(target);

    public async Task SendTypingAsync(Channel channel, QQReplyContext reply, int seconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (seconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        var request = ApplyReply(new QQSendRequest
        {
            MessageType = 6,
            InputNotify = new QQInputNotify(1, seconds)
        }, reply);
        await SendTypingAsync(channel, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> SendStreamFrameAsync(Channel channel, QQStreamFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var request = new QQStreamRequest
        {
            InputState = frame.State,
            ContentType = frame.ContentType == QOfficialStreamContentType.Markdown ? "markdown" : "text",
            Content = frame.Text,
            MessageId = frame.Reply.MessageId
                ?? throw new ArgumentException("QQ stream frames require a reply message ID.", nameof(frame)),
            EventId = frame.Reply.EventId ?? frame.Reply.MessageId
                ?? throw new ArgumentException("QQ stream frames require a reply event or message ID.", nameof(frame)),
            MessageSequence = frame.Reply.MessageSequence
                ?? throw new ArgumentException("QQ stream frames require a message sequence.", nameof(frame)),
            Index = frame.Index,
            StreamMessageId = frame.StreamMessageId
        };
        return await SendStreamFrameAsync(channel, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> UploadAsync(Channel channel, QOfficialMediaType type, string path, string fileName,
        CancellationToken cancellationToken = default)
    {
        ValidateMediaType(type);
        return UploadLocalAsync(channel, (int)type, path, fileName, cancellationToken);
    }

    public Task<string> UploadAsync(Channel channel, QOfficialMediaType type, Uri uri, string? fileName,
        CancellationToken cancellationToken = default)
    {
        ValidateMediaType(type);
        return UploadRemoteAsync(channel, (int)type, uri, fileName, cancellationToken);
    }

    public Task<SentMessage> SendLocalMediaAsync(Channel channel, QOfficialMediaType type, string path,
        string fileName, CancellationToken cancellationToken = default)
    {
        ValidateMediaType(type);
        return SendLocalAsync(channel, (int)type, path, fileName, cancellationToken);
    }

    public Task<SentMessage> SendRemoteMediaAsync(Channel channel, QOfficialMediaType type, Uri uri,
        string? fileName, CancellationToken cancellationToken = default)
    {
        ValidateMediaType(type);
        return SendRemoteAsync(channel, (int)type, uri, fileName, cancellationToken);
    }

    private static QQSendRequest BuildRequest(QQOutboundContent content, bool isChannel) => content switch
    {
        QQOutboundContent.TextContent text => new QQSendRequest { MessageType = 0, Content = text.Content },
        QQOutboundContent.MarkdownContent markdown => new QQSendRequest
        {
            MessageType = 2,
            Markdown = QQOfficialMessageMapper.MapMarkdown(markdown.Markdown),
            Keyboard = QQOfficialMessageMapper.MapKeyboard(markdown.Keyboard, isChannel)
        },
        QQOutboundContent.CardContent card => new QQSendRequest
        {
            MessageType = 8,
            Card = QQCardMapper.Map(card.Card)
        },
        QQOutboundContent.ArkContent ark => new QQSendRequest
        {
            MessageType = 3,
            Ark = new QQArk(ark.TemplateId,
                ark.Fields.Select(field => new QQArkField(field.Key, field.Value)).ToArray())
        },
        QQOutboundContent.EmbedContent embed => new QQSendRequest
        {
            MessageType = 4,
            Content = string.Empty,
            Embed = new QQEmbed
            {
                Title = embed.Embed.Title,
                Prompt = embed.Embed.Prompt,
                Thumbnail = embed.Embed.Thumbnail is null ? null : new QQEmbedThumbnail(embed.Embed.Thumbnail.Url),
                Fields = embed.Embed.Fields?.Select(field => new QQEmbedField(field.Name)).ToArray()
            }
        },
        QQOutboundContent.MediaContent media => new QQSendRequest
        {
            MessageType = 7,
            Content = media.Content,
            Media = new QQMedia(media.FileInfo)
        },
        _ => throw new NotSupportedException($"Unsupported QQ outgoing content: {content.GetType().Name}.")
    };

    private static QQSendRequest ApplyReply(QQSendRequest request, QQReplyContext reply)
    {
        var hasMessage = !string.IsNullOrWhiteSpace(reply.MessageId);
        var hasEvent = !string.IsNullOrWhiteSpace(reply.EventId);
        if (hasMessage == hasEvent || reply.MessageSequence is <= 0)
            throw new ArgumentException(
                "QQ reply needs exactly one message or event ID and a positive sequence.", nameof(reply));
        request = hasMessage
            ? request with { MessageId = reply.MessageId, MessageSequence = reply.MessageSequence }
            : request with { EventId = reply.EventId, MessageSequence = reply.MessageSequence };
        return reply.ReferenceIndex is { Length: > 0 } index
            ? request with { MessageReference = new QQMessageReference(index) }
            : request;
    }

    private static void ValidateMediaType(QOfficialMediaType type)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
    }

    public async Task SendTypingAsync(Channel channel, QQSendRequest message, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Post, QQApiRoutes.Messages(channel), message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> SendStreamFrameAsync(Channel channel, QQStreamRequest frame, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Post, QQApiRoutes.StreamMessages(channel), frame, cancellationToken).ConfigureAwait(false);
        var sent = await response.Content.ReadFromJsonAsync<QQSendResponse>(QQJson.Options, cancellationToken).ConfigureAwait(false);
        return sent?.Id;
    }

    public async Task<string> UploadRemoteAsync(Channel channel, int fileType, Uri uri,
        string? fileName = null, CancellationToken cancellationToken = default)
    {
        var cacheKey = QQUploadCache.Key(channel, fileType, $"{uri.AbsoluteUri}|{fileName}");
        if (_uploads.Get(cacheKey) is { } cached) return cached;
        var uploaded = await PostRemoteAsync(channel, fileType, uri, false, fileName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uploaded?.FileInfo))
            throw new InvalidDataException("QQ media upload response contains no file_info.");
        _uploads.Set(cacheKey, uploaded.FileInfo, uploaded.Ttl);
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
        // Path + size + mtime identifies the content well enough without reading the file a second time.
        var info = new FileInfo(path);
        var cacheKey = QQUploadCache.Key(channel, fileType,
            $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{fileName}");
        if (_uploads.Get(cacheKey) is { } cached) return cached;
        var uploaded = await _chunked.UploadAsync(channel, fileType, path, fileName, false, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uploaded.FileInfo))
            throw new InvalidDataException("QQ local upload response contains no file_info.");
        _uploads.Set(cacheKey, uploaded.FileInfo, uploaded.Ttl);
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
        return await response.Content.ReadFromJsonAsync<QQUploadResponse>(QQJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("QQ media upload response is empty.");
    }

    public async Task<string?> GetGroupNameAsync(string groupOpenId, CancellationToken cancellationToken = default) =>
        (await GetGroupInfoAsync(groupOpenId, cancellationToken).ConfigureAwait(false)).GroupName;

    public async Task<QQGroupInfo> GetGroupInfoAsync(string groupOpenId, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Get, QQApiRoutes.GroupInfo(groupOpenId), null, cancellationToken).ConfigureAwait(false);
        var info = await response.Content.ReadFromJsonAsync<QQGroupInfo>(QQJson.Options, cancellationToken).ConfigureAwait(false);
        if (info is null || !string.Equals(info.GroupOpenId, groupOpenId, StringComparison.Ordinal))
            throw new InvalidDataException("QQ group info response has a mismatched group_openid.");
        return info;
    }

    public async Task<QQGroupMemberPage> GetGroupMembersAsync(string groupOpenId, string? cursor,
        CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Get, QQApiRoutes.GroupMembers(groupOpenId, cursor), null, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<QQGroupMemberPage>(QQJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("QQ group member list response is empty.");
    }

    public async Task<QQUser> GetGroupMemberAsync(string groupOpenId, string memberOpenId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Get, QQApiRoutes.GroupMember(groupOpenId, memberOpenId), null, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<QQUser>(QQJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("QQ group member response is empty.");
    }

    public async Task SetMemberMuteAsync(string groupOpenId, QQMemberMuteState state,
        CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Post, QQApiRoutes.GroupRestrictChat(groupOpenId),
            new QQMuteRequest([state]), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveGroupMembersAsync(string groupOpenId, IReadOnlyList<string> memberOpenIds,
        bool addToBlacklist, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Post, QQApiRoutes.GroupRemoveMembers(groupOpenId),
            new QQRemoveMembersRequest(memberOpenIds, addToBlacklist), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Applies QQ's mute protocol: a zero duration clears the mute, and an already muted member needs
    ///     the "update" operation instead of "add".
    /// </summary>
    public async Task MuteMemberAsync(string groupOpenId, string memberOpenId, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupOpenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberOpenId);
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(duration), "QQ mutes last from 0 (unmute) up to 30 days.");
        if (duration == TimeSpan.Zero)
        {
            await SetMemberMuteAsync(groupOpenId, new QQMemberMuteState("del", memberOpenId, string.Empty),
                cancellationToken).ConfigureAwait(false);
            return;
        }
        var expireAt = DateTimeOffset.Now.Add(duration)
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await SetMemberMuteAsync(groupOpenId,
                new QQMemberMuteState("add", memberOpenId, expireAt), cancellationToken).ConfigureAwait(false);
        }
        catch (QQApiException addError) when (addError.StatusCode is not null
            && (int)addError.StatusCode.Value < 500)
        {
            // Someone who is already muted needs "update"; if that fails too, the first error is the useful one.
            try
            {
                await SetMemberMuteAsync(groupOpenId,
                    new QQMemberMuteState("update", memberOpenId, expireAt), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (QQApiException)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(addError).Throw();
            }
        }
    }

    public async Task<QQGroupBlacklistPage> GetGroupBlacklistAsync(string groupOpenId, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        var route = QQApiRoutes.GroupBlacklist(groupOpenId) + $"?limit={limit}"
            + (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        using var response = await _transport.SendAsync(HttpMethod.Get, route, null, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<QQGroupBlacklistPage>(QQJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("QQ group blacklist response is empty.");
    }

    public async Task<IReadOnlyList<string>> UpdateGroupBlacklistAsync(string groupOpenId,
        IReadOnlyList<string> memberOpenIds, bool add, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Post, QQApiRoutes.GroupBlacklist(groupOpenId),
            new QQGroupBlacklistRequest(add ? "add" : "del", memberOpenIds), cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<QQGroupBlacklistResponse>(QQJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("QQ group blacklist response is empty.");
        return result.FailOpenIds ?? [];
    }

    public async Task<QQGroupBotState> GetGroupBotStateAsync(string groupOpenId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Get, QQApiRoutes.GroupBotState(groupOpenId), null, cancellationToken).ConfigureAwait(false);
        var state = await response.Content.ReadFromJsonAsync<QQGroupBotState>(QQJson.Options, cancellationToken).ConfigureAwait(false);
        return state is null || string.IsNullOrEmpty(state.MemberOpenId)
            ? throw new InvalidDataException("QQ bot state response has no member_openid.")
            : state;
    }

    public async Task AcknowledgeInteractionAsync(string interactionId,
        QOfficialInteractionResponseCode code = QOfficialInteractionResponseCode.Success,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interactionId);
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        cancellationToken.ThrowIfCancellationRequested();
        Task task;
        TaskCompletionSource? completion = null;
        lock (_acknowledged)
        {
            if (!_acknowledged.TryGetValue(interactionId, out task!))
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                task = completion.Task;
                _acknowledged.Add(interactionId, task);
            }
        }
        if (completion is not null) _ = CompleteInteractionAcknowledgementAsync(interactionId, code, completion, cancellationToken);
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteInteractionAcknowledgementAsync(string interactionId, QOfficialInteractionResponseCode code,
        TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _transport.SendAsync(HttpMethod.Put, QQApiRoutes.Interaction(interactionId),
                new System.Text.Json.Nodes.JsonObject { ["code"] = (int)code }, cancellationToken).ConfigureAwait(false);
            lock (_acknowledged)
            {
                _acknowledgementOrder.Enqueue((interactionId, completion.Task));
                while (_acknowledgementOrder.Count > 4096)
                {
                    var old = _acknowledgementOrder.Dequeue();
                    if (_acknowledged.TryGetValue(old.Id, out var current) && ReferenceEquals(current, old.Task)) _acknowledged.Remove(old.Id);
                }
            }
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            // A failed exchange may already have reached QQ. Share that outcome instead of blindly resending.
            lock (_acknowledged)
            {
                _acknowledgementOrder.Enqueue((interactionId, completion.Task));
                while (_acknowledgementOrder.Count > 4096)
                {
                    var old = _acknowledgementOrder.Dequeue();
                    if (_acknowledged.TryGetValue(old.Id, out var current) && ReferenceEquals(current, old.Task)) _acknowledged.Remove(old.Id);
                }
            }
            completion.TrySetException(error);
        }
    }

    internal async Task<JsonElement> SendGroupRequestAsync(HttpMethod method, string route,
        System.Text.Json.Nodes.JsonObject? body, CancellationToken cancellationToken)
    {
        using var response = await _transport.SendAsync(method, route, body, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0) return default;
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    public async Task DeleteMessageAsync(Channel channel, string messageId, CancellationToken cancellationToken = default)
    {
        using var response = await _transport.SendAsync(HttpMethod.Delete,
            QQApiRoutes.Message(channel, messageId), null, cancellationToken).ConfigureAwait(false);
    }
}
