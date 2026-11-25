using System.Reflection;
using ShiroBot.Adapter.QQPlatform.AdapterImpl;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Dispatch;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Groups;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Official;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Transport;
using ShiroBot.Adapter.QQPlatform.Transport.Gateway;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

[assembly: RequiresShiroBotPackage("shirobot.model.qq", MinimumVersion = "1.0.0")]

namespace ShiroBot.Adapter.QQPlatform;

/// <summary>
///     Composition root for the QQ official adapter. Owns lifecycle and wiring only; gateway dispatch,
///     group names and user mapping live in their own components.
/// </summary>
[BotAdapter("qq-official", Name = "QQPlatformAdapter", Version = "0.5.0",
    Description = "QQ Official Bot OpenAPI adapter", Author = "ShirokaProject",
    Protocol = "qq-official", IsSingleFile = true)]
public sealed class QQPlatformAdapter : IBotAdapter, IConfigurableAdapter, IConfigurableComponent<QQPlatformConfig>
{
    private readonly QQEventService _events = new();
    private readonly QQUserService _users = new();
    private readonly QQChannelService _channels = new();
    private HttpClient? _http;
    private HttpClient? _uploadHttp;
    private QQGatewayConnection? _gateway;
    private QQMessageService? _messages;
    private QQOfficialMessageService? _officialMessages;
    private QQOfficialMediaService? _officialMedia;
    private QQOpenApiClient? _api;
    private QQGroupService? _groups;
    private QQGroupApprovalService? _groupApprovals;
    private QQInboundDispatcher? _dispatcher;
    private string? _selfId;

    public QQPlatformConfig CurrentConfigValue { get; private set; } = new();

    ConfigApplyMode IConfigurableAdapter.ApplyMode => ConfigApplyMode.RestartComponent;

    public string Platform => QQEventTranslator.Platform;
    public IConfigContext Config { get; set; } = null!;
    public IConsoleLogger Logger { get; set; } = null!;
    public IMessageService Message => _messages ?? throw new InvalidOperationException("QQ Official adapter is not started.");
    public IChannelService Channel => _channels;
    public IUserService User => _users;
    public IEventService Event => _events;

    public TService? GetExtension<TService>() where TService : class =>
        _officialMessages as TService ?? _officialMedia as TService ?? _groups as TService
        ?? _groupApprovals as TService
        ?? _api?.Materials as TService ?? _users as TService ?? _messages as TService ?? this as TService;

    public async Task StartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        var buildTime = typeof(QQPlatformAdapter).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildTimeUtc")?.Value;
        Logger.Info($"QQ Official Adapter build time (UTC): {buildTime ?? "unknown"}");
        var config = CurrentConfigValue;
        config.Validate();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _uploadHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var tokens = new QQTokenProvider(_http, config);
        var api = new QQOpenApiClient(_http, config, tokens, _uploadHttp);
        _api = api;
        _groups = new QQGroupService(api);
        _groupApprovals = new QQGroupApprovalService(api);
        _channels.Attach(_groups);
        _users.Attach(api);
        await ResolveIdentityAsync(api).ConfigureAwait(false);

        _messages = new QQMessageService(api, Logger, () => _users.Self,
            TimeSpan.FromMinutes(config.MessageContextRetentionMinutes), config.MessageContextMaxMessages,
            config.GroupMaxPassiveReplies);
        var groupNames = new QQGroupNameCache(api, Logger, config.TraceEvents);
        _officialMedia = new QQOfficialMediaService(api, _messages, Logger, groupNames.GetCached);
        _officialMessages = new QQOfficialMessageService(api, _messages, Logger, groupNames.GetCached, _officialMedia);
        _dispatcher = new QQInboundDispatcher(api, _messages, _events, groupNames, Logger,
            () => _selfId, () => _users.Self, config.TraceEvents);
        _gateway = new QQGatewayConnection(config, api, tokens, _dispatcher.DispatchAsync, OnGatewayReady,
            Logger, _dispatcher.AcknowledgeInteractionEarly, reason =>
                _ = _events.PublishAsync(new BotOfflineEvent { Platform = Platform, SelfId = _selfId, Reason = reason }));
        try
        {
            await _gateway.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
        Logger.Success("QQ Official gateway connected.");
    }

    private async Task ResolveIdentityAsync(QQOpenApiClient api)
    {
        try
        {
            var bot = await api.GetCurrentUserAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(bot.Id))
            {
                _selfId = bot.Id;
                _users.Self = new User(bot.Id) { Name = bot.Username, AvatarUrl = bot.Avatar, IsBot = true };
            }
            Logger.Info($"QQ Official bot identity: id={bot.Id ?? "<missing>"}, " +
                $"username={bot.Username ?? "<missing>"}, bot={bot.Bot}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.Warning($"QQ Official /users/@me lookup failed ({ex.GetType().Name}); " +
                "will use the Gateway READY identity.");
        }
    }

    private void OnGatewayReady(QQUser? user)
    {
        if (string.IsNullOrWhiteSpace(user?.Id)) return;
        var identityWasMissing = string.IsNullOrWhiteSpace(_selfId);
        _selfId = user.Id;
        _users.Self = new User(_selfId)
        {
            Name = user.Username ?? _users.Self?.Name,
            AvatarUrl = user.Avatar ?? _users.Self?.AvatarUrl,
            IsBot = true
        };
        if (identityWasMissing)
            Logger.Info($"QQ Official bot identity from Gateway READY: id={user.Id}, " +
                $"username={user.Username ?? "<missing>"}, bot={user.Bot}");
    }

    public Task OnConfigLoadedAsync(QQPlatformConfig config, CancellationToken cancellationToken)
    {
        NormalizeConfig(config);
        config.Validate();
        CurrentConfigValue = config;
        return Task.CompletedTask;
    }

    public Task OnConfigChangedAsync(
        QQPlatformConfig previous,
        QQPlatformConfig config,
        CancellationToken cancellationToken)
    {
        NormalizeConfig(config);
        config.Validate();
        CurrentConfigValue = config;
        return Task.CompletedTask;
    }

    private void NormalizeConfig(QQPlatformConfig config)
    {
        var changed = config.NormalizeLegacyTokenEndpoint();
        if (config.Intents == 1UL << 25)
        {
            config.Intents |= 1UL << 26;
            changed = true;
        }
        if (changed) Config.Save(config);
    }

    public async Task StopAsync()
    {
        try
        {
            if (_gateway is not null) await _gateway.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _gateway = null;
            _messages?.Dispose();
            _messages = null;
            _officialMessages = null;
            _officialMedia = null;
            _api = null;
            _groups = null;
            _groupApprovals = null;
            _dispatcher?.Clear();
            _dispatcher = null;
            _channels.Attach(null);
            _users.Attach(null);
            _http?.Dispose();
            _http = null;
            _uploadHttp?.Dispose();
            _uploadHttp = null;
            _selfId = null;
            _users.Self = null;
        }
    }
}
