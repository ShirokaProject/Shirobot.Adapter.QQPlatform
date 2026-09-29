using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal static class QQEventTranslator
{
    internal const string Platform = "qq-official";

    public static BotEvent? Translate(GatewayPayload payload, string? selfId, string? selfName = null)
    {
        if (payload.EventType is not { Length: > 0 } type) return null;
        var raw = payload.Data.Clone();
        if (type == "INTERACTION_CREATE")
        {
            var interaction = raw.Deserialize<QQInteractionData>();
            if (interaction is not { Type: 11, Id: { Length: > 0 }, Data.Resolved.ButtonData: { Length: > 0 } })
                return null;
            var isGroup = interaction.ChatType == 1 || interaction.Scene == "group";
            var isChannel = !isGroup && !string.IsNullOrWhiteSpace(interaction.ChannelId);
            var channelId = isGroup ? interaction.GroupOpenId
                : isChannel ? interaction.ChannelId : interaction.UserOpenId;
            var userId = isGroup ? interaction.GroupMemberOpenId
                : isChannel ? interaction.UserId : interaction.UserOpenId;
            if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId)) return null;
            var target = new QOfficialMessageTarget(
                isGroup ? QOfficialMessageScene.Group
                : isChannel ? QOfficialMessageScene.Channel : QOfficialMessageScene.Direct,
                channelId);
            return new PlatformEvent
            {
                Platform = Platform, SelfId = selfId, Kind = QEventKinds.OfficialButtonInteraction,
                Channel = isGroup ? Channel.Group(channelId)
                    : isChannel ? new Channel(channelId, ChannelType.Other) : Channel.Direct(channelId),
                Raw = new QOfficialButtonInteraction
                {
                    Time = DateTimeOffset.UtcNow,
                    SelfId = long.TryParse(selfId, out var numericSelfId) ? numericSelfId : 0,
                    InteractionId = interaction.Id,
                    EventId = payload.Id,
                    ButtonData = interaction.Data.Resolved.ButtonData,
                    ButtonId = interaction.Data.Resolved.ButtonId,
                    MessageId = interaction.Data.Resolved.MessageId,
                    Target = target,
                    UserId = userId,
                    GuildId = interaction.GuildId
                }
            };
        }
        if (type is "GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE" or "C2C_MESSAGE_CREATE")
        {
            var message = raw.Deserialize<QQIncomingMessage>();
            if (message is null || string.IsNullOrWhiteSpace(message.Id)) return null;
            var isGroup = type is "GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE";
            var senderId = isGroup
                ? message.Author?.MemberOpenId ?? message.Author?.Id
                : message.Author?.UserOpenId ?? message.Author?.Id;
            var channelId = isGroup ? message.GroupOpenId : senderId;
            if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(senderId)) return null;
            var segments = new List<MessageSegment>();
            if (!string.IsNullOrWhiteSpace(message.MessageReference?.MessageId))
                segments.Add(new QuoteSegment(message.MessageReference.MessageId));
            var mentionNames = (message.Mentions ?? [])
                .Select(mention => (Id: mention.MemberOpenId ?? mention.Id, mention.Username))
                .Where(mention => !string.IsNullOrWhiteSpace(mention.Id) && !string.IsNullOrWhiteSpace(mention.Username))
                .GroupBy(mention => mention.Id!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Username!, StringComparer.Ordinal);
            segments.AddRange(QQContentParser.Parse(message.Content, mentionNames));
            if (type == "GROUP_AT_MESSAGE_CREATE" && !string.IsNullOrWhiteSpace(selfId))
            {
                var botName = string.IsNullOrWhiteSpace(selfName) ? "机器人" : selfName;
                var selfMention = segments.OfType<MentionSegment>()
                    .FirstOrDefault(mention => mention.UserId == selfId);
                if (selfMention is null)
                    segments.Insert(0, new MentionSegment(selfId) { DisplayName = botName });
                else if (string.IsNullOrWhiteSpace(selfMention.DisplayName))
                {
                    var index = segments.IndexOf(selfMention);
                    segments[index] = selfMention with { DisplayName = botName };
                }
            }
            foreach (var attachment in message.Attachments ?? [])
            {
                var url = attachment.VoiceWavUrl ?? attachment.Url;
                if (string.IsNullOrWhiteSpace(url)) continue;
                var mime = attachment.ContentType ?? "";
                segments.Add(mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    ? new ImageSegment(url) { FileName = attachment.FileName }
                    : mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                        ? new AudioSegment(url) { FileName = attachment.FileName }
                        : mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                            ? new VideoSegment(url) { FileName = attachment.FileName }
                            : new FileSegment(url) { FileName = attachment.FileName });
            }
            var sender = new User(senderId) { Name = message.Author?.Username, IsBot = message.Author?.Bot ?? false };
            return new MessageEvent
            {
                Platform = Platform, SelfId = selfId, Raw = raw, MessageId = message.Id,
                Channel = isGroup ? Channel.Group(channelId) : Channel.Direct(channelId),
                Sender = sender,
                Member = isGroup ? new Member(sender)
                {
                    Nick = message.Author?.Username,
                    Role = QQIdentityMapper.Role(message.Author?.MemberRole)
                } : null,
                Segments = segments,
                Timestamp = DateTimeOffset.TryParse(message.Timestamp, out var timestamp) ? timestamp : DateTimeOffset.UtcNow
            };
        }
        if (type is "READY" or "RESUMED") return null;
        return new PlatformEvent { Platform = Platform, SelfId = selfId, Raw = raw, Kind = type.ToLowerInvariant() };
    }
}
