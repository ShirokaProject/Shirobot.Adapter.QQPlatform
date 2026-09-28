using System.Text;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.QQPlatform.Contracts;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

internal sealed class QQMessageService(QQOpenApiClient api, IConsoleLogger? logger = null) : IMessageService, IQQPlatformMessageService
{
    private readonly Dictionary<string, int> _sequences = [];
    private readonly Queue<string> _sequenceOrder = new();
    private readonly Dictionary<string, (string MessageId, DateTimeOffset SeenAt)> _recent = [];
    private readonly Queue<string> _recentOrder = new();

    public void RegisterIncoming(MessageEvent message)
    {
        var key = ChannelKey(message.Channel);
        lock (_recent)
        {
            if (!_recent.ContainsKey(key)) _recentOrder.Enqueue(key);
            _recent[key] = (message.MessageId, DateTimeOffset.UtcNow);
            while (_recentOrder.Count > 4096) _recent.Remove(_recentOrder.Dequeue());
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
        ResourceSegment? resource = null;
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case QuoteSegment: break;
                case TextSegment text: content.Append(text.Text); break;
                case MentionSegment mention: content.Append('@').Append(mention.DisplayName ?? mention.UserId).Append(' '); break;
                case MentionAllSegment: content.Append("@全体成员 "); break;
                case EmojiSegment emoji: content.Append(emoji.Name ?? $":{emoji.Id}:"); break;
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
            request = new QQSendRequest { MessageType = 0, Content = content.ToString() };
        }
        return await SendAndLogAsync(channel, WithReply(request, replyToMessageId),
            resource is null ? content.ToString() : "[图片]").ConfigureAwait(false);
    }

    public Task DeleteMessageAsync(Channel channel, string messageId) => api.DeleteMessageAsync(channel, messageId);

    public Task<SentMessage> SendMarkdownAsync(Channel channel, string markdown, string replyToMessageId)
    {
        return SendRichMarkdownAsync(channel, new QQMarkdownMessage(markdown), QQResponseReference.ForMessage(replyToMessageId));
    }

    public Task<SentMessage> SendTextAsync(Channel channel, string text, QQResponseReference response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return SendAndLogAsync(channel, WithResponse(new QQSendRequest
        {
            MessageType = 0, Content = text
        }, response), text);
    }

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

    public IQQPlatformMessageStream BeginStream(Channel channel, string replyToMessageId,
        QQStreamContentType contentType = QQStreamContentType.Text)
    {
        if (channel.Type != ChannelType.Direct)
            throw new NotSupportedException("QQ Official stream messages support C2C only.");
        if (!Enum.IsDefined(contentType)) throw new ArgumentOutOfRangeException(nameof(contentType));
        var reply = WithReply(new QQSendRequest(), replyToMessageId);
        return new QQPlatformMessageStream(api, channel, replyToMessageId, reply.MessageSequence!.Value,
            contentType, logger);
    }

    public Task<SentMessage> SendRichMarkdownAsync(Channel channel, QQMarkdownMessage message, QQResponseReference response)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Content);
        ArgumentNullException.ThrowIfNull(response);
        var request = new QQSendRequest
        {
            MessageType = 2,
            Markdown = new QQMarkdown(message.Content),
            Keyboard = message.Keyboard is null ? null : QQKeyboardMapper.Map(message.Keyboard)
        };
        request = WithResponse(request, response);
        return SendAndLogAsync(channel, request, $"[Markdown] {message.Content}");
    }

    private QQSendRequest WithResponse(QQSendRequest request, QQResponseReference response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(response.MessageId) == string.IsNullOrWhiteSpace(response.EventId))
            throw new ArgumentException("Exactly one message ID or event ID is required.", nameof(response));
        return !string.IsNullOrWhiteSpace(response.MessageId)
            ? WithReply(request, response.MessageId)
            : request with { EventId = response.EventId };
    }

    public Task<SentMessage> SendArkAsync(Channel channel, int templateId, IReadOnlyDictionary<string, string> fields, string replyToMessageId)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (templateId <= 0) throw new ArgumentOutOfRangeException(nameof(templateId));
        var values = fields.Select(field => new QQArkField(field.Key, field.Value)).ToArray();
        return SendAndLogAsync(channel, WithReply(new QQSendRequest
        {
            MessageType = 3, Ark = new QQArk(templateId, values)
        }, replyToMessageId), $"[Ark 模板 {templateId}]");
    }

    private async Task<SentMessage> SendAndLogAsync(Channel channel, QQSendRequest request, string description)
    {
        var sent = await api.SendMessageAsync(channel, request).ConfigureAwait(false);
        var target = string.IsNullOrWhiteSpace(channel.Name) ? channel.Id : $"{channel.Name}({channel.Id})";
        var kind = channel.Type == ChannelType.Group ? "群消息" : "私聊消息";
        logger?.Info($"已发送{kind}到 {target}: {description.Replace("\r", "\\r").Replace("\n", "\\n")}");
        return sent;
    }

    private QQSendRequest WithReply(QQSendRequest request, string replyToMessageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyToMessageId);
        lock (_sequences)
        {
            var sequence = _sequences.TryGetValue(replyToMessageId, out var previous) ? checked(previous + 1) : 1;
            if (sequence == 1) _sequenceOrder.Enqueue(replyToMessageId);
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
}
