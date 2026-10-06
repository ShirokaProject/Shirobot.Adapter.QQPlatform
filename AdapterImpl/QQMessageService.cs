using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

internal sealed class QQMessageService(
    QQOpenApiClient api,
    IConsoleLogger? logger = null,
    Func<User?>? getSelfUser = null) : IMessageService
{
    private readonly Dictionary<string, int> _sequences = [];
    private readonly Queue<string> _sequenceOrder = new();
    private readonly Dictionary<string, (string MessageId, DateTimeOffset SeenAt)> _recent = [];
    private readonly Queue<string> _recentOrder = new();
    private readonly Dictionary<string, DateTimeOffset> _arrivals = [];
    private readonly Queue<string> _arrivalOrder = new();
    private readonly QQReplyTracker _replies = new();

    public MessageEvent RegisterIncoming(MessageEvent message)
    {
        var key = ChannelKey(message.Channel);
        lock (_recent)
        {
            if (!_recent.ContainsKey(key)) _recentOrder.Enqueue(key);
            _recent[key] = (message.MessageId, DateTimeOffset.UtcNow);
            while (_recentOrder.Count > 4096) _recent.Remove(_recentOrder.Dequeue());
        }
        lock (_sequences)
        {
            if (_arrivals.TryAdd(message.MessageId, DateTimeOffset.UtcNow)) _arrivalOrder.Enqueue(message.MessageId);
            while (_arrivalOrder.Count > 4096) _arrivals.Remove(_arrivalOrder.Dequeue());
        }
        var resolution = _replies.Resolve(message);
        if (resolution.Chain.Count > 0)
        {
            var preview = QQReplyTracker.Preview(resolution.Chain[0]);
            // Plugins match replies by message ID (see reply subscriptions), not by QQ's REFIDX_ value; only a
            // message found in the cache is known by its own ID.
            var knownId = resolution.Source is "index" or "content-match" ? resolution.Chain[0].MessageId : null;
            message = message with
            {
                Segments = message.Segments.Select(segment => segment is QuoteSegment quoteSegment
                    ? quoteSegment with { MessageId = knownId ?? quoteSegment.MessageId, Preview = preview }
                    : segment).ToArray()
            };
        }
        var resolvedRaw = AddResolvedReplyChain(message.Raw, resolution.Chain);
        var enriched = resolvedRaw is { } rawWithChain ? message with { Raw = rawWithChain } : message;
        _replies.Remember(enriched, QQReplyTracker.OwnIndexOf(message), resolution.Chain);
        if (message.GetQuote() is { } quote)
        {
            var entries = string.Join(" <- ", resolution.Chain.Select(item =>
                $"{item.Sender.Name ?? item.Sender.Id}:{string.Join('+', item.Segments.Select(segment => segment.GetType().Name))}"));
            logger?.Info($"QQ Official reply chain: source={resolution.Source}, referenceKey={QQReplyTracker.Fingerprint(quote.MessageId)}, " +
                $"depth={resolution.Chain.Count}, messages=[{entries}], {resolution.Detail}" +
                (resolution.Chain.Count == 0 ? $"; recentCache=[{_replies.DescribeRecent(message.Channel, message.MessageId)}]" : ""));
        }
        return enriched;
    }

    public async Task<SentMessage> SendMessageAsync(Channel channel, IReadOnlyList<MessageSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var quotes = segments.OfType<QuoteSegment>().ToArray();
        if (quotes.Length > 1 || quotes.Length == 1 && string.IsNullOrWhiteSpace(quotes[0].MessageId))
            throw new ArgumentException("QQ Official replies accept at most one valid QuoteSegment.", nameof(segments));
        // A quote normally names a message ID; one that could not be resolved on receipt keeps QQ's raw REFIDX_ value.
        var quoteIsIndex = quotes.Length == 1 && quotes[0].MessageId.StartsWith("REFIDX_", StringComparison.Ordinal);
        var replyToMessageId = quotes.Length == 1 && !quoteIsIndex ? quotes[0].MessageId : TryRecentMessageId(channel);
        string? QuoteReferenceIndex() => quotes.Length != 1 ? null
            : quoteIsIndex ? quotes[0].MessageId : GetReferenceIndex(channel, quotes[0].MessageId);
        // Once the reply window or reply count is used up, the message goes out as a proactive one.
        if (replyToMessageId is not null && !CanReplyPassively(channel, replyToMessageId)) replyToMessageId = null;

        var content = new StringBuilder();
        var displayContent = new StringBuilder();
        var hasNativeMention = false;
        ResourceSegment? resource = null;
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case QuoteSegment: break;
                case TextSegment text:
                    content.Append(text.Text);
                    displayContent.Append(text.Text);
                    break;
                case MentionSegment mention when channel.Type == ChannelType.Group && IsSafeOpenId(mention.UserId):
                    content.Append("<qqbot-at-user id=\"").Append(mention.UserId).Append("\" /> ");
                    displayContent.Append('@').Append(mention.DisplayName ?? "用户").Append(' ');
                    hasNativeMention = true;
                    break;
                case MentionSegment mention:
                    content.Append('@').Append(mention.DisplayName ?? "用户").Append(' ');
                    displayContent.Append('@').Append(mention.DisplayName ?? "用户").Append(' ');
                    break;
                case MentionAllSegment:
                    content.Append("@全体成员 ");
                    displayContent.Append("@全体成员 ");
                    break;
                case EmojiSegment emoji:
                    var emojiText = emoji.Name ?? $":{emoji.Id}:";
                    content.Append(emojiText);
                    displayContent.Append(emojiText);
                    break;
                case ResourceSegment media when resource is null: resource = media; break;
                case ResourceSegment: throw new NotSupportedException("QQ Official supports one media resource per message.");
                default: throw new NotSupportedException($"QQ Official cannot send segment {segment.GetType().Name}.");
            }
        }

        QQSendRequest request;
        if (resource is not null)
        {
            using var source = await QQMediaSource.ResolveAsync(resource).ConfigureAwait(false);
            var url = source.Url;
            var path = source.Path;
            var fileType = resource switch
            {
                ImageSegment => 1,
                VideoSegment => 2,
                AudioSegment => 3,
                FileSegment => 4,
                _ => throw new NotSupportedException($"QQ Official cannot send resource {resource.GetType().Name}.")
            };
            var fileName = source.FileName;
            if (channel.Type == ChannelType.Group && replyToMessageId is null && content.Length == 0)
            {
                var sent = path is not null
                    ? await api.SendLocalAsync(channel, fileType, path, fileName).ConfigureAwait(false)
                    : await api.SendRemoteAsync(channel, fileType, url!,
                        resource is FileSegment ? fileName : null).ConfigureAwait(false);
                RememberSentMessage(channel, sent, replyToMessageId, segments);
                var target = string.IsNullOrWhiteSpace(channel.Name) ? channel.Id : channel.Name;
                logger?.Info($"已发送群消息到 {target}: [{resource.GetType().Name}]");
                return sent;
            }
            var fileInfo = path is not null
                ? await api.UploadLocalAsync(channel, fileType, path, fileName).ConfigureAwait(false)
                : await api.UploadRemoteAsync(channel, fileType, url!,
                    resource is FileSegment ? fileName : null).ConfigureAwait(false);
            request = new QQSendRequest
            {
                MessageType = 7,
                Content = content.Length > 0 ? content.ToString() : null,
                Media = new QQMedia(fileInfo)
            };
        }
        else
        {
            if (content.Length == 0) throw new ArgumentException("Message has no sendable content.", nameof(segments));
            if (hasNativeMention)
                request = new QQSendRequest { MessageType = 2, Markdown = new QQMarkdown { Content = content.ToString() } };
            else
            {
                var chunks = QQTextChunker.Split(content.ToString(), QQTextChunker.MaxLength);
                var referenced = QuoteReferenceIndex() is { } index ? new QQMessageReference(index) : null;
                SentMessage lastSent = null!;
                for (var i = 0; i < chunks.Count; i++)
                    lastSent = await SendOneAsync(channel, new QQSendRequest
                    {
                        MessageType = 0, Content = chunks[i], MessageReference = i == 0 ? referenced : null
                    }, replyToMessageId, chunks.Count == 1 ? displayContent.ToString() : chunks[i], segments).ConfigureAwait(false);
                return lastSent;
            }
        }
        if (QuoteReferenceIndex() is { } referenceIndex)
            request = request with { MessageReference = new QQMessageReference(referenceIndex) };
        return await SendOneAsync(channel, request, replyToMessageId,
            resource is null ? displayContent.ToString() : $"[{resource.GetType().Name}]", segments).ConfigureAwait(false);
    }

    /// <summary>Sends passively while the source message still allows it, otherwise (or when QQ says it no longer does) proactively.</summary>
    private async Task<SentMessage> SendOneAsync(Channel channel, QQSendRequest request, string? replyToMessageId,
        string description, IReadOnlyList<MessageSegment> segments)
    {
        if (replyToMessageId is not null && !CanReplyPassively(channel, replyToMessageId)) replyToMessageId = null;
        if (replyToMessageId is null) return await SendAndLogAsync(channel, request, description, segments).ConfigureAwait(false);
        try
        {
            return await SendAndLogAsync(channel, WithReply(request, replyToMessageId), description, segments).ConfigureAwait(false);
        }
        catch (QQApiException ex) when (ex.IsPassiveReplyExpired)
        {
            logger?.Warning($"QQ Official passive reply was refused (err_code {ex.ErrorCode}); sending as a proactive message.");
            return await SendAndLogAsync(channel, request, description, segments).ConfigureAwait(false);
        }
    }

    public Task DeleteMessageAsync(Channel channel, string messageId) => api.DeleteMessageAsync(channel, messageId);

    /// <summary>QQ has no endpoint to fetch a message, so only recently seen or sent messages can be returned.</summary>
    public Task<MessageEvent?> GetMessageAsync(Channel channel, string messageId) =>
        Task.FromResult(_replies.Find(channel, messageId));

    public async Task SendTypingAsync(Channel channel, string replyToMessageId, TimeSpan duration)
    {
        if (channel.Type != ChannelType.Direct)
            throw new NotSupportedException("QQ Official typing status supports C2C only.");
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(duration), "Typing duration must be between 1 and 60 seconds.");
        var seconds = (int)Math.Ceiling(duration.TotalSeconds);
        var request = WithReply(new QQSendRequest
        {
            MessageType = 6, InputNotify = new QQInputNotify(1, seconds)
        }, replyToMessageId);
        await api.SendTypingAsync(channel, request).ConfigureAwait(false);
        logger?.Info($"已发送私聊输入状态到 {channel.Id}: {seconds} 秒");
    }

    internal IQOfficialMessageStream BeginOfficialStream(Channel channel, string replyToMessageId,
        QOfficialStreamContentType contentType = QOfficialStreamContentType.Text) =>
        BeginStreamCore(channel, replyToMessageId, contentType);

    private QQPlatformMessageStream BeginStreamCore(Channel channel, string replyToMessageId,
        QOfficialStreamContentType contentType)
    {
        if (channel.Type != ChannelType.Direct)
            throw new NotSupportedException("QQ Official stream messages support C2C only.");
        if (!Enum.IsDefined(contentType)) throw new ArgumentOutOfRangeException(nameof(contentType));
        var reply = WithReply(new QQSendRequest(), replyToMessageId);
        return new QQPlatformMessageStream(api, channel, replyToMessageId, reply.MessageSequence!.Value,
            contentType, logger);
    }

    internal Task<SentMessage> SendOfficialAsync(Channel channel, QQSendRequest request,
        QOfficialMessageReply? reply, string description, CancellationToken cancellationToken = default)
    {
        if (reply is not null)
        {
            var hasMessage = !string.IsNullOrWhiteSpace(reply.MessageId);
            var hasEvent = !string.IsNullOrWhiteSpace(reply.EventId);
            if (hasMessage && hasEvent || reply.MessageSequence is <= 0
                || !hasMessage && !hasEvent && reply.MessageSequence is not null)
                throw new ArgumentException("QQ official reply needs one message or event ID and a positive sequence.", nameof(reply));
            if (hasMessage) request = WithReply(request, reply.MessageId!, reply.MessageSequence);
            else if (hasEvent) request = request with
            {
                EventId = reply.EventId,
                MessageSequence = reply.MessageSequence
            };
        }
        return SendOfficialAndRememberAsync(channel, request, reply, description, cancellationToken);
    }

    private async Task<SentMessage> SendOfficialAndRememberAsync(Channel channel, QQSendRequest request,
        QOfficialMessageReply? reply, string description, CancellationToken cancellationToken = default)
    {
        var sent = await SendAndLogAsync(channel, request, description, cancellationToken: cancellationToken).ConfigureAwait(false);
        RememberSentMessage(channel, sent, request.MessageId, GetReplyContextSegments(request));
        return sent;
    }

    internal async Task<SentMessage> SendOfficialAsync(string route, string targetName, QQSendRequest request,
        QOfficialMessageReply? reply, string description, CancellationToken cancellationToken = default)
    {
        if (reply is not null)
        {
            var hasMessage = !string.IsNullOrWhiteSpace(reply.MessageId);
            var hasEvent = !string.IsNullOrWhiteSpace(reply.EventId);
            if (hasMessage == hasEvent || reply.MessageSequence is <= 0
                || !hasMessage && reply.MessageSequence is not null)
                throw new ArgumentException("QQ official reply needs one message or event ID and a positive sequence.", nameof(reply));
            if (hasMessage) request = WithReply(request, $"{route}:{reply.MessageId}", reply.MessageId!, reply.MessageSequence);
            else request = request with { EventId = reply.EventId, MessageSequence = reply.MessageSequence };
        }
        var sent = await api.SendMessageAsync(route, request, cancellationToken).ConfigureAwait(false);
        logger?.Info($"已发送 QQ 官方消息到 {targetName}: {description.Replace("\r", "\\r").Replace("\n", "\\n")}");
        return sent;
    }

    private async Task<SentMessage> SendAndLogAsync(Channel channel, QQSendRequest request, string description,
        IReadOnlyList<MessageSegment>? sentSegments = null, CancellationToken cancellationToken = default)
    {
        var sent = await api.SendMessageAsync(channel, request, cancellationToken).ConfigureAwait(false);
        if (sentSegments is not null)
            RememberSentMessage(channel, sent, request.MessageId, sentSegments);
        var target = string.IsNullOrWhiteSpace(channel.Name) ? channel.Id : channel.Name;
        var kind = channel.Type == ChannelType.Group ? "群消息" : "私聊消息";
        logger?.Info($"已发送{kind}到 {target}: {description.Replace("\r", "\\r").Replace("\n", "\\n")}");
        return sent;
    }

    private void RememberSentMessage(Channel channel, SentMessage sent, string? replyToMessageId,
        IReadOnlyList<MessageSegment> segments)
    {
        var chain = string.IsNullOrWhiteSpace(replyToMessageId) ? null : _replies.ChainOf(channel, replyToMessageId);
        var self = getSelfUser?.Invoke();
        var snapshot = new MessageEvent
        {
            Platform = QQEventTranslator.Platform,
            SelfId = self?.Id,
            MessageId = sent.MessageId,
            Channel = channel,
            Sender = self ?? new User("qq-official-bot") { Name = "机器人", IsBot = true },
            Segments = segments.Where(segment => segment is not QuoteSegment).Select(SnapshotSegment).ToArray(),
            Timestamp = sent.Timestamp ?? DateTimeOffset.UtcNow,
        };
        _replies.Remember(snapshot, api.TakeSentReferenceIndex(sent.MessageId), chain);
    }

    private static MessageSegment SnapshotSegment(MessageSegment segment) =>
        segment is ResourceSegment resource && resource.Uri.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)
            ? resource with { Uri = "base64:[内容已省略]" }
            : segment;

    private static IReadOnlyList<MessageSegment> GetReplyContextSegments(QQSendRequest request)
    {
        if (request.MessageType == 0 && !string.IsNullOrEmpty(request.Content))
            return [new TextSegment(request.Content)];
        if (request.MessageType == 2 && !string.IsNullOrEmpty(request.Markdown?.Content))
            return [new TextSegment(request.Markdown.Content)];
        return [new RawSegment(QQEventTranslator.Platform, "sent_message",
            JsonSerializer.SerializeToElement(request, QQJson.Options))];
    }

    private static JsonElement? AddResolvedReplyChain(object? raw, IReadOnlyList<QQQuotedMessage> chain)
    {
        if (raw is not JsonElement { ValueKind: JsonValueKind.Object } rawEvent || chain.Count == 0) return null;
        var document = JsonNode.Parse(rawEvent.GetRawText())?.AsObject();
        if (document is null) return null;
        document["resolved_reply_chain"] = new JsonArray(chain.Select(item => (JsonNode)new JsonObject
        {
            ["messageId"] = item.MessageId,
            ["sender"] = new JsonObject { ["id"] = item.Sender.Id, ["name"] = item.Sender.Name, ["isBot"] = item.Sender.IsBot },
            ["timestamp"] = item.Timestamp,
            ["segments"] = new JsonArray(item.Segments.Select(ToRawSegment).ToArray())
        }).ToArray());
        using var enriched = JsonDocument.Parse(document.ToJsonString());
        return enriched.RootElement.Clone();
    }

    private static JsonNode ToRawSegment(MessageSegment segment) => segment switch
    {
        TextSegment text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
        ImageSegment image => new JsonObject { ["type"] = "image", ["url"] = image.Uri, ["filename"] = image.FileName },
        VideoSegment video => new JsonObject { ["type"] = "video", ["url"] = video.Uri, ["filename"] = video.FileName },
        AudioSegment audio => new JsonObject { ["type"] = "audio", ["url"] = audio.Uri, ["filename"] = audio.FileName },
        FileSegment file => new JsonObject { ["type"] = "file", ["url"] = file.Uri, ["filename"] = file.FileName },
        EmojiSegment emoji => new JsonObject { ["type"] = "emoji", ["id"] = emoji.Id, ["name"] = emoji.Name },
        MentionSegment mention => new JsonObject { ["type"] = "mention", ["id"] = mention.UserId, ["name"] = mention.DisplayName },
        MentionAllSegment => new JsonObject { ["type"] = "mention_all" },
        _ => new JsonObject { ["type"] = segment.GetType().Name }
    };

    private QQSendRequest WithReply(QQSendRequest request, string replyToMessageId, int? explicitSequence = null)
        => WithReply(request, replyToMessageId, replyToMessageId, explicitSequence);

    private QQSendRequest WithReply(QQSendRequest request, string sequenceKey, string replyToMessageId,
        int? explicitSequence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyToMessageId);
        lock (_sequences)
        {
            var hasPrevious = _sequences.TryGetValue(sequenceKey, out var previous);
            var sequence = explicitSequence ?? (hasPrevious ? checked(previous + 1) : 1);
            if (hasPrevious && sequence <= previous)
                throw new ArgumentException("QQ reply sequence must increase for each source message.", nameof(explicitSequence));
            if (!hasPrevious) _sequenceOrder.Enqueue(sequenceKey);
            _sequences[sequenceKey] = sequence;
            while (_sequenceOrder.Count > 4096) _sequences.Remove(_sequenceOrder.Dequeue());
            return request with { MessageId = replyToMessageId, MessageSequence = sequence };
        }
    }

    // Group: 5 minutes and 5 replies per message; direct chat: 60 minutes and 4 replies (QQ API docs).
    private static (TimeSpan Window, int MaxReplies) PassiveLimits(Channel channel) =>
        channel.Type == ChannelType.Group ? (TimeSpan.FromMinutes(5), 5) : (TimeSpan.FromMinutes(60), 4);

    private bool CanReplyPassively(Channel channel, string messageId)
    {
        var (window, maxReplies) = PassiveLimits(channel);
        lock (_sequences)
        {
            if (_arrivals.TryGetValue(messageId, out var arrivedAt) && DateTimeOffset.UtcNow - arrivedAt >= window)
                return false;
            return !_sequences.TryGetValue(messageId, out var used) || used < maxReplies;
        }
    }

    private string? TryRecentMessageId(Channel channel)
    {
        var window = PassiveLimits(channel).Window;
        lock (_recent)
        {
            if (_recent.TryGetValue(ChannelKey(channel), out var entry)
                && DateTimeOffset.UtcNow - entry.SeenAt < window) return entry.MessageId;
        }
        return null;
    }

    private static string ChannelKey(Channel channel) => $"{(int)channel.Type}:{channel.Id}";

    private string? GetReferenceIndex(Channel channel, string messageId) => _replies.ReferenceIndexOf(channel, messageId);

    private static bool IsSafeOpenId(string userId) =>
        userId is { Length: > 0 and <= 128 } && userId.All(c => c is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}
