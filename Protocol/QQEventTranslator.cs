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
        if (type is "GROUP_MEMBER_ADD" or "GROUP_MEMBER_REMOVE" or "GROUP_JOIN_REQUEST")
        {
            var groupId = QQOfficialGroupMapper.Text(raw, "group_openid");
            var memberId = QQOfficialGroupMapper.Text(raw, "member_openid");
            if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(memberId)) return null;
            if (type == "GROUP_JOIN_REQUEST")
            {
                if (string.IsNullOrWhiteSpace(QQOfficialGroupMapper.Text(raw, "join_request_id"))) return null;
                return new PlatformEvent
                {
                    Platform = Platform, SelfId = selfId, Channel = Channel.Group(groupId),
                    Kind = QEventKinds.OfficialGroupJoinRequest,
                    Raw = QQOfficialGroupMapper.JoinRequest(raw, groupId, payload.Id, selfId)
                };
            }
            var member = new QOfficialGroupMemberEvent
            {
                GroupOpenId = groupId, MemberOpenId = memberId,
                UserOpenId = QQOfficialGroupMapper.Text(raw, "user_openid"), EventId = payload.Id,
                Time = QQOfficialGroupMapper.Time(raw), SelfId = long.TryParse(selfId, out var numeric) ? numeric : 0
            };
            return type == "GROUP_MEMBER_ADD"
                ? new MemberJoinedEvent { Platform = Platform, SelfId = selfId, Channel = Channel.Group(groupId), UserId = memberId, Raw = member }
                : new MemberLeftEvent { Platform = Platform, SelfId = selfId, Channel = Channel.Group(groupId), UserId = memberId, Raw = member };
        }
        if (type == "INTERACTION_CREATE")
        {
            var interaction = raw.Deserialize<QQInteractionData>(QQJson.Options);
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
            var message = raw.Deserialize<QQIncomingMessage>(QQJson.Options);
            if (message is null || string.IsNullOrWhiteSpace(message.Id)) return null;
            var isGroup = type is "GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE";
            var senderId = isGroup
                ? message.Author?.MemberOpenId ?? message.Author?.Id
                : message.Author?.UserOpenId ?? message.Author?.Id;
            var channelId = isGroup ? message.GroupOpenId : senderId;
            if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(senderId)) return null;
            var segments = new List<MessageSegment>();
            var referencedMessageId = GetReferencedMessageIndex(message, raw);
            if (!string.IsNullOrWhiteSpace(referencedMessageId))
                segments.Add(new QuoteSegment(referencedMessageId));
            var mentionNames = (message.Mentions ?? [])
                .Select(mention => (Id: mention.MemberOpenId ?? mention.Id, mention.Username))
                .Where(mention => !string.IsNullOrWhiteSpace(mention.Id) && !string.IsNullOrWhiteSpace(mention.Username))
                .GroupBy(mention => mention.Id!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Username!, StringComparer.Ordinal);
            segments.AddRange(QQContentParser.Parse(message.Content, mentionNames));
            var selfMentionIds = new HashSet<string>(StringComparer.Ordinal);
            var mentionsBot = type == "GROUP_AT_MESSAGE_CREATE";
            if (!string.IsNullOrWhiteSpace(selfId))
            {
                foreach (var mention in message.Mentions ?? [])
                {
                    var matchesSelf = string.Equals(mention.MemberOpenId, selfId, StringComparison.Ordinal)
                        || string.Equals(mention.UserOpenId, selfId, StringComparison.Ordinal)
                        || string.Equals(mention.Id, selfId, StringComparison.Ordinal)
                        || (mention.Bot && !string.IsNullOrWhiteSpace(selfName)
                            && string.Equals(mention.Username, selfName, StringComparison.Ordinal));
                    if (!matchesSelf) continue;

                    mentionsBot = true;
                    AddMentionId(selfMentionIds, mention.MemberOpenId);
                    AddMentionId(selfMentionIds, mention.UserOpenId);
                    AddMentionId(selfMentionIds, mention.Id);
                }
            }
            if (isGroup && mentionsBot && !string.IsNullOrWhiteSpace(selfId))
            {
                var botName = string.IsNullOrWhiteSpace(selfName) ? "机器人" : selfName;
                var selfMentionIndex = segments.FindIndex(segment => segment is MentionSegment mention
                    && (mention.UserId == selfId || selfMentionIds.Contains(mention.UserId)));
                if (selfMentionIndex < 0)
                    segments.Insert(0, new MentionSegment(selfId) { DisplayName = botName });
                else
                {
                    var selfMention = (MentionSegment)segments[selfMentionIndex];
                    if (selfMention.UserId != selfId || string.IsNullOrWhiteSpace(selfMention.DisplayName))
                        segments[selfMentionIndex] = selfMention with { UserId = selfId, DisplayName = botName };
                }
            }
            foreach (var attachment in message.Attachments ?? [])
                if (QQAttachmentMapper.ToSegment(attachment) is { } resource) segments.Add(resource);
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
        if (Lifecycle.TryGetValue(type, out var lifecycle) && TranslateLifecycle(payload, raw, lifecycle, selfId) is { } lifecycleEvent)
            return lifecycleEvent;
        if (type is "READY" or "RESUMED") return null;
        return new PlatformEvent { Platform = Platform, SelfId = selfId, Raw = raw, Kind = type.ToLowerInvariant() };
    }

    private static readonly Dictionary<string, (string Kind, bool Group)> Lifecycle = new(StringComparer.Ordinal)
    {
        ["GROUP_ADD_ROBOT"] = (QEventKinds.OfficialGroupAddRobot, true),
        ["GROUP_DEL_ROBOT"] = (QEventKinds.OfficialGroupDelRobot, true),
        ["GROUP_MSG_REJECT"] = (QEventKinds.OfficialGroupMsgReject, true),
        ["GROUP_MSG_RECEIVE"] = (QEventKinds.OfficialGroupMsgReceive, true),
        ["FRIEND_ADD"] = (QEventKinds.OfficialFriendAdd, false),
        ["FRIEND_DEL"] = (QEventKinds.OfficialFriendDel, false),
        ["C2C_MSG_REJECT"] = (QEventKinds.OfficialC2CMsgReject, false),
        ["C2C_MSG_RECEIVE"] = (QEventKinds.OfficialC2CMsgReceive, false),
    };

    /// <summary>Group/friend lifecycle events: keep the channel and event id so plugins can react and reply.</summary>
    private static BotEvent? TranslateLifecycle(GatewayPayload payload, JsonElement raw,
        (string Kind, bool Group) lifecycle, string? selfId)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        var channelId = lifecycle.Group ? ReadString(raw, "group_openid") : ReadString(raw, "openid");
        if (string.IsNullOrWhiteSpace(channelId)) return null;
        var operatorId = lifecycle.Group ? ReadString(raw, "op_member_openid") : channelId;
        DateTimeOffset? eventTime = null;
        if (raw.TryGetProperty("timestamp", out var stamp))
        {
            if (stamp.ValueKind == JsonValueKind.Number && stamp.TryGetInt64(out var seconds))
                eventTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
            else if (stamp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(stamp.GetString(), out var parsed))
                eventTime = parsed;
        }
        var lifecycleRaw = new QOfficialLifecycleEvent
        {
            Time = eventTime ?? DateTimeOffset.UtcNow,
            SelfId = long.TryParse(selfId, out var numericSelfId) ? numericSelfId : 0,
            EventId = payload.Id,
            Target = new QOfficialMessageTarget(
                lifecycle.Group ? QOfficialMessageScene.Group : QOfficialMessageScene.Direct, channelId),
            OperatorId = operatorId,
            EventTime = eventTime
        };
        // The SDK has types for "bot joined" and "member left"; the bot being removed is the latter.
        if (lifecycle.Kind == QEventKinds.OfficialGroupAddRobot)
            return new GuildInviteEvent
            {
                Platform = Platform, SelfId = selfId, Raw = lifecycleRaw,
                GuildId = channelId, InviterId = operatorId ?? string.Empty
            };
        if (lifecycle.Kind == QEventKinds.OfficialGroupDelRobot)
            return new MemberLeftEvent
            {
                Platform = Platform, SelfId = selfId, Raw = lifecycleRaw,
                Channel = Channel.Group(channelId), UserId = selfId ?? string.Empty, OperatorId = operatorId
            };
        return new PlatformEvent
        {
            Platform = Platform, SelfId = selfId, Kind = lifecycle.Kind,
            Channel = lifecycle.Group ? Channel.Group(channelId) : Channel.Direct(channelId),
            Raw = lifecycleRaw
        };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AddMentionId(HashSet<string> mentions, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) mentions.Add(id);
    }

    private static string? GetSceneExtension(JsonElement raw, string prefix)
    {
        if (raw.ValueKind != JsonValueKind.Object
            || !raw.TryGetProperty("message_scene", out var scene)
            || scene.ValueKind != JsonValueKind.Object
            || !scene.TryGetProperty("ext", out var extensions)
            || extensions.ValueKind != JsonValueKind.Array) return null;
        foreach (var extension in extensions.EnumerateArray())
        {
            if (extension.ValueKind != JsonValueKind.String
                || extension.GetString() is not { } value
                || !value.StartsWith(prefix, StringComparison.Ordinal)
                || value.Length <= prefix.Length) continue;
            return value[prefix.Length..].Trim();
        }
        return null;
    }

    private static string? GetReferencedMessageIndex(QQIncomingMessage message, JsonElement raw)
    {
        // QQ message_type=103 carries the most authoritative reference in msg_elements[0].
        // ref_msg_idx can be stale or a TMP_* placeholder for nested quote messages.
        if (message.MessageType == 103
            && !string.IsNullOrWhiteSpace(message.MessageElements?.FirstOrDefault()?.MessageIndex))
            return message.MessageElements[0].MessageIndex;
        return message.MessageReference?.MessageId
            ?? GetSceneExtension(raw, "ref_msg_idx=")
            ?? GetSceneExtension(raw, "refMsgIdx:");
    }
}
