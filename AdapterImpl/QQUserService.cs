using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

/// <summary>
///     Maps the QQ official user endpoint onto the host identity contract (<see cref="IUserService" />) and the
///     QQ profile contract (<see cref="IQSystemApi" />), which adds the union ID and proactive-message consent.
/// </summary>
internal sealed class QQUserService : IUserService, IQSystemApi
{
    private QQOpenApiClient? _api;

    public void Attach(QQOpenApiClient? api) => _api = api;

    public User? Self { get; set; }

    public Task<User> GetSelfAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Self ?? throw new InvalidOperationException("QQ bot identity is not ready."));
    }

    public async Task<QUserProfile> GetUserProfileAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();
        var api = _api ?? throw new InvalidOperationException("QQ adapter is not running.");
        var user = await api.GetUserInfoAsync(userId, cancellationToken).ConfigureAwait(false);
        return new QUserProfile
        {
            UserId = userId,
            Nickname = user.Username,
            AvatarUrl = user.Avatar,
            UnionId = user.UnionOpenId,
            AllowProactiveMessage = user.AllowProactiveMessage
        };
    }

    public async Task<User?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var profile = await GetUserProfileAsync(userId, cancellationToken).ConfigureAwait(false);
        return new User(profile.UserId) { Name = profile.Nickname, AvatarUrl = profile.AvatarUrl };
    }
}
