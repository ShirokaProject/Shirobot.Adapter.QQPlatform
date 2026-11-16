using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Transport;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Dispatch;

/// <summary>
/// Turns raw gateway payloads into ShiroBot events. Keeps de-duplication, interaction acknowledgement,
/// group-name enrichment and delivery order in one place so the adapter class stays a composition root.
/// </summary>
internal sealed class QQInboundDispatcher(
    QQOpenApiClient api,
    QQMessageService messages,
    QQEventService events,
    QQGroupNameCache groupNames,
    IConsoleLogger logger,
    Func<string?> selfId,
    Func<User?> selfUser,
    bool traceEvents)
{
    private const int SeenCapacity = 4096;

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();

    public async Task DispatchAsync(GatewayPayload payload)
    {
        if (payload.Op != 0) return;
        if (traceEvents) logger.Info($"QQ Official event: {payload.EventType}");
        var id = payload.Id;
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (!_seen.Add(id)) return;
            _seenOrder.Enqueue(id);
            if (_seenOrder.Count > SeenCapacity) _seen.Remove(_seenOrder.Dequeue());
        }

        try
        {
            var translated = QQEventTranslator.Translate(payload, selfId(), selfUser()?.Name);
            if (translated is InteractionEvent interaction)
            {
                translated = await AcknowledgeAsync(interaction).ConfigureAwait(false);
                messages.RememberInteraction((InteractionEvent)translated);
                if (interaction.Channel?.Type == ChannelType.Group)
                {
                    var name = await groupNames.GetAsync(interaction.Channel.Id).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(name))
                        translated = ((InteractionEvent)translated) with
                        {
                            Channel = interaction.Channel with { Name = name }
                        };
                }
            }

            if (translated is MessageEvent { IsDirect: false } groupMessage)
            {
                LogMentionDiagnostics(payload, groupMessage);
                var name = await groupNames.GetAsync(groupMessage.Channel.Id).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(name))
                    translated = groupMessage with { Channel = groupMessage.Channel with { Name = name } };
            }

            if (translated is MessageEvent message)
                translated = messages.RegisterIncoming(message);

            if (translated is not null) await events.PublishAsync(translated).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.Error($"QQ Official event {payload.EventType} failed: {ex.Message}");
        }
    }

    private async Task<BotEvent> AcknowledgeAsync(InteractionEvent platformEvent)
    {
        try
        {
            await api.AcknowledgeInteractionAsync(platformEvent.InteractionId).ConfigureAwait(false);
            return platformEvent with { Acknowledgement = InteractionAcknowledgement.Acknowledged };
        }
        catch (Exception ex)
        {
            logger.Warning($"QQ interaction acknowledgement failed: {ex.Message}");
            return platformEvent with
            {
                Acknowledgement = ex is QQApiException
                    ? InteractionAcknowledgement.Failed
                    : InteractionAcknowledgement.Unknown
            };
        }
    }

    /// <summary>QQ expects the button callback to be answered quickly, so do it as soon as the event arrives.</summary>
    public void AcknowledgeInteractionEarly(GatewayPayload payload)
    {
        if (QQEventTranslator.InteractionId(payload) is not { } id) return;
        _ = Task.Run(async () =>
        {
            try { await api.AcknowledgeInteractionAsync(id).ConfigureAwait(false); }
            catch (Exception ex) { logger.Warning($"QQ interaction acknowledgement failed: {ex.Message}"); }
        });
    }

    public void Clear()
    {
        _seen.Clear();
        _seenOrder.Clear();
    }

    private void LogMentionDiagnostics(GatewayPayload payload, MessageEvent message)
    {
        var mentionCount = 0;
        var botMentionCount = 0;
        if (payload.Data.ValueKind == JsonValueKind.Object
            && payload.Data.TryGetProperty("mentions", out var mentions)
            && mentions.ValueKind == JsonValueKind.Array)
        {
            foreach (var mention in mentions.EnumerateArray())
            {
                mentionCount++;
                if (mention.ValueKind == JsonValueKind.Object
                    && mention.TryGetProperty("bot", out var bot)
                    && bot.ValueKind == JsonValueKind.True)
                    botMentionCount++;
            }
        }

        var mentionSegments = message.Segments.OfType<MentionSegment>().Count();
        var selfName = selfUser()?.Name;
        var botNameMatches = 0;
        if (!string.IsNullOrWhiteSpace(selfName)
            && payload.Data.ValueKind == JsonValueKind.Object
            && payload.Data.TryGetProperty("mentions", out var mentionUsers)
            && mentionUsers.ValueKind == JsonValueKind.Array)
        {
            botNameMatches = mentionUsers.EnumerateArray().Count(mention =>
                mention.ValueKind == JsonValueKind.Object
                && mention.TryGetProperty("bot", out var bot)
                && bot.ValueKind == JsonValueKind.True
                && mention.TryGetProperty("username", out var username)
                && username.ValueKind == JsonValueKind.String
                && string.Equals(username.GetString(), selfName, StringComparison.Ordinal));
        }
        if (payload.EventType != "GROUP_AT_MESSAGE_CREATE" && mentionCount == 0 && mentionSegments == 0)
            return;

        var selfIdReady = !string.IsNullOrWhiteSpace(selfId());
        var selfMentioned = selfIdReady && message.HasMention(selfId()!);
        logger.Info($"QQ Official @ diagnostic: event={payload.EventType}, selfIdReady={selfIdReady}, " +
            $"selfMentioned={selfMentioned}, mentionMetadata={mentionCount}, botMetadata={botMentionCount}, " +
            $"botNameMatches={botNameMatches}, mentionSegments={mentionSegments}");
    }
}
