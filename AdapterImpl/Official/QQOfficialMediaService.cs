using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

using ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;
namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Official;

/// <summary>QQ official chat media upload and send operations, separate from text and markup messages.</summary>
internal sealed class QQOfficialMediaService(
    QQOpenApiClient api,
    QQMessageService messages,
    IConsoleLogger? logger = null,
    Func<string, string?>? groupNameResolver = null)
    : IQOfficialMediaApi, IFileService
{
    private const long MaxUploadSize = 200L * 1024 * 1024;

    public FileCapabilities GetFileCapabilities(Channel channel) => new()
    {
        CanUpload = channel.Type is ChannelType.Group or ChannelType.Direct,
        UploadPublishes = false
    };

    public async Task<FileUploadResult> UploadAsync(Channel channel, FileUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        cancellationToken.ThrowIfCancellationRequested();
        if (!GetFileCapabilities(channel).CanUpload)
            throw new NotSupportedException("QQ official file uploads support group and direct channels only.");
        using var source = await QQMediaSource.ResolveAsync(new FileSegment(request.Uri)
        {
            FileName = request.FileName
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var fileInfo = source.Path is not null
            ? await api.UploadLocalAsync(channel, 4, source.Path, source.FileName, cancellationToken)
                .ConfigureAwait(false)
            : await api.UploadRemoteAsync(channel, 4, source.Url!, source.FileName, cancellationToken)
                .ConfigureAwait(false);
        return new() { FileId = fileInfo, IsPublished = false };
    }

    public async Task<QOfficialMedia> UploadAsync(QOfficialMessageTarget target, QOfficialMediaType type,
        Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        ValidateMedia(type, content, fileName);
        var channel = ResolveChannel(target);
        var path = await CopyToTemporaryFileAsync(content, cancellationToken).ConfigureAwait(false);
        try
        {
            var fileInfo = await api.UploadLocalAsync(channel, (int)type, path, Path.GetFileName(fileName),
                cancellationToken).ConfigureAwait(false);
            logger?.Info($"已上传 QQ 官方媒体到 {target.Id}: {type}, {Path.GetFileName(fileName)}");
            return new QOfficialMedia(fileInfo, type, Path.GetFileName(fileName));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public async Task<string> SendAsync(QOfficialMessageTarget target, QOfficialMedia media,
        QOfficialMessageReply? reply = null, CancellationToken cancellationToken = default) =>
        await SendMediaAsync(ResolveChannel(target), media, null, reply, cancellationToken).ConfigureAwait(false);

    public async Task<string> SendWithCaptionAsync(QOfficialMessageTarget target, QOfficialMedia media,
        string content, QOfficialMessageReply? reply = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return await SendMediaAsync(ResolveChannel(target), media, content, reply, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> UploadAndSendAsync(QOfficialMessageTarget target, QOfficialMediaType type,
        Stream content, string fileName, QOfficialMessageReply? reply = null,
        CancellationToken cancellationToken = default) =>
        await UploadAndSendCoreAsync(target, type, content, fileName, null, reply, cancellationToken)
            .ConfigureAwait(false);

    public async Task<string> UploadAndSendWithCaptionAsync(QOfficialMessageTarget target, QOfficialMediaType type,
        Stream content, string fileName, string caption, QOfficialMessageReply? reply = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCaption = string.IsNullOrWhiteSpace(caption) ? null : caption;
        return await UploadAndSendCoreAsync(target, type, content, fileName, normalizedCaption, reply,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendMediaAsync(Channel channel, QOfficialMedia media, string? content,
        QOfficialMessageReply? reply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (string.IsNullOrWhiteSpace(media.FileInfo))
            throw new ArgumentException("Media file_info is required.", nameof(media));
        if (!Enum.IsDefined(media.Type)) throw new ArgumentOutOfRangeException(nameof(media));
        var sent = await messages.SendOfficialAsync(channel,
            new QQOutboundContent.MediaContent(media.FileInfo, content),
            reply, $"[{media.Type}]", cancellationToken).ConfigureAwait(false);
        return sent.MessageId;
    }

    private async Task<string> UploadAndSendCoreAsync(QOfficialMessageTarget target, QOfficialMediaType type,
        Stream content, string fileName, string? messageContent, QOfficialMessageReply? reply,
        CancellationToken cancellationToken)
    {
        ValidateMedia(type, content, fileName);
        if (messageContent is not null) ArgumentException.ThrowIfNullOrWhiteSpace(messageContent);
        var channel = ResolveChannel(target);
        var path = await CopyToTemporaryFileAsync(content, cancellationToken).ConfigureAwait(false);
        try
        {
            if (reply is null && messageContent is null)
            {
                var sent = await api.SendLocalMediaAsync(channel, type, path, Path.GetFileName(fileName),
                    cancellationToken).ConfigureAwait(false);
                return sent.MessageId;
            }
            var fileInfo = await api.UploadAsync(channel, type, path, Path.GetFileName(fileName),
                cancellationToken).ConfigureAwait(false);
            var result = await messages.SendOfficialAsync(channel,
                new QQOutboundContent.MediaContent(fileInfo, messageContent),
                reply, $"[{type}]", cancellationToken).ConfigureAwait(false);
            return result.MessageId;
        }
        finally
        {
            File.Delete(path);
        }
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
            _ => throw new NotSupportedException(
                "QQ official channel messages are not implemented by this adapter.")
        };
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
}
