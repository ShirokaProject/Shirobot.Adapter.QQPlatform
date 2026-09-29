using System.Text;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

internal sealed class QQMessageService(QQOpenApiClient api, IConsoleLogger? logger = null) : IMessageService
{
    private readonly Dictionary<string, int> _sequences = [];
    private readonly Queue<string> _sequenceOrder = new();
    private readonly Dictionary<string, (string MessageId, DateTimeOffset SeenAt)> _recent = [];
    private readonly Queue<string> _recentOrder = new();
    private readonly Dictionary<(ChannelType Type, string ChannelId, string MessageId), string> _referenceIndexes = [];
    private readonly Queue<(ChannelType Type, string ChannelId, string MessageId)> _referenceOrder = new();

    public void RegisterIncoming(MessageEvent message)
    {
        var key = ChannelKey(message.Channel);
        lock (_recent)
        {
            if (!_recent.ContainsKey(key)) _recentOrder.Enqueue(key);
            _recent[key] = (message.MessageId, DateTimeOffset.UtcNow);
            while (_recentOrder.Count > 4096) _recent.Remove(_recentOrder.Dequeue());
        }
        if (message.Raw is not JsonElement { ValueKind: JsonValueKind.Object } raw
            || !raw.TryGetProperty("message_scene", out var scene)
            || scene.ValueKind != JsonValueKind.Object
            || !scene.TryGetProperty("ext", out var extensions)
            || extensions.ValueKind != JsonValueKind.Array) return;
        foreach (var extension in extensions.EnumerateArray())
        {
            if (extension.ValueKind != JsonValueKind.String
                || extension.GetString() is not { } value
                || !value.StartsWith("msg_idx=", StringComparison.Ordinal)
                || value.Length <= "msg_idx=".Length) continue;
            var referenceKey = (message.Channel.Type, message.Channel.Id, message.MessageId);
            lock (_referenceIndexes)
            {
                if (!_referenceIndexes.ContainsKey(referenceKey)) _referenceOrder.Enqueue(referenceKey);
                _referenceIndexes[referenceKey] = value["msg_idx=".Length..];
                while (_referenceOrder.Count > 4096) _referenceIndexes.Remove(_referenceOrder.Dequeue());
            }
            break;
        }
    }

    public async Task<SentMessage> SendMessageAsync(Channel channel, IReadOnlyList<MessageSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var quotes = segments.OfType<QuoteSegment>().ToArray();
        if (quotes.Length > 1 || quotes.Length == 1 && string.IsNullOrWhiteSpace(quotes[0].MessageId))
            throw new ArgumentException("QQ Official replies accept at most one valid QuoteSegment.", nameof(segments));
        var replyToMessageId = quotes.Length == 1 ? quotes[0].MessageId : RecentMessageId(channel);

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
                case ImageSegment image when resource is null: resource = image; break;
                case ResourceSegment: throw new NotSupportedException("QQ Official supports one remote image per message in this adapter version.");
                default: throw new NotSupportedException($"QQ Official cannot send segment {segment.GetType().Name}.");
            }
        }

        QQSendRequest request;
        if (resource is ImageSegment imageSegment)
        {
            if (content.Length > 0)
                throw new NotSupportedException("QQ Official image and text must be sent as separate replies.");
            if (!Uri.TryCreate(imageSegment.Uri, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                throw new NotSupportedException("QQ Official image upload currently requires an HTTP(S) URL.");
            var fileInfo = await api.UploadRemoteAsync(channel, 1, url).ConfigureAwait(false);
            request = new QQSendRequest { MessageType = 7, Content = " ", Media = new QQMedia(fileInfo) };
        }
        else
        {
            if (content.Length == 0) throw new ArgumentException("Message has no sendable content.", nameof(segments));
            request = hasNativeMention
                ? new QQSendRequest { MessageType = 2, Markdown = new QQMarkdown { Content = content.ToString() } }
                : new QQSendRequest { MessageType = 0, Content = content.ToString() };
        }
        if (quotes.Length == 1 && GetReferenceIndex(channel, quotes[0].MessageId) is { } referenceIndex)
            request = request with { MessageReference = new QQMessageReference(referenceIndex) };
        return await SendAndLogAsync(channel, WithReply(request, replyToMessageId),
            resource is null ? displayContent.ToString() : "[图片]").ConfigureAwait(false);
    }

    public Task DeleteMessageAsync(Channel channel, string messageId) => api.DeleteMessageAsync(channel, messageId);

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
        QOfficialMessageReply? reply, string description)
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
        return SendAndLogAsync(channel, request, description);
    }

    private async Task<SentMessage> SendAndLogAsync(Channel channel, QQSendRequest request, string description)
    {
        var sent = await api.SendMessageAsync(channel, request).ConfigureAwait(false);
        var target = string.IsNullOrWhiteSpace(channel.Name) ? channel.Id : channel.Name;
        var kind = channel.Type == ChannelType.Group ? "群消息" : "私聊消息";
        logger?.Info($"已发送{kind}到 {target}: {description.Replace("\r", "\\r").Replace("\n", "\\n")}");
        return sent;
    }

    private QQSendRequest WithReply(QQSendRequest request, string replyToMessageId, int? explicitSequence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyToMessageId);
        lock (_sequences)
        {
            var hasPrevious = _sequences.TryGetValue(replyToMessageId, out var previous);
            var sequence = explicitSequence ?? (hasPrevious ? checked(previous + 1) : 1);
            if (hasPrevious && sequence <= previous)
                throw new ArgumentException("QQ reply sequence must increase for each source message.", nameof(explicitSequence));
            if (!hasPrevious) _sequenceOrder.Enqueue(replyToMessageId);
            _sequences[replyToMessageId] = sequence;
            while (_sequenceOrder.Count > 4096) _sequences.Remove(_sequenceOrder.Dequeue());
            return request with { MessageId = replyToMessageId, MessageSequence = sequence };
        }
    }

    private string RecentMessageId(Channel channel)
    {
        lock (_recent)
        {
            if (_recent.TryGetValue(ChannelKey(channel), out var entry)
                && DateTimeOffset.UtcNow - entry.SeenAt < TimeSpan.FromMinutes(5)) return entry.MessageId;
        }
        throw new InvalidOperationException("QQ Official requires a recent incoming message in this channel or an explicit QuoteSegment.");
    }

    private static string ChannelKey(Channel channel) => $"{(int)channel.Type}:{channel.Id}";

    private string? GetReferenceIndex(Channel channel, string messageId)
    {
        lock (_referenceIndexes)
            return _referenceIndexes.GetValueOrDefault((channel.Type, channel.Id, messageId));
    }

    private static bool IsSafeOpenId(string userId) =>
        userId is { Length: > 0 and <= 128 } && userId.All(c => c is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}
