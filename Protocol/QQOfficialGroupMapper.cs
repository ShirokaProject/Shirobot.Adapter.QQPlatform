using System.Text.Json;
using ShiroBot.Model.QQ;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal static class QQOfficialGroupMapper
{
    internal static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    internal static JsonElement Object(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) ? item : default;
    internal static IEnumerable<JsonElement> Items(JsonElement value, string name) =>
        Object(value, name) is { ValueKind: JsonValueKind.Array } items ? items.EnumerateArray() : [];
    internal static bool Flag(JsonElement value, string name) => Object(value, name).ValueKind == JsonValueKind.True;
    internal static int Number(JsonElement value, string name) => Object(value, name) is var item
        && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) ? number : 0;
    internal static DateTimeOffset? Date(JsonElement value, string name) =>
        DateTimeOffset.TryParse(Text(value, name), out var date) ? date : null;
    internal static DateTimeOffset Time(JsonElement value)
    {
        var stamp = Object(value, "timestamp");
        if (stamp.ValueKind == JsonValueKind.Number && stamp.TryGetInt64(out var seconds)
            && seconds is >= -62135596800 and <= 253402300799) return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return Date(value, "timestamp") ?? Date(value, "apply_at") ?? DateTimeOffset.UtcNow;
    }
    internal static QOfficialJoinRequest JoinRequest(JsonElement value, string groupId, string? eventId = null, string? selfId = null)
    {
        var verification = Object(value, "verify_info");
        return new QOfficialJoinRequest
        {
            GroupOpenId = groupId, MemberOpenId = Text(value, "member_openid") ?? "",
            JoinRequestId = Text(value, "join_request_id") ?? "", EventId = eventId,
            Time = Time(value), SelfId = long.TryParse(selfId, out var numeric) ? numeric : 0,
            Username = Text(value, "username"), UnionOpenId = Text(value, "union_openid"),
            RiskTips = Text(value, "risk_tips"), ApplyAt = Date(value, "apply_at"),
            ApplySource = Text(value, "apply_source"), InvitedBy = Text(value, "invited_by"), IsBot = Flag(value, "bot"),
            Verification = verification.ValueKind == JsonValueKind.Object
                ? new QOfficialJoinVerification(Text(verification, "method"), Text(verification, "verify_message"),
                    Items(verification, "review_qa_list").Select(x => new QOfficialReviewAnswer(Text(x, "question"), Text(x, "answer"))).ToArray()) : null,
            AutoApprovedStrategyId = Text(Object(value, "auto_approved"), "strategy_id")
        };
    }
    internal static QOfficialApprovalStrategy Strategy(JsonElement value, string? id = null) => new()
    {
        StrategyId = Text(value, "strategy_id") ?? id ?? throw new InvalidDataException("QQ strategy response has no ID."),
        Enabled = Text(value, "is_enable") == "on", ExpiresAt = Date(value, "expire_at"),
        CreatedAt = Date(value, "created_at"), UpdatedAt = Date(value, "updated_at"), Remark = Text(value, "remark"),
        GroupOpenIds = Items(value, "group_openids").Select(x => x.GetString() ?? "").ToArray(),
        GroupIds = Items(value, "group_ids").Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()).ToArray(),
        WhitelistUserCount = Number(value, "whitelist_user_count")
    };
    internal static QOfficialGroupMuteState Mutes(JsonElement value)
    {
        var rule = Object(value, "global_rule");
        return new QOfficialGroupMuteState(Text(rule, "mode"),
            Items(rule, "schedule_rules").Select(x => new QOfficialMuteSchedule(Text(x, "task_id") ?? "", Date(x, "start_at"), Date(x, "end_at"), Flag(x, "enabled"))).ToArray(),
            Items(rule, "recurring_rules").Select(x => new QOfficialMuteRecurring(Text(x, "task_id") ?? "", Items(x, "weekdays").Select(d => d.GetInt32()).ToArray(), Text(x, "start_time"), Text(x, "end_time"), Flag(x, "enabled"))).ToArray(),
            Items(value, "members").Select(x => new QOfficialMutedMember(Text(x, "member_openid") ?? "", Date(x, "mute_expire_at"), Text(x, "username"), Text(x, "union_openid"))).ToArray());
    }
}
