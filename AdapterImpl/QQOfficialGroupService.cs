using System.Globalization;
using System.Text.Json.Nodes;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Model.QQ;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

internal sealed class QQOfficialGroupService(QQOpenApiClient api) : IQOfficialGroupApi
{
    private static string GroupRoute(string id, string resource) => $"v2/groups/{Id(id)}/{resource}";
    private static string StrategyRoute(string id) => $"v2/groups/join_approval_strategy/{Id(id)}";
    private static string Id(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Uri.EscapeDataString(id);
    }
    private static string Page(string route, string? cursor, int limit)
    {
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        return $"{route}?limit={limit}" + (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
    }
    private static string Stamp(DateTimeOffset date) => date.ToString("O", CultureInfo.InvariantCulture);
    private static JsonArray Strings(IReadOnlyList<string> values, int max)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is 0 || values.Count > max || values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Expected 1 to {max} non-empty identifiers.", nameof(values));
        return new JsonArray(values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    }
    private static void Groups(JsonObject body, IReadOnlyList<string>? openIds, IReadOnlyList<ulong>? ids)
    {
        if ((openIds is null) == (ids is null)) throw new ArgumentException("Specify either group OpenIDs or group numbers.");
        if (openIds is not null) body["group_openids"] = Strings(openIds, 100);
        else
        {
            if (ids!.Count is 0 or > 100 || ids.Any(x => x == 0)) throw new ArgumentException("Expected 1 to 100 group numbers.");
            body["group_ids"] = new JsonArray(ids.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        }
    }
    private static void Remark(JsonObject body, string? remark)
    {
        if (remark is null) return;
        if (remark.EnumerateRunes().Count() > 255) throw new ArgumentException("Strategy remark exceeds 255 characters.", nameof(remark));
        body["remark"] = remark;
    }

    public async Task<QOfficialJoinRequestPage> GetJoinRequestsAsync(string groupOpenId, string? cursor = null,
        int limit = 20, CancellationToken cancellationToken = default)
    {
        var result = await api.SendGroupRequestAsync(HttpMethod.Get, Page(GroupRoute(groupOpenId, "join_request_list"), cursor, limit), null, cancellationToken).ConfigureAwait(false);
        return new QOfficialJoinRequestPage(QQOfficialGroupMapper.Items(result, "list")
            .Select(x => QQOfficialGroupMapper.JoinRequest(x, groupOpenId)).ToArray(), QQOfficialGroupMapper.Text(result, "next_cursor"));
    }
    public Task ApproveJoinRequestAsync(string groupOpenId, string memberOpenId, string joinRequestId,
        CancellationToken cancellationToken = default) => Review(groupOpenId, memberOpenId, joinRequestId, true, null, false, cancellationToken);
    public Task RejectJoinRequestAsync(string groupOpenId, string memberOpenId, string joinRequestId,
        string? reason = null, bool addToBlacklist = false, CancellationToken cancellationToken = default) =>
        Review(groupOpenId, memberOpenId, joinRequestId, false, reason, addToBlacklist, cancellationToken);
    private async Task Review(string group, string member, string requestId, bool approve, string? reason, bool blacklist, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var body = new JsonObject { ["op"] = approve ? "approve" : "decline", ["join_request_id"] = requestId };
        if (!approve)
        {
            body["add_to_member_blacklist"] = blacklist;
            if (reason is not null) body["reject_reason"] = reason;
        }
        await api.SendGroupRequestAsync(HttpMethod.Post, GroupRoute(group, $"approval_join_request/{Id(member)}"), body, token).ConfigureAwait(false);
    }
    public async Task<QOfficialGroupMuteState> GetMuteStateAsync(string groupOpenId, CancellationToken cancellationToken = default) =>
        QQOfficialGroupMapper.Mutes(await api.SendGroupRequestAsync(HttpMethod.Get, GroupRoute(groupOpenId, "restrict_chat_setting"), null, cancellationToken).ConfigureAwait(false));
    public async Task SetMemberMutesAsync(string groupOpenId, IReadOnlyList<QOfficialMemberMute> members,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count is < 1 or > 20) throw new ArgumentException("A mute batch must contain 1 to 20 members.", nameof(members));
        var entries = new JsonArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        foreach (var member in members)
        {
            ArgumentNullException.ThrowIfNull(member);
            ArgumentException.ThrowIfNullOrWhiteSpace(member.MemberOpenId);
            if (!ids.Add(member.MemberOpenId)) throw new ArgumentException("Duplicate member OpenID.", nameof(members));
            if (member.Duration < TimeSpan.Zero || member.Duration > TimeSpan.FromDays(30)) throw new ArgumentOutOfRangeException(nameof(members));
            entries.Add(new JsonObject
            {
                ["op"] = member.Duration == TimeSpan.Zero ? "del" : member.UpdateExisting ? "update" : "add",
                ["member_openid"] = member.MemberOpenId,
                ["mute_expire_at"] = member.Duration == TimeSpan.Zero ? "" : Stamp(now.Add(member.Duration))
            });
        }
        await api.SendGroupRequestAsync(HttpMethod.Post, GroupRoute(groupOpenId, "restrict_chat_setting"), new JsonObject { ["members"] = entries }, cancellationToken).ConfigureAwait(false);
    }
    public async Task<QOfficialApprovalStrategyPage> GetApprovalStrategiesAsync(string? cursor = null, int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await api.SendGroupRequestAsync(HttpMethod.Get, Page("v2/groups/join_approval_strategy", cursor, limit), null, cancellationToken).ConfigureAwait(false);
        return new QOfficialApprovalStrategyPage(QQOfficialGroupMapper.Items(result, "strategies").Select(x => QQOfficialGroupMapper.Strategy(x)).ToArray(), QQOfficialGroupMapper.Text(result, "next_cursor"));
    }
    public async Task<QOfficialApprovalStrategy> CreateApprovalStrategyAsync(QOfficialApprovalStrategyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var body = new JsonObject { ["is_enable"] = options.Enabled ? "on" : "off" };
        Groups(body, options.GroupOpenIds, options.GroupIds);
        if (options.ExpiresAt is { } expiry) body["expire_at"] = Stamp(expiry);
        Remark(body, options.Remark);
        return QQOfficialGroupMapper.Strategy(await api.SendGroupRequestAsync(HttpMethod.Post, "v2/groups/join_approval_strategy", body, cancellationToken).ConfigureAwait(false));
    }
    public async Task<QOfficialApprovalStrategy> UpdateApprovalStrategyAsync(string strategyId, QOfficialApprovalStrategyUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var body = new JsonObject();
        if (update.Enabled is { } enabled) body["is_enable"] = enabled ? "on" : "off";
        if (update.ExpiresAt is { } expiry) body["expire_at"] = Stamp(expiry);
        Remark(body, update.Remark);
        if (update.Groups is { } groups)
        {
            var action = new JsonObject { ["op"] = groups.Add ? "add" : "del" };
            Groups(action, groups.GroupOpenIds, groups.GroupIds);
            body["group_action"] = action;
        }
        if (body.Count == 0) throw new ArgumentException("No strategy changes supplied.", nameof(update));
        return QQOfficialGroupMapper.Strategy(await api.SendGroupRequestAsync(HttpMethod.Patch, StrategyRoute(strategyId), body, cancellationToken).ConfigureAwait(false), strategyId);
    }
    public async Task DeleteApprovalStrategyAsync(string strategyId, CancellationToken cancellationToken = default) =>
        await api.SendGroupRequestAsync(HttpMethod.Delete, StrategyRoute(strategyId), null, cancellationToken).ConfigureAwait(false);
    public async Task ExecuteApprovalStrategyAsync(string strategyId, CancellationToken cancellationToken = default) =>
        await api.SendGroupRequestAsync(HttpMethod.Post, StrategyRoute(strategyId) + "/execute", new JsonObject(), cancellationToken).ConfigureAwait(false);
    public async Task<int> UpdateApprovalWhitelistAsync(string strategyId, IReadOnlyList<string> qqNumbers, bool add = true,
        CancellationToken cancellationToken = default)
    {
        var users = Strings(qqNumbers, 10000);
        if (qqNumbers.Any(x => !ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0))
            throw new ArgumentException("Whitelist entries must be QQ numbers.", nameof(qqNumbers));
        var result = await api.SendGroupRequestAsync(HttpMethod.Post, StrategyRoute(strategyId) + "/whitelist_users",
            new JsonObject { ["op"] = add ? "add" : "del", ["whitelist_users"] = users }, cancellationToken).ConfigureAwait(false);
        return QQOfficialGroupMapper.Number(result, "whitelist_user_count");
    }
}
