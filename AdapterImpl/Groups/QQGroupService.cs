using System.Text.Json.Nodes;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
using ShiroBot.Adapter.QQPlatform.Transport;
using ShiroBot.Model.QQ;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Groups;

/// <summary>Group information, members, moderation, join requests, blacklist and bot state.</summary>
internal sealed class QQGroupService(QQOpenApiClient api) : IQGroupApi
{
    public QGroupCapabilities Capabilities => QGroupCapabilities.MemberMute | QGroupCapabilities.Kick
        | QGroupCapabilities.JoinRequests | QGroupCapabilities.MuteState | QGroupCapabilities.BatchMute
        | QGroupCapabilities.RejectAndBlacklist | QGroupCapabilities.GroupInfo | QGroupCapabilities.Members
        | QGroupCapabilities.MemberBlacklist | QGroupCapabilities.BotState;

    public async Task<QGroup> GetGroupInfoAsync(string groupId, bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var info = await api.GetGroupInfoAsync(groupId, cancellationToken).ConfigureAwait(false);
        return new QGroup
        {
            GroupId = groupId,
            GroupName = info.GroupName,
            MemberCount = info.MemberCount,
            UnionId = info.UnionGroupOpenId,
            Description = info.FingerMemo,
            ClassText = info.ClassText,
            Tags = info.Tags ?? []
        };
    }

    public async Task<QGroupMember> GetGroupMemberInfoAsync(string groupId, string userId, bool noCache = false,
        CancellationToken cancellationToken = default) =>
        QQOfficialGroupMapper.Member(groupId,
            await api.GetGroupMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<QGroupMember>> GetGroupMemberListAsync(string groupId, bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var members = new Dictionary<string, QGroupMember>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await api.GetGroupMembersAsync(groupId, cursor, cancellationToken).ConfigureAwait(false);
            foreach (var user in page.Members ?? [])
            {
                var member = QQOfficialGroupMapper.Member(groupId, user);
                members[member.UserId] = member;
            }
            cursor = page.NextCursor;
            if (!string.IsNullOrEmpty(cursor) && !seen.Add(cursor))
                throw new InvalidDataException("QQ repeated its member pagination cursor.");
        } while (!string.IsNullOrEmpty(cursor));
        return members.Values.ToArray();
    }

