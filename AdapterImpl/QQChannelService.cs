using ShiroBot.Adapter.QQPlatform.AdapterImpl.Groups;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

/// <summary>
///     Maps the SDK channel contract onto the qualified QQ group service. Only groups exist here: direct chats
///     have no members, and QQ offers no way to list the groups a bot is in.
/// </summary>
internal sealed class QQChannelService : IChannelService
{
    private QQGroupService? _groups;

    public void Attach(QQGroupService? groups) => _groups = groups;

    private QQGroupService Groups =>
        _groups ?? throw new InvalidOperationException("QQ Official adapter is not started.");

    public async Task<Channel?> GetChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var info = await Groups.GetGroupInfoAsync(channelId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Channel.Group(channelId) with { Name = info.GroupName };
    }

    public async Task<IReadOnlyList<Member>> GetMembersAsync(string channelId,
        CancellationToken cancellationToken = default) =>
        (await Groups.GetGroupMemberListAsync(channelId, cancellationToken: cancellationToken)
            .ConfigureAwait(false)).Select(ToMember).ToArray();

    public async Task<Member?> GetMemberAsync(string channelId, string userId,
        CancellationToken cancellationToken = default) =>
        ToMember(await Groups.GetGroupMemberInfoAsync(channelId, userId, cancellationToken: cancellationToken)
            .ConfigureAwait(false));

    public Task KickMemberAsync(string channelId, string userId, CancellationToken cancellationToken = default) =>
        Groups.KickMemberAsync(channelId, userId, cancellationToken: cancellationToken);

    /// <summary>A zero duration lifts the mute. QQ allows at most 30 days and only mutes ordinary members.</summary>
    public Task MuteMemberAsync(string channelId, string userId, TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        Groups.MuteMemberAsync(channelId, userId, duration, cancellationToken);

    private static Member ToMember(QGroupMember member) =>
        new(new User(member.UserId) { Name = member.Nickname, IsBot = member.IsBot })
        {
            Nick = string.IsNullOrWhiteSpace(member.Card) ? member.Nickname : member.Card,
            Role = QQIdentityMapper.Role(member.Role),
            JoinedAt = member.JoinTime
        };
}
