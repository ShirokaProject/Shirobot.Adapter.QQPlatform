using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

/// <summary>Turns a QQ attachment into the matching SDK resource segment.</summary>
internal static class QQAttachmentMapper
{
    public static MessageSegment? ToSegment(QQAttachment attachment)
    {
        var url = attachment.VoiceWavUrl ?? attachment.Url;
        if (string.IsNullOrWhiteSpace(url)) return null;
        var mime = attachment.ContentType ?? string.Empty;
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return new ImageSegment(url) { FileName = attachment.FileName, Width = attachment.Width, Height = attachment.Height };
        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return new AudioSegment(url)
            {
                FileName = attachment.FileName,
                Transcript = string.IsNullOrWhiteSpace(attachment.AsrText) ? null : attachment.AsrText
            };
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return new VideoSegment(url) { FileName = attachment.FileName };
        return new FileSegment(url) { FileName = attachment.FileName, FileSize = attachment.Size };
    }
}
