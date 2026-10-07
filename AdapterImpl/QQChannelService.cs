using System.Globalization;
using System.Runtime.ExceptionServices;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

/// <summary>
/// Group queries and moderation through the QQ OpenAPI. Only groups exist here: direct chats have no members,
/// and QQ offers no way to list the groups a bot is in. Several of these endpoints are whitelisted by QQ
/// (error 11253), so callers should expect <see cref="QQApiException"/>.
/// </summary>
internal sealed class QQChannelService : IChannelService
{
    private static readonly TimeSpan MaxMuteDuration = TimeSpan.FromDays(30);
    private const int MaxMemberPages = 100;

    private QQOpenApiClient? _api;

    public void Attach(QQOpenApiClient? api) => _api = api;

    private QQOpenApiClient Api => _api ?? throw new InvalidOperationException("QQ Official adapter is not started.");

    public async Task<Channel?> GetChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var info = await Api.GetGroupInfoAsync(channelId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Channel.Group(channelId) with { Name = info.GroupName };
    }

    public async Task<IReadOnlyList<Member>> GetMembersAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var members = new List<Member>();
        string? cursor = null;
        for (var page = 0; page < MaxMemberPages; page++)
        {
            var result = await Api.GetGroupMembersAsync(channelId, cursor, cancellationToken: cancellationToken).ConfigureAwait(false);
            members.AddRange((result.Members ?? []).Select(ToMember));
            if (string.IsNullOrEmpty(result.NextCursor)) break;
            cursor = result.NextCursor;
        }
        return members;
    }

    public async Task<Member?> GetMemberAsync(string channelId, string userId, CancellationToken cancellationToken = default) =>
        ToMember(await Api.GetGroupMemberAsync(channelId, userId, cancellationToken: cancellationToken).ConfigureAwait(false));

    public Task KickMemberAsync(string channelId, string userId, CancellationToken cancellationToken = default) =>
        Api.RemoveGroupMembersAsync(channelId, [userId], addToBlacklist: false, cancellationToken: cancellationToken);

    /// <summary>A zero duration lifts the mute. QQ allows at most 30 days and only mutes ordinary members.</summary>
    public Task MuteMemberAsync(string channelId, string userId, TimeSpan duration, CancellationToken cancellationToken = default) => MuteMemberCoreAsync(channelId, userId, duration, cancellationToken);

    internal async Task MuteMemberCoreAsync(string channelId, string userId, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration < TimeSpan.Zero || duration > MaxMuteDuration)
            throw new ArgumentOutOfRangeException(nameof(duration), "QQ mutes last from 0 (unmute) up to 30 days.");
        if (duration == TimeSpan.Zero)
        {
            await Api.SetMemberMuteAsync(channelId, new QQMemberMuteState("del", userId, string.Empty), cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }
        var expireAt = DateTimeOffset.Now.Add(duration).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        try
        {
            await Api.SetMemberMuteAsync(channelId, new QQMemberMuteState("add", userId, expireAt), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (QQApiException addError) when (addError.StatusCode is not null && (int)addError.StatusCode.Value < 500)
        {
            // Someone who is already muted needs "update" instead of "add"; if that fails too, the first error is the useful one.
            try { await Api.SetMemberMuteAsync(channelId, new QQMemberMuteState("update", userId, expireAt), cancellationToken: cancellationToken).ConfigureAwait(false); }
            catch (QQApiException) { ExceptionDispatchInfo.Capture(addError).Throw(); }
        }
    }

    private static Member ToMember(QQUser user)
    {
        var id = user.MemberOpenId ?? user.Id ?? throw new InvalidDataException("QQ group member has no openid.");
        return new Member(new User(id) { Name = user.Username, IsBot = user.Bot })
        {
            Nick = user.Username,
            Role = QQIdentityMapper.Role(user.MemberRole),
            JoinedAt = DateTimeOffset.TryParse(user.JoinedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var joined)
                ? joined : null
        };
    }
}
