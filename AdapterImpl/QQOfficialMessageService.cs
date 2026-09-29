using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

/// <summary>Implements the shared QQ official capability with this adapter's OpenAPI transport.</summary>
internal sealed class QQOfficialMessageService(
    QQOpenApiClient api, QQMessageService messages, IConsoleLogger? logger = null,
    Func<string, string?>? groupNameResolver = null)
    : IQOfficialMessageApi, IQOfficialDirectMessageApi
{
    public async Task<string> SendTextAsync(QOfficialMessageTarget target, string content,
        QOfficialMessageReply? reply = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var sent = await messages.SendOfficialAsync(ResolveChannel(target),
            new QQSendRequest { MessageType = 0, Content = content }, reply, content).ConfigureAwait(false);
        return sent.MessageId;
    }

    public async Task<string> SendArkAsync(QOfficialMessageTarget target, int templateId,
        IReadOnlyDictionary<string, string> fields, QOfficialMessageReply? reply = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (templateId <= 0) throw new ArgumentOutOfRangeException(nameof(templateId));
        var request = new QQSendRequest
        {
            MessageType = 3,
            Ark = new QQArk(templateId, fields.Select(field => new QQArkField(field.Key, field.Value)).ToArray())
        };
        var sent = await messages.SendOfficialAsync(ResolveChannel(target), request, reply,
            $"[Ark 模板 {templateId}]").ConfigureAwait(false);
        return sent.MessageId;
    }

    public Task SendTypingAsync(QOfficialMessageTarget target, QOfficialMessageReply reply, TimeSpan duration)
    {
        var (channel, messageId) = ResolveDirectReply(target, reply);
        return messages.SendTypingAsync(channel, messageId, duration);
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
            || target.Scene is not (QOfficialMessageScene.Direct or QOfficialMessageScene.Group))
            return false;
        try
        {
            _ = QQOfficialMessageMapper.MapMarkdown(markdown);
            _ = QQOfficialMessageMapper.MapKeyboard(keyboard);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task<string> SendMarkdownAsync(QOfficialMessageTarget target, QOfficialMarkdown markdown,
        QOfficialKeyboard? keyboard = null, QOfficialMessageReply? reply = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!CanSendMarkdown(target, markdown, keyboard))
            throw new NotSupportedException("QQ official Markdown target, content or keyboard is not supported.");
        var channel = ResolveChannel(target);
        var request = new QQSendRequest
        {
            MessageType = 2,
            Markdown = QQOfficialMessageMapper.MapMarkdown(markdown),
            Keyboard = QQOfficialMessageMapper.MapKeyboard(keyboard)
        };
        var sent = await messages.SendOfficialAsync(channel, request, reply, "[Markdown]")
            .ConfigureAwait(false);
        return sent.MessageId;
    }

    public async Task AcknowledgeInteractionAsync(string interactionId,
        QOfficialInteractionResponseCode code = QOfficialInteractionResponseCode.Success)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interactionId);
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        await api.AcknowledgeInteractionAsync(interactionId, code).ConfigureAwait(false);
        logger?.Info($"已回应 QQ 官方按钮互动 {interactionId}: {code}");
    }

    private Channel ResolveChannel(QOfficialMessageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Id);
        return target.Scene switch
        {
            QOfficialMessageScene.Direct => Channel.Direct(target.Id),
            QOfficialMessageScene.Group => Channel.Group(target.Id)
                with { Name = groupNameResolver?.Invoke(target.Id) },
            _ => throw new NotSupportedException("QQ official channel messages are not implemented by this adapter.")
        };
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
