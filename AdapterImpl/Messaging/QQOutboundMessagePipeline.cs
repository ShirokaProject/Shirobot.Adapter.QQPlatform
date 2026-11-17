using System.Text;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Official;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
using ShiroBot.Adapter.QQPlatform.Transport;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;

/// <summary>
///     Encodes SDK messages into protocol-level outgoing content, chooses passive or proactive delivery,
///     and records every successful send in the message context.
/// </summary>
internal sealed class QQOutboundMessagePipeline(
    QQOpenApiClient api,
    QQIncomingMessageContext context,
    QQPassiveReplyWindow window,
    IConsoleLogger? logger = null,
    Func<User?>? getSelfUser = null)
{
    public MessageCapabilities GetCapabilities(Channel channel) => QQMessageCapabilities.ForChannel(channel);

    public MessageSendAssessment Assess(Channel channel, OutgoingMessage message) =>
        QQMessageCapabilities.Assess(channel, message);

    public async Task<SentMessage> SendAsync(Channel channel, OutgoingMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = MessagePreparation.Prepare(message, QQMessageCapabilities.ForMessage(channel, message));
        SentMessage result;
        if (prepared.Message.Segments.OfType<CardSegment>().SingleOrDefault() is { } card)
        {
            var quote = prepared.Message.Segments.OfType<QuoteSegment>().SingleOrDefault();
            result = await SendOfficialAsync(channel, new QQOutboundContent.CardContent(card),
                prepared.Message.ReplyToInteraction is { } cardInteraction
                    ? context.ResolveInteractionReply(cardInteraction)
                    : quote is null ? null : new QOfficialMessageReply
                    {
                        MessageId = quote.MessageId,
                        ReferenceIndex = context.ReferenceIndexOf(channel, quote.MessageId)
                    },
                "[图文卡片]", cancellationToken).ConfigureAwait(false);
        }
        else if (prepared.Message.Segments.OfType<MarkdownSegment>().SingleOrDefault() is { } markdown)
        {
            var keyboard = prepared.Message.Buttons is { } layout
                ? new QInlineKeyboard(layout.Rows.Select(row => new QKeyboardRow(
                    row.Buttons.Select(button => new QKeyboardButton
                    {
                        Id = button.Id,
                        RenderData = new(button.Label, button.Label, QKeyboardButtonStyle.Blue),
                        Action = new()
                        {
                            Type = button.Action is OpenUrlAction ? QKeyboardActionType.Jump : QKeyboardActionType.Callback,
                            Data = button.Action is OpenUrlAction link ? link.Url : ((CallbackAction)button.Action).Data,
                            Permission = new() { Type = QKeyboardPermissionType.Everyone },
                            UnsupportTips = "请升级 QQ 客户端"
                        }
                    }).ToArray())).ToArray())
                : null;
            var quote = prepared.Message.Segments.OfType<QuoteSegment>().SingleOrDefault();
            result = await SendOfficialAsync(channel,
                new QQOutboundContent.MarkdownContent(new QCustomMarkdown(markdown.Content), keyboard),
                prepared.Message.ReplyToInteraction is { } interaction
                    ? context.ResolveInteractionReply(interaction)
                    : quote is null ? null : new QOfficialMessageReply
                    {
                        MessageId = quote.MessageId,
                        ReferenceIndex = context.ReferenceIndexOf(channel, quote.MessageId)
                    },
                "[Markdown]", cancellationToken).ConfigureAwait(false);
        }
        else if (prepared.Message.ReplyToInteraction is { } interaction)
        {
            result = await SendOfficialAsync(channel, new QQOutboundContent.TextContent(
                    string.Concat(prepared.Message.Segments.Select(segment => segment switch
                    {
                        TextSegment text => text.Text,
                        MentionAllSegment when channel.Type == ChannelType.Group => "<qqbot-at-everyone /> ",
                        MentionAllSegment => "@全体成员 ",
                        MentionSegment mention when channel.Type == ChannelType.Group && IsSafeOpenId(mention.UserId)
                            => $"<qqbot-at-user id=\"{mention.UserId}\" /> ",
                        MentionSegment mention => $"@{mention.DisplayName ?? "用户"} ",
                        _ => ""
                    }))),
                context.ResolveInteractionReply(interaction), "[Interaction reply]", cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            result = await SendAsync(channel, prepared.Message.Segments, cancellationToken).ConfigureAwait(false);
        }
        return result with { Transformations = prepared.Transformations };
    }

    public async Task<SentMessage> SendAsync(Channel channel, IReadOnlyList<MessageSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        cancellationToken.ThrowIfCancellationRequested();
        if (segments.Any(x => x is MarkdownSegment or CardSegment))
            return await SendAsync(channel, new OutgoingMessage { Segments = segments }, cancellationToken)
                .ConfigureAwait(false);
        var quotes = segments.OfType<QuoteSegment>().ToArray();
        if (quotes.Length > 1 || quotes.Length == 1 && string.IsNullOrWhiteSpace(quotes[0].MessageId))
            throw new ArgumentException("QQ Official replies accept at most one valid QuoteSegment.", nameof(segments));
        // A quote normally names a message ID; one that could not be resolved on receipt keeps QQ's raw REFIDX_ value.
        var quoteIsIndex = quotes.Length == 1 && quotes[0].MessageId.StartsWith("REFIDX_", StringComparison.Ordinal);
        var replyToMessageId = quotes.Length == 1 && !quoteIsIndex ? quotes[0].MessageId : TryRecentMessageId(channel);
        string? QuoteReferenceIndex() => quotes.Length != 1 ? null
            : quoteIsIndex ? quotes[0].MessageId : context.ReferenceIndexOf(channel, quotes[0].MessageId);
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
                case MentionAllSegment when channel.Type == ChannelType.Group:
                    content.Append("<qqbot-at-everyone /> ");
                    displayContent.Append("@全体成员 ");
                    hasNativeMention = true;
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
                case ResourceSegment media when resource is null:
                    resource = media;
                    break;
                case ResourceSegment:
                    throw new NotSupportedException("QQ Official supports one media resource per message.");
                default:
                    throw new NotSupportedException($"QQ Official cannot send segment {segment.GetType().Name}.");
            }
        }

        QQOutboundContent payload;
        if (resource is not null)
        {
            using var source = await QQMediaSource.ResolveAsync(resource).ConfigureAwait(false);
            var url = source.Url;
            var path = source.Path;
            var mediaType = resource switch
            {
                ImageSegment => QOfficialMediaType.Image,
                VideoSegment => QOfficialMediaType.Video,
                AudioSegment => QOfficialMediaType.Audio,
                FileSegment => QOfficialMediaType.File,
                _ => throw new NotSupportedException($"QQ Official cannot send resource {resource.GetType().Name}.")
            };
            var fileName = source.FileName;
            if (channel.Type == ChannelType.Group && replyToMessageId is null && content.Length == 0)
            {
                var sent = path is not null
                    ? await api.SendLocalMediaAsync(channel, mediaType, path, fileName, cancellationToken)
                        .ConfigureAwait(false)
                    : await api.SendRemoteMediaAsync(channel, mediaType, url!,
                        resource is FileSegment ? fileName : null, cancellationToken).ConfigureAwait(false);
                RememberSentMessage(channel, sent, replyToMessageId, segments);
                var target = string.IsNullOrWhiteSpace(channel.Name) ? channel.Id : channel.Name;
                logger?.Info($"已发送群消息到 {target}: [{resource.GetType().Name}]");
                return sent;
            }
            var fileInfo = path is not null
                ? await api.UploadAsync(channel, mediaType, path, fileName, cancellationToken)
                    .ConfigureAwait(false)
                : await api.UploadAsync(channel, mediaType, url!,
                    resource is FileSegment ? fileName : null, cancellationToken).ConfigureAwait(false);
            payload = new QQOutboundContent.MediaContent(fileInfo,
                content.Length > 0 ? content.ToString() : null);
        }
        else
        {
            if (content.Length == 0) throw new ArgumentException("Message has no sendable content.", nameof(segments));
            if (hasNativeMention)
            {
                payload = new QQOutboundContent.MarkdownContent(
                    new QCustomMarkdown(content.ToString()), null);
            }
            else
            {
                var chunks = QQTextChunker.Split(content.ToString(), QQTextChunker.MaxLength);
                var referenceIndex = QuoteReferenceIndex();
                SentMessage lastSent = null!;
                for (var i = 0; i < chunks.Count; i++)
                    lastSent = await SendOneAsync(channel, new QQOutboundContent.TextContent(chunks[i]),
                        replyToMessageId, i == 0 ? referenceIndex : null,
                        chunks.Count == 1 ? displayContent.ToString() : chunks[i], segments, cancellationToken)
                        .ConfigureAwait(false);
                return lastSent;
            }
        }
        return await SendOneAsync(channel, payload, replyToMessageId, QuoteReferenceIndex(),
            resource is null ? displayContent.ToString() : $"[{resource.GetType().Name}]", segments,
            cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteMessageAsync(Channel channel, string messageId, CancellationToken cancellationToken = default) =>
        api.DeleteMessageAsync(channel, messageId, cancellationToken);

    public async Task SendTypingAsync(Channel channel, string replyToMessageId, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (channel.Type != ChannelType.Direct)
            throw new NotSupportedException("QQ Official typing status supports C2C only.");
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(duration), "Typing duration must be between 1 and 60 seconds.");
        var seconds = (int)Math.Ceiling(duration.TotalSeconds);
        var sequence = window.NextSequence(replyToMessageId);
        await api.SendTypingAsync(channel, new QQReplyContext
        {
            MessageId = replyToMessageId,
            MessageSequence = sequence
        }, seconds, cancellationToken).ConfigureAwait(false);
        logger?.Info($"已发送私聊输入状态到 {channel.Id}: {seconds} 秒");
    }

    public IQOfficialMessageStream BeginStream(Channel channel, string replyToMessageId,
        QOfficialStreamContentType contentType = QOfficialStreamContentType.Text)
    {
        if (channel.Type != ChannelType.Direct)
            throw new NotSupportedException("QQ Official stream messages support C2C only.");
        if (!Enum.IsDefined(contentType)) throw new ArgumentOutOfRangeException(nameof(contentType));
        var sequence = window.NextSequence(replyToMessageId);
        return new QQOfficialMessageStream(api, channel, replyToMessageId, sequence, contentType, logger);
    }

    internal Task<SentMessage> SendOfficialAsync(Channel channel, QQOutboundContent content,
        QOfficialMessageReply? reply, string description, CancellationToken cancellationToken = default)
    {
        var replyContext = CreateReplyContext(reply);
        return SendOfficialAndRememberAsync(channel, content, replyContext, description, cancellationToken);
    }

    internal Task<SentMessage> SendOfficialAsync(QOfficialMessageTarget target, QQOutboundContent content,
        QOfficialMessageReply? reply, string description, CancellationToken cancellationToken = default) =>
        SendOfficialAsync(QQOpenApiClient.MessageRoute(target), target.Id, content, reply, description,
            target.Scene == QOfficialMessageScene.Channel, cancellationToken);

    internal async Task<SentMessage> SendOfficialAsync(string route, string targetName,
        QQOutboundContent content, QOfficialMessageReply? reply, string description, bool isChannel = false,
        CancellationToken cancellationToken = default)
    {
        var replyContext = CreateReplyContext(reply);
        var sent = await api.SendAsync(route, content, replyContext, isChannel, cancellationToken)
            .ConfigureAwait(false);
        api.TakeSentReferenceIndex(sent.MessageId);
        logger?.Info($"已发送 QQ 官方消息到 {targetName}: " +
            description.Replace("\r", "\\r").Replace("\n", "\\n"));
        return sent;
    }

    private QQReplyContext? CreateReplyContext(QOfficialMessageReply? reply)
    {
        if (reply is null) return null;
        var hasMessage = !string.IsNullOrWhiteSpace(reply.MessageId);
        var hasEvent = !string.IsNullOrWhiteSpace(reply.EventId);
        if (!hasMessage && !hasEvent)
        {
            if (reply.MessageSequence is not null)
                throw new ArgumentException(
                    "QQ official reply needs one message or event ID and a positive sequence.", nameof(reply));
            return null;
        }
        if (hasMessage == hasEvent || reply.MessageSequence is <= 0)
            throw new ArgumentException(
                "QQ official reply needs one message or event ID and a positive sequence.", nameof(reply));
        if (hasMessage)
        {
            return new QQReplyContext
            {
                MessageId = reply.MessageId,
                MessageSequence = window.NextSequence(reply.MessageId!, reply.MessageSequence),
                ReferenceIndex = reply.ReferenceIndex
            };
        }
        return new QQReplyContext
        {
            EventId = reply.EventId,
            MessageSequence = reply.MessageSequence
        };
    }

    private async Task<SentMessage> SendOfficialAndRememberAsync(Channel channel, QQOutboundContent content,
        QQReplyContext? reply, string description, CancellationToken cancellationToken)
    {
        var sent = await api.SendAsync(channel, content, reply, cancellationToken).ConfigureAwait(false);
        RememberSentMessage(channel, sent, reply?.MessageId, SegmentsFor(content));
        LogSent(channel, description);
        return sent;
    }

    private async Task<SentMessage> SendOneAsync(Channel channel, QQOutboundContent content,
        string? replyToMessageId, string? referenceIndex, string description,
        IReadOnlyList<MessageSegment> segments, CancellationToken cancellationToken)
    {
        if (replyToMessageId is not null && !CanReplyPassively(channel, replyToMessageId)) replyToMessageId = null;
        if (replyToMessageId is null)
            return await SendAndLogAsync(channel, content, null, description, segments, cancellationToken)
                .ConfigureAwait(false);
        var sequence = window.NextSequence(replyToMessageId);
        try
        {
            return await SendAndLogAsync(channel, content, new QQReplyContext
            {
                MessageId = replyToMessageId,
                MessageSequence = sequence,
                ReferenceIndex = referenceIndex
            }, description, segments, cancellationToken).ConfigureAwait(false);
        }
        catch (QQApiException ex) when (ex.IsPassiveReplyExpired)
        {
            logger?.Warning($"QQ Official passive reply was refused (err_code {ex.ErrorCode}); " +
                "sending as a proactive message.");
            return await SendAndLogAsync(channel, content, null, description, segments, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<SentMessage> SendAndLogAsync(Channel channel, QQOutboundContent content,
        QQReplyContext? reply, string description, IReadOnlyList<MessageSegment>? sentSegments,
        CancellationToken cancellationToken = default)
    {
        var sent = await api.SendAsync(channel, content, reply, cancellationToken).ConfigureAwait(false);
        if (sentSegments is not null) RememberSentMessage(channel, sent, reply?.MessageId, sentSegments);
        LogSent(channel, description);
        return sent;
    }

    private void LogSent(Channel channel, string description)
    {
        var target = string.IsNullOrWhiteSpace(channel.Name) ? channel.Id : channel.Name;
        var kind = channel.Type == ChannelType.Group ? "群消息" : "私聊消息";
        logger?.Info($"已发送{kind}到 {target}: {description.Replace("\r", "\\r").Replace("\n", "\\n")}");
    }

    private void RememberSentMessage(Channel channel, SentMessage sent, string? replyToMessageId,
        IReadOnlyList<MessageSegment> segments) =>
        context.RememberSent(channel, sent, api.TakeSentReferenceIndex(sent.MessageId), replyToMessageId, segments,
            getSelfUser?.Invoke());

    private static IReadOnlyList<MessageSegment> SegmentsFor(QQOutboundContent content) => content switch
    {
        QQOutboundContent.TextContent text => [new TextSegment(text.Content)],
        QQOutboundContent.MarkdownContent { Markdown: QCustomMarkdown markdown } =>
            [new TextSegment(markdown.Content)],
        _ => [new RawSegment(QQEventTranslator.Platform, "sent_message",
            JsonSerializer.SerializeToElement(content))]
    };

    private bool CanReplyPassively(Channel channel, string messageId) =>
        window.CanReply(channel, messageId, context.ArrivalOf(messageId));

    private string? TryRecentMessageId(Channel channel) =>
        context.TryRecentMessageId(channel, QQPassiveReplyWindow.Limits(channel).Window);

    private static bool IsSafeOpenId(string userId) =>
        userId is { Length: > 0 and <= 128 } && userId.All(c => c is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}
