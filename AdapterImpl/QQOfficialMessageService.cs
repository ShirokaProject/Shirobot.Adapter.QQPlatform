using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

/// <summary>Implements the shared QQ official capability with this adapter's OpenAPI transport.</summary>
internal sealed class QQOfficialMessageService(
    QQOpenApiClient api, QQMessageService messages, IConsoleLogger? logger = null) : IQOfficialMessageApi
{
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
        var channel = target.Scene switch
        {
            QOfficialMessageScene.Direct => Channel.Direct(target.Id),
            QOfficialMessageScene.Group => Channel.Group(target.Id),
            _ => throw new NotSupportedException("QQ official channel Markdown is not implemented by this adapter.")
        };
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
}
