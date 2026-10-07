using System.Globalization;
using System.Text.Json.Nodes;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Model.QQ;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

internal sealed class QQOfficialGroupService(QQOpenApiClient api) : IQGroupApi, IQGroupApprovalStrategyApi
{
    public QGroupCapabilities Capabilities => QGroupCapabilities.MemberMute | QGroupCapabilities.Kick
        | QGroupCapabilities.JoinRequests | QGroupCapabilities.MuteState | QGroupCapabilities.BatchMute
        | QGroupCapabilities.RejectAndBlacklist | QGroupCapabilities.GroupInfo | QGroupCapabilities.Members;

    public async Task<QGroup> GetGroupInfoAsync(string groupId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        var info = await api.GetGroupInfoAsync(groupId, cancellationToken).ConfigureAwait(false);
        return new QGroup { GroupId = groupId, GroupName = info.GroupName, MemberCount = info.MemberCount };
    }
    public async Task<QGroupMember> GetGroupMemberInfoAsync(string groupId, string userId, bool noCache = false, CancellationToken cancellationToken = default) =>
        ToMember(groupId, await api.GetGroupMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false));
    public async Task<IReadOnlyList<QGroupMember>> GetGroupMemberListAsync(string groupId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        var members = new Dictionary<string, QGroupMember>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await api.GetGroupMembersAsync(groupId, cursor, cancellationToken).ConfigureAwait(false);
            foreach (var user in page.Members ?? [])
            {
                var member = ToMember(groupId, user);
                members[member.UserId] = member;
            }
            cursor = page.NextCursor;
            if (!string.IsNullOrEmpty(cursor) && !seen.Add(cursor)) throw new InvalidDataException("QQ repeated its member pagination cursor.");
        } while (!string.IsNullOrEmpty(cursor));
        return members.Values.ToArray();
    }
    private static QGroupMember ToMember(string groupId, ShiroBot.Adapter.QQPlatform.Wire.QQUser user)
    {
        var id = user.MemberOpenId ?? user.Id ?? throw new InvalidDataException("QQ member has no OpenID.");
        return new QGroupMember
        {
            GroupId = groupId, UserId = id, Nickname = user.Username,
            Role = user.MemberRole switch { "admin" => QGroupRole.Admin, "owner" => QGroupRole.Owner, "member" => QGroupRole.Member, _ => QGroupRole.Unknown },
            JoinTime = DateTimeOffset.TryParse(user.JoinedAt, out var joined) ? joined : null
        };
    }

    public Task MuteMemberAsync(string groupId, string userId, TimeSpan duration, CancellationToken cancellationToken = default) =>
        MuteOneAsync(groupId, userId, duration, cancellationToken);

    public Task KickMemberAsync(string groupId, string userId, bool rejectAddRequest = false, CancellationToken cancellationToken = default) =>
        api.RemoveGroupMembersAsync(groupId, [userId], rejectAddRequest, cancellationToken);

    private Task MuteOneAsync(string groupId, string userId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var channels = new QQChannelService();
        channels.Attach(api);
        return channels.MuteMemberCoreAsync(groupId, userId, duration, cancellationToken);
    }

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
    private static void Groups(JsonObject body, IReadOnlyList<string>? openIds, IReadOnlyList<string>? ids)
    {
        if ((openIds is null) == (ids is null)) throw new ArgumentException("Specify either group OpenIDs or group numbers.");
        if (openIds is not null) body["group_openids"] = Strings(openIds, 100);
        else
        {
            if (ids!.Count is 0 or > 100 || ids.Any(x => !ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0)) throw new ArgumentException("Expected 1 to 100 group numbers.");
            body["group_ids"] = new JsonArray(ids.Select(x => (JsonNode?)JsonValue.Create(ulong.Parse(x, CultureInfo.InvariantCulture))).ToArray());
        }
    }
    private static void Remark(JsonObject body, string? remark)
    {
        if (remark is null) return;
        if (remark.EnumerateRunes().Count() > 255) throw new ArgumentException("Strategy remark exceeds 255 characters.", nameof(remark));
        body["remark"] = remark;
    }

    public async Task<QGroupJoinRequestPage> GetJoinRequestsAsync(string groupOpenId, string? cursor = null,
        int limit = 20, CancellationToken cancellationToken = default)
    {
        var result = await api.SendGroupRequestAsync(HttpMethod.Get, Page(GroupRoute(groupOpenId, "join_request_list"), cursor, limit), null, cancellationToken).ConfigureAwait(false);
        return new QGroupJoinRequestPage(QQOfficialGroupMapper.Items(result, "list")
            .Select(x => QQOfficialGroupMapper.JoinRequest(x, groupOpenId)).ToArray(), QQOfficialGroupMapper.Text(result, "next_cursor"));
    }
    public Task AcceptJoinRequestAsync(QGroupJoinRequest request, CancellationToken cancellationToken = default) =>
        Review(request, true, null, false, cancellationToken);
    public Task RejectJoinRequestAsync(QGroupJoinRequest request, string? reason = null, bool addToBlacklist = false,
        CancellationToken cancellationToken = default) => Review(request, false, reason, addToBlacklist, cancellationToken);
    private async Task Review(QGroupJoinRequest request, bool approve, string? reason, bool blacklist, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        var body = new JsonObject { ["op"] = approve ? "approve" : "decline", ["join_request_id"] = request.RequestId };
        if (!approve)
        {
            body["add_to_member_blacklist"] = blacklist;
            if (reason is not null) body["reject_reason"] = reason;
        }
        await api.SendGroupRequestAsync(HttpMethod.Post, GroupRoute(request.GroupId, $"approval_join_request/{Id(request.UserId)}"), body, token).ConfigureAwait(false);
    }
    public async Task<QGroupMuteState> GetMuteStateAsync(string groupOpenId, CancellationToken cancellationToken = default) =>
        QQOfficialGroupMapper.Mutes(await api.SendGroupRequestAsync(HttpMethod.Get, GroupRoute(groupOpenId, "restrict_chat_setting"), null, cancellationToken).ConfigureAwait(false));
    public Task<QBatchOperationResult> SetMemberMutesAsync(string groupId, IReadOnlyList<QMemberMute> members,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count is < 1 or > 20) throw new ArgumentException("Expected 1 to 20 members.", nameof(members));
        Id(groupId);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            ArgumentNullException.ThrowIfNull(member);
            ArgumentException.ThrowIfNullOrWhiteSpace(member.UserId);
            if (!ids.Add(member.UserId)) throw new ArgumentException("Duplicate user ID.", nameof(members));
            if (member.Duration < TimeSpan.Zero || member.Duration > TimeSpan.FromDays(30)) throw new ArgumentOutOfRangeException(nameof(members));
        }
        return QBatchMuteExecutor.ExecuteAsync(members,
            (member, token) => MuteOneAsync(groupId, member.UserId, member.Duration, token), ex => ex is QQApiException { StatusCode: not null } error && (int)error.StatusCode.Value < 500, cancellationToken);
    }

    public async Task<QApprovalStrategyPage> GetApprovalStrategiesAsync(string? cursor = null, int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await api.SendGroupRequestAsync(HttpMethod.Get, Page("v2/groups/join_approval_strategy", cursor, limit), null, cancellationToken).ConfigureAwait(false);
        return new QApprovalStrategyPage(QQOfficialGroupMapper.Items(result, "strategies").Select(x => QQOfficialGroupMapper.Strategy(x)).ToArray(), QQOfficialGroupMapper.Text(result, "next_cursor"));
    }
    public async Task<QApprovalStrategy> CreateApprovalStrategyAsync(QApprovalStrategyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var body = new JsonObject { ["is_enable"] = options.Enabled ? "on" : "off" };
        Groups(body, options.GroupIds, options.GroupNumbers);
        if (options.ExpiresAt is { } expiry) body["expire_at"] = Stamp(expiry);
        Remark(body, options.Remark);
        return QQOfficialGroupMapper.Strategy(await api.SendGroupRequestAsync(HttpMethod.Post, "v2/groups/join_approval_strategy", body, cancellationToken).ConfigureAwait(false));
    }
    public async Task<QApprovalStrategy> UpdateApprovalStrategyAsync(string strategyId, QApprovalStrategyUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var body = new JsonObject();
        if (update.Enabled is { } enabled) body["is_enable"] = enabled ? "on" : "off";
        if (update.ExpiresAt.IsSpecified)
        {
            // No verified reset mapping in this adapter: do not silently drop the requested clear.
            if (update.ExpiresAt.IsClear) throw new NotSupportedException("This adapter does not implement clearing a strategy expiry.");
            body["expire_at"] = Stamp(update.ExpiresAt.Value);
        }
        if (update.Remark.IsSpecified) Remark(body, update.Remark.IsClear ? string.Empty : update.Remark.Value);
        if (update.Groups is { } groups)
        {
            var action = new JsonObject { ["op"] = groups.Add ? "add" : "del" };
            Groups(action, groups.GroupIds, groups.GroupNumbers);
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
