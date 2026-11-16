using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

/// <summary>Maps the QQ official user endpoints onto the host identity contracts.</summary>
internal sealed class QQUserService : IUserService, IQOfficialUserApi
{
    private QQOpenApiClient? _api;

    public void Attach(QQOpenApiClient? api) => _api = api;

    public User? Self { get; set; }

    public Task<User> GetSelfAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Self ?? throw new InvalidOperationException("QQ bot identity is not ready."));
    }

    public async Task<QOfficialUserInfo> GetUserInfoAsync(string userOpenId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userOpenId);
        cancellationToken.ThrowIfCancellationRequested();
        var api = _api ?? throw new InvalidOperationException("QQ adapter is not running.");
        var user = await api.GetUserInfoAsync(userOpenId, cancellationToken).ConfigureAwait(false);
        return new QOfficialUserInfo(userOpenId)
        {
            Username = user.Username,
            AvatarUrl = user.Avatar,
            UnionOpenId = user.UnionOpenId,
            AllowProactiveMessage = user.AllowProactiveMessage
        };
    }

    public async Task<User?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await GetUserInfoAsync(userId, cancellationToken).ConfigureAwait(false);
        return new User(user.UserOpenId) { Name = user.Username, AvatarUrl = user.AvatarUrl };
    }
}