    public Task MuteMemberAsync(string groupId, string userId, TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        api.MuteMemberAsync(groupId, userId, duration, cancellationToken);

    public Task KickMemberAsync(string groupId, string userId, bool rejectAddRequest = false,
        CancellationToken cancellationToken = default) =>
        api.RemoveGroupMembersAsync(groupId, [userId], rejectAddRequest, cancellationToken);

    public async Task<QGroupJoinRequestPage> GetJoinRequestsAsync(string groupOpenId, string? cursor = null,
        int limit = 20, CancellationToken cancellationToken = default)
    {
        var result = await api.SendGroupRequestAsync(HttpMethod.Get,
            QQGroupHelpers.Page(QQGroupHelpers.GroupRoute(groupOpenId, "join_request_list"), cursor, limit),
            null, cancellationToken).ConfigureAwait(false);
        return new QGroupJoinRequestPage(QQOfficialGroupMapper.Items(result, "list")
            .Select(x => QQOfficialGroupMapper.JoinRequest(x, groupOpenId)).ToArray(),
            QQOfficialGroupMapper.Text(result, "next_cursor"));
    }

    public Task AcceptJoinRequestAsync(QGroupJoinRequest request, CancellationToken cancellationToken = default) =>
        Review(request, true, null, false, cancellationToken);

    public Task RejectJoinRequestAsync(QGroupJoinRequest request, string? reason = null,
        bool addToBlacklist = false, CancellationToken cancellationToken = default) =>
        Review(request, false, reason, addToBlacklist, cancellationToken);

    private async Task Review(QGroupJoinRequest request, bool approve, string? reason, bool blacklist,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        var body = new JsonObject
        {
            ["op"] = approve ? "approve" : "decline",
            ["join_request_id"] = request.RequestId
        };
        if (!approve)
        {
            body["add_to_member_blacklist"] = blacklist;
            if (reason is not null) body["reject_reason"] = reason;
        }
        await api.SendGroupRequestAsync(HttpMethod.Post,
            QQGroupHelpers.GroupRoute(request.GroupId,
                $"approval_join_request/{QQGroupHelpers.Id(request.UserId)}"),
            body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QGroupMuteState> GetMuteStateAsync(string groupOpenId,
        CancellationToken cancellationToken = default) =>
        QQOfficialGroupMapper.Mutes(await api.SendGroupRequestAsync(HttpMethod.Get,
            QQGroupHelpers.GroupRoute(groupOpenId, "restrict_chat_setting"), null, cancellationToken)
            .ConfigureAwait(false));

    public Task<QGroupBlacklistPage> GetGroupBlacklistAsync(string groupOpenId, string? cursor = null,
        int limit = 20, CancellationToken cancellationToken = default) =>
        GuardAsync("群黑名单查询", async () =>
        {
            if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
            var page = await api.GetGroupBlacklistAsync(groupOpenId, cursor, limit, cancellationToken)
                .ConfigureAwait(false);
            return new QGroupBlacklistPage(
                (page.Users ?? []).Select(QQOfficialGroupMapper.BlacklistedMember).ToArray(), page.NextCursor);
        });

    public Task<IReadOnlyList<string>> UpdateGroupBlacklistAsync(string groupOpenId,
        IReadOnlyList<string> userIds, bool add = true, CancellationToken cancellationToken = default) =>
        GuardAsync("群黑名单操作", async () =>
        {
            ArgumentNullException.ThrowIfNull(userIds);
            if (userIds.Count is < 1 or > 20 || userIds.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Expected 1 to 20 non-empty OpenIDs.", nameof(userIds));
            if (userIds.Distinct(StringComparer.Ordinal).Count() != userIds.Count)
                throw new ArgumentException("Duplicate user ID.", nameof(userIds));
            return await api.UpdateGroupBlacklistAsync(groupOpenId, userIds, add, cancellationToken)
                .ConfigureAwait(false);
        });

    public Task<QGroupBotState> GetBotStateAsync(string groupOpenId,
        CancellationToken cancellationToken = default) =>
        GuardAsync("机器人群内状态", async () =>
            QQOfficialGroupMapper.BotState(groupOpenId, await api.GetGroupBotStateAsync(groupOpenId, cancellationToken)
                .ConfigureAwait(false)));

    private static async Task<T> GuardAsync<T>(string operation, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (QQApiException ex) when (ex.IsPermissionDenied)
        {
            throw new QPermissionDeniedException(operation, ex.ErrorCode, ex.Message);
        }
    }

    public Task<QBatchOperationResult> SetMemberMutesAsync(string groupId, IReadOnlyList<QMemberMute> members,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count is < 1 or > 20) throw new ArgumentException("Expected 1 to 20 members.", nameof(members));
        QQGroupHelpers.Id(groupId);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            ArgumentNullException.ThrowIfNull(member);
            ArgumentException.ThrowIfNullOrWhiteSpace(member.UserId);
            if (!ids.Add(member.UserId)) throw new ArgumentException("Duplicate user ID.", nameof(members));
            if (member.Duration < TimeSpan.Zero || member.Duration > TimeSpan.FromDays(30))
                throw new ArgumentOutOfRangeException(nameof(members));
        }
        return QBatchMuteExecutor.ExecuteAsync(members,
            (member, token) => api.MuteMemberAsync(groupId, member.UserId, member.Duration, token),
            ex => ex is QQApiException { StatusCode: not null } error && (int)error.StatusCode.Value < 500,
            cancellationToken);
    }
}
