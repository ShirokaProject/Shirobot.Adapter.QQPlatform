using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.Wire;

namespace ShiroBot.Adapter.QQPlatform.Protocol.Mapping;

internal sealed record QQQuoteEnvelope(IReadOnlyList<QQQuoteBlock> Blocks, string? ElementIndex)
{
    public static QQQuoteEnvelope Empty { get; } = new([], null);
}

/// <summary>Reads quote metadata out of a raw QQ message without leaking Wire DTOs to the application layer.</summary>
internal static class QQIncomingMessageParser
{
    public static QQQuoteEnvelope Parse(JsonElement raw)
    {
        var wire = raw.Deserialize<QQIncomingMessage>(QQJson.Options);
        if (wire is null) return QQQuoteEnvelope.Empty;
        return new QQQuoteEnvelope(QQQuoteElementParser.Parse(wire),
            wire.MessageElements?.FirstOrDefault()?.MessageIndex);
    }
}
