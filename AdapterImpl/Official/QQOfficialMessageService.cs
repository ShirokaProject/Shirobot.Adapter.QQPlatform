using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

using ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;
namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Official;

/// <summary>QQ official typed messages, Markdown, cards, interaction replies and direct-chat streaming.</summary>
internal sealed class QQOfficialMessageService(
    QQOpenApiClient api,
    QQMessageService messages,
    IConsoleLogger? logger = null,
    Func<string, string?>? groupNameResolver = null,
    QQOfficialMediaService? media = null)
    : IQOfficialMessageApi, IQOfficialDirectMessageApi, IQOfficialMediaApi, IMessageInteractionService
{
    private readonly QQOfficialMediaService _media =
        media ?? new QQOfficialMediaService(api, messages, logger, groupNameResolver);

    public async Task<string> SendAsync(QOfficialMessageTarget target, QOfficialMessage message,
        QOfficialMessageReply? reply = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        switch (message)
        {
            case QOfficialCardMessage card:
                if (target.Scene is not (QOfficialMessageScene.Group or QOfficialMessageScene.Direct))
                    throw new NotSupportedException("QQ image-text cards support group and direct messages only.");
                return (await SendToTargetAsync(target, new QQOutboundContent.CardContent(card.Content),
                    reply, "[图文卡片]", cancellationToken).ConfigureAwait(false)).MessageId;
            case QOfficialTextMessage text:
                ArgumentException.ThrowIfNullOrWhiteSpace(text.Content);
                return (await SendToTargetAsync(target, new QQOutboundContent.TextContent(text.Content),
                    reply, text.Content, cancellationToken).ConfigureAwait(false)).MessageId;
            case QOfficialMarkdownMessage markdown:
                if (!CanSendMarkdown(target, markdown.Content, markdown.Keyboard))
                    throw new NotSupportedException("QQ official Markdown target, content or keyboard is not supported.");
                return (await SendToTargetAsync(target,
                    new QQOutboundContent.MarkdownContent(markdown.Content, markdown.Keyboard),
                    reply, "[Markdown]", cancellationToken).ConfigureAwait(false)).MessageId;
            case QOfficialMediaSourceMessage source:
                return source.Caption is null
                    ? await _media.UploadAndSendAsync(target, source.Type, source.Content, source.FileName,
                        reply, cancellationToken).ConfigureAwait(false)
                    : await _media.UploadAndSendWithCaptionAsync(target, source.Type, source.Content,
                        source.FileName, source.Caption, reply, cancellationToken).ConfigureAwait(false);
            case QOfficialUploadedMediaMessage uploaded:
                return uploaded.Caption is null
                    ? await _media.SendAsync(target, uploaded.UploadedMedia, reply, cancellationToken)
                        .ConfigureAwait(false)
                    : await _media.SendWithCaptionAsync(target, uploaded.UploadedMedia, uploaded.Caption,
                        reply, cancellationToken).ConfigureAwait(false);
            default:
                throw new NotSupportedException(
                    $"Unsupported QQ official message type: {message.GetType().Name}.");
        }
    }

    public async Task<string> SendTextAsync(QOfficialMessageTarget target, string content,
        QOfficialMessageReply? reply = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var sent = await SendToTargetAsync(target, new QQOutboundContent.TextContent(content),
            reply, content, cancellationToken: cancellationToken).ConfigureAwait(false);
        return sent.MessageId;
    }

    public async Task<string> SendArkAsync(QOfficialMessageTarget target, int templateId,
        IReadOnlyDictionary<string, string> fields, QOfficialMessageReply? reply = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (templateId <= 0) throw new ArgumentOutOfRangeException(nameof(templateId));
        if (fields.Count == 0 || fields.Any(field => string.IsNullOrWhiteSpace(field.Key)
            || string.IsNullOrWhiteSpace(field.Value)))
            throw new ArgumentException("QQ Ark fields need non-empty keys and values.", nameof(fields));
        var sent = await SendToTargetAsync(target,
            new QQOutboundContent.ArkContent(templateId, fields),
            reply, $"[Ark 模板 {templateId}]", cancellationToken: cancellationToken).ConfigureAwait(false);
        return sent.MessageId;
    }

    public async Task<string> SendEmbedAsync(QOfficialMessageTarget target, QOfficialEmbed embed,
        QOfficialMessageReply? reply = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embed);
        if (embed.Title is null && embed.Prompt is null && embed.Thumbnail is null
            && embed.Fields is not { Count: > 0 })
            throw new ArgumentException("QQ Embed must contain at least one visible field.", nameof(embed));
        if (embed.Thumbnail is not null
            && (!Uri.TryCreate(embed.Thumbnail.Url, UriKind.Absolute, out var imageUrl)
                || imageUrl.Scheme is not ("http" or "https")))
            throw new ArgumentException("QQ Embed thumbnail requires an HTTP(S) URL.", nameof(embed));
        if (embed.Fields is not null && embed.Fields.Any(field => field is null || string.IsNullOrWhiteSpace(field.Name)))
            throw new ArgumentException("QQ Embed fields require a name.", nameof(embed));
        var sent = await SendToTargetAsync(target, new QQOutboundContent.EmbedContent(embed),
            reply, "[Embed]", cancellationToken: cancellationToken).ConfigureAwait(false);
        return sent.MessageId;
    }

    public Task SendTypingAsync(QOfficialMessageTarget target, QOfficialMessageReply reply, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var (channel, messageId) = ResolveDirectReply(target, reply);
        return messages.SendTypingAsync(channel, messageId, duration, cancellationToken: cancellationToken);
    }

    public IQOfficialMessageStream BeginStream(QOfficialMessageTarget target, QOfficialMessageReply reply,
        QOfficialStreamContentType contentType = QOfficialStreamContentType.Text)
    {
        var (channel, messageId) = ResolveDirectReply(target, reply);
        return messages.BeginOfficialStream(channel, messageId, contentType);
    }

    public bool CanSendMarkdown(QOfficialMessageTarget target, QOfficialMarkdown markdown,
        QOfficialKeyboard? keyboard = null)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.Id)
            || !Enum.IsDefined(target.Scene))
            return false;
        try
        {
            _ = QQOfficialMessageMapper.MapMarkdown(markdown);
            _ = QQOfficialMessageMapper.MapKeyboard(keyboard, target.Scene == QOfficialMessageScene.Channel);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task<string> SendMarkdownAsync(QOfficialMessageTarget target, QOfficialMarkdown markdown,
        QOfficialKeyboard? keyboard = null, QOfficialMessageReply? reply = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!CanSendMarkdown(target, markdown, keyboard))
            throw new NotSupportedException("QQ official Markdown target, content or keyboard is not supported.");
        var sent = await SendToTargetAsync(target,
            new QQOutboundContent.MarkdownContent(markdown, keyboard),
            reply, "[Markdown]", cancellationToken: cancellationToken).ConfigureAwait(false);
        return sent.MessageId;
    }

    public async Task AcknowledgeInteractionAsync(string interactionId,
        QOfficialInteractionResponseCode code = QOfficialInteractionResponseCode.Success,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interactionId);
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        await api.AcknowledgeInteractionAsync(interactionId, code, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        logger?.Info($"已回应 QQ 官方按钮互动 {interactionId}: {code}");
    }

    public Task AcknowledgeAsync(InteractionEvent interaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        cancellationToken.ThrowIfCancellationRequested();
        if (interaction.Acknowledgement == InteractionAcknowledgement.Acknowledged) return Task.CompletedTask;
        return api.AcknowledgeInteractionAsync(interaction.InteractionId, cancellationToken: cancellationToken);
    }

    public Task<QOfficialMedia> UploadAsync(QOfficialMessageTarget target, QOfficialMediaType type,
        Stream content, string fileName, CancellationToken cancellationToken = default) =>
        _media.UploadAsync(target, type, content, fileName, cancellationToken);

    public Task<string> SendAsync(QOfficialMessageTarget target, QOfficialMedia media,
        QOfficialMessageReply? reply = null, CancellationToken cancellationToken = default) =>
        _media.SendAsync(target, media, reply, cancellationToken);

    public Task<string> SendWithCaptionAsync(QOfficialMessageTarget target, QOfficialMedia media,
        string content, QOfficialMessageReply? reply = null, CancellationToken cancellationToken = default) =>
        _media.SendWithCaptionAsync(target, media, content, reply, cancellationToken);

    public Task<string> UploadAndSendAsync(QOfficialMessageTarget target, QOfficialMediaType type,
        Stream content, string fileName, QOfficialMessageReply? reply = null,
        CancellationToken cancellationToken = default) =>
        _media.UploadAndSendAsync(target, type, content, fileName, reply, cancellationToken);

    public Task<string> UploadAndSendWithCaptionAsync(QOfficialMessageTarget target, QOfficialMediaType type,
        Stream content, string fileName, string caption, QOfficialMessageReply? reply = null,
        CancellationToken cancellationToken = default) =>
        _media.UploadAndSendWithCaptionAsync(target, type, content, fileName, caption, reply, cancellationToken);

    private Channel ResolveChannel(QOfficialMessageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Id);
        return target.Scene switch
        {
            QOfficialMessageScene.Direct => Channel.Direct(target.Id),
            QOfficialMessageScene.Group => Channel.Group(target.Id)
                with { Name = groupNameResolver?.Invoke(target.Id) },
            _ => throw new NotSupportedException(
                "QQ official channel messages are not implemented by this adapter.")
        };
    }

    private Task<SentMessage> SendToTargetAsync(QOfficialMessageTarget target, QQOutboundContent content,
        QOfficialMessageReply? reply, string description, CancellationToken cancellationToken = default)
    {
        if (target.Scene is QOfficialMessageScene.Direct or QOfficialMessageScene.Group)
            return messages.SendOfficialAsync(ResolveChannel(target), content, reply, description, cancellationToken);
        return messages.SendOfficialAsync(target, content, reply, description, cancellationToken);
    }

    private (Channel Channel, string MessageId) ResolveDirectReply(
        QOfficialMessageTarget target, QOfficialMessageReply reply)
    {
        var channel = ResolveChannel(target);
        if (channel.Type != ChannelType.Direct)
            throw new NotSupportedException("QQ official typing and streaming support C2C only.");
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentException.ThrowIfNullOrWhiteSpace(reply.MessageId);
        if (reply.EventId is not null || reply.MessageSequence is not null)
            throw new ArgumentException("QQ official typing and streaming require a message ID only.", nameof(reply));
        return (channel, reply.MessageId);
    }
}
