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
    : IQOfficialMessageApi, IQOfficialDirectMessageApi, IQOfficialMediaApi
{
    private const long MaxUploadSize = 200L * 1024 * 1024;

    public Task<string> SendAsync(QOfficialMessageTarget target, QOfficialMessage message,
        QOfficialMessageReply? reply = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message switch
        {
            QOfficialTextMessage text => SendTextAsync(target, text.Content, reply),
            QOfficialMarkdownMessage markdown => SendMarkdownAsync(target, markdown.Content,
                markdown.Keyboard, reply),
            QOfficialMediaSourceMessage media => media.Caption is null
                ? UploadAndSendAsync(target, media.Type, media.Content, media.FileName, reply, cancellationToken)
                : UploadAndSendWithCaptionAsync(target, media.Type, media.Content, media.FileName, media.Caption,
                    reply, cancellationToken),
            QOfficialUploadedMediaMessage media => SendMediaAsync(target, media.UploadedMedia,
                media.Caption ?? " ", reply),
            _ => throw new NotSupportedException($"Unsupported QQ official message type: {message.GetType().Name}.")
        };
    }

    public async Task<QOfficialMedia> UploadAsync(QOfficialMessageTarget target, QOfficialMediaType type,
        Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        ValidateMedia(type, content, fileName);
        var channel = ResolveMediaChannel(target);
        var path = await CopyToTemporaryFileAsync(content, cancellationToken).ConfigureAwait(false);
        try
        {
            var fileInfo = await api.UploadLocalAsync(channel, (int)type, path, Path.GetFileName(fileName),
                cancellationToken).ConfigureAwait(false);
            return new QOfficialMedia(fileInfo, type, Path.GetFileName(fileName));
        }
        finally { File.Delete(path); }
    }

    public async Task<string> SendAsync(QOfficialMessageTarget target, QOfficialMedia media,
        QOfficialMessageReply? reply = null)
        => await SendMediaAsync(target, media, " ", reply).ConfigureAwait(false);

    public async Task<string> SendWithCaptionAsync(QOfficialMessageTarget target, QOfficialMedia media,
        string content, QOfficialMessageReply? reply = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return await SendMediaAsync(target, media, content, reply).ConfigureAwait(false);
    }

    private async Task<string> SendMediaAsync(QOfficialMessageTarget target, QOfficialMedia media,
        string content, QOfficialMessageReply? reply)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (string.IsNullOrWhiteSpace(media.FileInfo)) throw new ArgumentException("Media file_info is required.", nameof(media));
        if (!Enum.IsDefined(media.Type)) throw new ArgumentOutOfRangeException(nameof(media));
        var sent = await messages.SendOfficialAsync(ResolveMediaChannel(target),
            new QQSendRequest { MessageType = 7, Content = content, Media = new QQMedia(media.FileInfo) },
            reply, $"[{media.Type}]").ConfigureAwait(false);
        return sent.MessageId;
    }

    public async Task<string> UploadAndSendAsync(QOfficialMessageTarget target, QOfficialMediaType type,
        Stream content, string fileName, QOfficialMessageReply? reply = null,
        CancellationToken cancellationToken = default)
        => await UploadAndSendCoreAsync(target, type, content, fileName, null, reply, cancellationToken)
            .ConfigureAwait(false);

    public async Task<string> UploadAndSendWithCaptionAsync(QOfficialMessageTarget target, QOfficialMediaType type,
        Stream content, string fileName, string caption, QOfficialMessageReply? reply = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCaption = string.IsNullOrWhiteSpace(caption) ? null : caption;
        return await UploadAndSendCoreAsync(target, type, content, fileName, normalizedCaption, reply, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> UploadAndSendCoreAsync(QOfficialMessageTarget target, QOfficialMediaType type,
        Stream content, string fileName, string? messageContent, QOfficialMessageReply? reply,
        CancellationToken cancellationToken)
    {
        ValidateMedia(type, content, fileName);
        if (messageContent is not null) ArgumentException.ThrowIfNullOrWhiteSpace(messageContent);
        var channel = ResolveMediaChannel(target);
        var path = await CopyToTemporaryFileAsync(content, cancellationToken).ConfigureAwait(false);
        try
        {
            if (reply is null && messageContent is null)
            {
                var sent = await api.SendLocalAsync(channel, (int)type, path, Path.GetFileName(fileName),
                    cancellationToken).ConfigureAwait(false);
                return sent.MessageId;
            }
            var fileInfo = await api.UploadLocalAsync(channel, (int)type, path, Path.GetFileName(fileName),
                cancellationToken).ConfigureAwait(false);
            var result = await messages.SendOfficialAsync(channel,
                new QQSendRequest { MessageType = 7, Content = messageContent ?? " ", Media = new QQMedia(fileInfo) },
                reply, $"[{type}]").ConfigureAwait(false);
            return result.MessageId;
        }
        finally { File.Delete(path); }
    }

    private Channel ResolveMediaChannel(QOfficialMessageTarget target)
    {
        return ResolveChannel(target);
    }

    private static void ValidateMedia(QOfficialMediaType type, Stream content, string fileName)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new ArgumentException("Media stream must be readable.", nameof(content));
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.GetFileName(fileName) != fileName || fileName is "." or "..")
            throw new ArgumentException("File name must not contain a path.", nameof(fileName));
    }

    private static async Task<string> CopyToTemporaryFileAsync(Stream content, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"shirobot-qq-{Guid.NewGuid():N}.upload");
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxUploadSize)
                    throw new NotSupportedException("QQ Official files must not exceed 200 MB.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (total == 0) throw new ArgumentException("Media stream is empty.", nameof(content));
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    public async Task<string> SendTextAsync(QOfficialMessageTarget target, string content,
        QOfficialMessageReply? reply = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var sent = await SendToTargetAsync(target,
            new QQSendRequest { MessageType = 0, Content = content }, reply, content).ConfigureAwait(false);
        return sent.MessageId;
    }

    public async Task<string> SendArkAsync(QOfficialMessageTarget target, int templateId,
        IReadOnlyDictionary<string, string> fields, QOfficialMessageReply? reply = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (templateId <= 0) throw new ArgumentOutOfRangeException(nameof(templateId));
        if (fields.Count == 0 || fields.Any(field => string.IsNullOrWhiteSpace(field.Key)
            || string.IsNullOrWhiteSpace(field.Value)))
            throw new ArgumentException("QQ Ark fields need non-empty keys and values.", nameof(fields));
        var request = new QQSendRequest
        {
            MessageType = 3,
            Ark = new QQArk(templateId, fields.Select(field => new QQArkField(field.Key, field.Value)).ToArray())
        };
        var sent = await SendToTargetAsync(target, request, reply,
            $"[Ark 模板 {templateId}]").ConfigureAwait(false);
        return sent.MessageId;
    }

    public async Task<string> SendEmbedAsync(QOfficialMessageTarget target, QOfficialEmbed embed,
        QOfficialMessageReply? reply = null)
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
        var request = new QQSendRequest
        {
            MessageType = 4,
            Content = string.Empty,
            Embed = new QQEmbed
            {
                Title = embed.Title,
                Prompt = embed.Prompt,
                Thumbnail = embed.Thumbnail is null ? null : new QQEmbedThumbnail(embed.Thumbnail.Url),
                Fields = embed.Fields?.Select(field => new QQEmbedField(field.Name)).ToArray()
            }
        };
        var sent = await SendToTargetAsync(target, request, reply, "[Embed]").ConfigureAwait(false);
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
        QOfficialKeyboard? keyboard = null, QOfficialMessageReply? reply = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!CanSendMarkdown(target, markdown, keyboard))
            throw new NotSupportedException("QQ official Markdown target, content or keyboard is not supported.");
        var request = new QQSendRequest
        {
            MessageType = 2,
            Markdown = QQOfficialMessageMapper.MapMarkdown(markdown),
            Keyboard = QQOfficialMessageMapper.MapKeyboard(keyboard, target.Scene == QOfficialMessageScene.Channel)
        };
        var sent = await SendToTargetAsync(target, request, reply, "[Markdown]")
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

    private Task<SentMessage> SendToTargetAsync(QOfficialMessageTarget target, QQSendRequest request,
        QOfficialMessageReply? reply, string description)
    {
        if (target.Scene is QOfficialMessageScene.Direct or QOfficialMessageScene.Group)
            return messages.SendOfficialAsync(ResolveChannel(target), request, reply, description);
        return messages.SendOfficialAsync(QQApiRoutes.Messages(target), target.Id, request, reply, description);
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
