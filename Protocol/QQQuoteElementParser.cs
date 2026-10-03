using System.Text;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal sealed record QQQuotedMessage(
    string MessageId,
    User Sender,
    IReadOnlyList<MessageSegment> Segments,
    DateTimeOffset Timestamp);

/// <summary>One message described by a QQ <c>msg_elements</c> entry: the quoted message or one of its ancestors.</summary>
internal sealed record QQQuoteBlock(string Text, IReadOnlyList<MessageSegment> Segments)
{
    public bool IsEmpty => Segments.Count == 0;
}

/// <summary>
/// Parses the text QQ attaches to quote messages (message_type 103). The first <c>msg_elements</c> entry looks like:
/// <code>
/// === 消息 1 ===
/// [消息内容] 被引用的消息
/// [消息类型] 引用消息
/// [关联消息]
/// --- 第1条 ---
/// [消息内容] 被引用消息所引用的消息
/// </code>
/// The first block is the directly quoted message; every <c>--- 第N条 ---</c> block is an older ancestor.
/// </summary>
internal static class QQQuoteElementParser
{
    private const string ContentTag = "[消息内容]";

    private static readonly string[] SectionMarkers = ["[消息类型]", "[关联消息]", "--- 第", "=== 消息"];

    public static IReadOnlyList<QQQuoteBlock> Parse(QQIncomingMessage message)
    {
        var element = message.MessageElements?.FirstOrDefault();
        if (element is null) return [];

        var texts = SplitBlocks(element.Content);
        var blocks = new List<QQQuoteBlock>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var segments = new List<MessageSegment>(QQContentParser.Parse(texts[i]));
            if (i == 0)
                foreach (var attachment in element.Attachments ?? [])
                    if (QQAttachmentMapper.ToSegment(attachment) is { } resource) segments.Add(resource);
            blocks.Add(new QQQuoteBlock(texts[i], segments));
        }
        // An attachment-only quote has no text but still describes a message.
        if (blocks.Count == 0 && element.Attachments is { Count: > 0 })
        {
            var segments = element.Attachments.Select(QQAttachmentMapper.ToSegment).OfType<MessageSegment>().ToArray();
            if (segments.Length > 0) blocks.Add(new QQQuoteBlock(string.Empty, segments));
        }
        return blocks.Where(block => !block.IsEmpty).ToArray();
    }

    /// <summary>Returns the <c>[消息内容]</c> text of each block, or the raw text when it is not in the report format.</summary>
    private static List<string> SplitBlocks(string? content)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(content)) return result;
        if (!content.TrimStart().StartsWith("=== 消息", StringComparison.Ordinal))
        {
            result.Add(content.Trim());
            return result;
        }

        StringBuilder? current = null;
        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith(ContentTag, StringComparison.Ordinal))
            {
                Flush();
                current = new StringBuilder(line[ContentTag.Length..].TrimStart());
            }
            else if (SectionMarkers.Any(marker => line.StartsWith(marker, StringComparison.Ordinal)))
                Flush();
            else current?.Append('\n').Append(rawLine);
        }
        Flush();
        return result;

        void Flush()
        {
            if (current is null) return;
            result.Add(current.ToString().Trim());
            current = null;
        }
    }
}
