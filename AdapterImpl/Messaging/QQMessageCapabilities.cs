using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;

/// <summary>Declares what this adapter can encode for a channel and degrades unsupported cards explicitly.</summary>
internal static class QQMessageCapabilities
{
    public static MessageCapabilities ForChannel(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Type is not (ChannelType.Group or ChannelType.Direct)) return new();
        return new()
        {
            NativeFeatures = MessageFeatures.Text | MessageFeatures.Markdown | MessageFeatures.Card
                | MessageFeatures.Image | MessageFeatures.Audio | MessageFeatures.Video | MessageFeatures.File
                | MessageFeatures.Quote | MessageFeatures.LinkButtons | MessageFeatures.CallbackButtons
                | MessageFeatures.InteractionReply
                | (channel.Type == ChannelType.Group
                    ? MessageFeatures.Mention | MessageFeatures.MentionAll
                    : MessageFeatures.None),
            ButtonsRequireMarkdown = true,
            MaxButtonRows = 5,
            MaxButtonsPerRow = 5,
            MaxMediaSegments = 1,
            MaxTextLength = QQTextChunker.MaxLength
        };
    }

    public static MessageCapabilities ForMessage(Channel channel, OutgoingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Segments);
        var capabilities = ForChannel(channel);
        var cards = message.Segments.OfType<CardSegment>().ToArray();
        if (cards.Length == 0) return capabilities;
        try
        {
            if (cards.Length != 1 || message.Buttons is not null
                || message.Segments.Any(segment => segment is not (CardSegment or QuoteSegment)))
                throw new NotSupportedException("QQ cards must be sent alone, optionally with a quote.");
            _ = QQCardMapper.Map(cards[0]);
            return capabilities;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            // Preserve explicit SDK fallbacks for cards that cannot be represented by QQ's tuwen schema.
            return capabilities with { NativeFeatures = capabilities.NativeFeatures & ~MessageFeatures.Card };
        }
    }

    public static MessageSendAssessment Assess(Channel channel, OutgoingMessage message) =>
        MessagePreparation.Assess(message, ForMessage(channel, message));
}
