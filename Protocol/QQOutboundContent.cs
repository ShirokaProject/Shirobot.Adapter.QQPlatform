using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

/// <summary>
///     A reply target resolved by the application layer. The protocol client turns it into QQ's
///     <c>msg_id</c>/<c>event_id</c>/<c>msg_seq</c> fields and validates exclusivity.
/// </summary>
internal sealed record QQReplyContext
{
    public string? MessageId { get; init; }
    public string? EventId { get; init; }
    public int? MessageSequence { get; init; }

    /// <summary>QQ REFIDX_ value used for a quoted message.</summary>
    public string? ReferenceIndex { get; init; }
}

/// <summary>A protocol-level outgoing payload; Wire DTO construction stays inside the protocol layer.</summary>
internal abstract record QQOutboundContent
{
    public sealed record TextContent(string Content) : QQOutboundContent;

    public sealed record MarkdownContent(QOfficialMarkdown Markdown, QOfficialKeyboard? Keyboard)
        : QQOutboundContent;

    public sealed record CardContent(CardSegment Card) : QQOutboundContent;

    public sealed record ArkContent(int TemplateId, IReadOnlyDictionary<string, string> Fields)
        : QQOutboundContent;

    public sealed record EmbedContent(QOfficialEmbed Embed) : QQOutboundContent;

    public sealed record MediaContent(string FileInfo, string? Content = null) : QQOutboundContent;
}

/// <summary>One QQ C2C stream frame. The protocol client owns the wire request shape.</summary>
internal sealed record QQStreamFrame(
    string Text,
    int State,
    int Index,
    string? StreamMessageId,
    QOfficialStreamContentType ContentType,
    QQReplyContext Reply);
