using System.Reflection;
using ShiroBot.Adapter.QQPlatform.AdapterImpl;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

[assembly: ShiroBotApiCompatibility("0.9.1", "0.9.1")]
[assembly: RequiresShiroBotPackage("shirobot.model.qq", MinimumVersion = "0.9.2")]

namespace ShiroBot.Adapter.QQPlatform;

[BotAdapter("qq-official", Name = "QQPlatformAdapter", Version = "0.2.0",
    Description = "QQ Official Bot OpenAPI adapter", Author = "ShirokaProject",
    Protocol = "qq-official", IsSingleFile = true)]
public sealed class QQPlatformAdapter : IBotAdapter, IConfigurableAdapter, IConfigurableComponent<QQPlatformConfig>
{
    private readonly QQEventService _events = new();
    private readonly QQUserService _users = new();
    private readonly QQChannelService _channels = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _seen = [];
    private readonly Dictionary<string, (string? Name, DateTimeOffset ExpiresAt)> _groupNames = [];
    private HttpClient? _http;
    private HttpClient? _uploadHttp;
    private QQGatewayClient? _gateway;
    private QQMessageService? _messages;
    private QQOfficialMessageService? _officialMessages;
    private QQOpenApiClient? _api;
    private string? _selfId;
    private QQPlatformConfig? _config;

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
        _officialMessages as TService ?? _messages as TService ?? this as TService;

    public async Task StartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        var buildTime = typeof(QQPlatformAdapter).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildTimeUtc")?.Value;
        Logger.Info($"QQ Official Adapter build time (UTC): {buildTime ?? "unknown"}");
        var config = CurrentConfigValue;
        config.Validate();
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _uploadHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var tokens = new QQTokenProvider(_http, config);
        var api = new QQOpenApiClient(_http, config, tokens, _uploadHttp);
        _api = api;
        _messages = new QQMessageService(api, Logger);
        _officialMessages = new QQOfficialMessageService(api, _messages, Logger, GetCachedGroupName);
        _gateway = new QQGatewayClient(config, api, tokens, DispatchAsync, user =>
        {
            _selfId = user?.Id;
            if (!string.IsNullOrWhiteSpace(_selfId))
                _users.Self = new User(_selfId) { Name = user?.Username, IsBot = true };
        }, Logger);
        try { await _gateway.StartAsync().ConfigureAwait(false); }
        catch { await StopAsync().ConfigureAwait(false); throw; }
        Logger.Success("QQ Official gateway connected.");
    }

    public Task OnConfigLoadedAsync(QQPlatformConfig config, CancellationToken cancellationToken)
    {
        NormalizeConfig(config);
        config.Validate();
        CurrentConfigValue = config;
        _config = config;
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
        _config = config;
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
        if (_gateway is not null) await _gateway.StopAsync().ConfigureAwait(false);
        _gateway = null;
        _messages = null;
        _officialMessages = null;
        _api = null;
        _http?.Dispose();
        _http = null;
        _uploadHttp?.Dispose();
        _uploadHttp = null;
        _selfId = null;
        _users.Self = null;
        _config = null;
        _seen.Clear();
        _seenOrder.Clear();
        _groupNames.Clear();
    }

    private async Task DispatchAsync(GatewayPayload payload)
    {
        if (payload.Op != 0) return;
        if (_config?.TraceEvents == true) Logger.Info($"QQ Official event: {payload.EventType}");
        var id = payload.Id;
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (!_seen.Add(id)) return;
            _seenOrder.Enqueue(id);
            if (_seenOrder.Count > 4096) _seen.Remove(_seenOrder.Dequeue());
        }
        try
        {
            var translated = QQEventTranslator.Translate(payload, _selfId, _users.Self?.Name);
            if (translated is PlatformEvent
                { Kind: QEventKinds.OfficialButtonInteraction, Raw: QOfficialButtonInteraction interaction } platformEvent)
            {
                try { await _api!.AcknowledgeInteractionAsync(interaction.InteractionId).ConfigureAwait(false); }
                catch (Exception ex) { Logger.Warning($"QQ interaction acknowledgement failed: {ex.Message}"); }
                if (platformEvent.Channel?.Type == ChannelType.Group)
                {
                    var groupName = await GetGroupNameAsync(platformEvent.Channel.Id).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(groupName))
                        translated = platformEvent with { Channel = platformEvent.Channel with { Name = groupName } };
                }
            }
            if (translated is MessageEvent { IsDirect: false } groupMessage)
            {
                var name = await GetGroupNameAsync(groupMessage.Channel.Id).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(name))
                    translated = groupMessage with { Channel = groupMessage.Channel with { Name = name } };
            }
            if (translated is MessageEvent message) _messages?.RegisterIncoming(message);
            if (translated is not null) await _events.PublishAsync(translated).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"QQ Official event {payload.EventType} failed: {ex.Message}");
        }
    }

    private async Task<string?> GetGroupNameAsync(string groupOpenId)
    {
        if (_groupNames.TryGetValue(groupOpenId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Name;
        string? name = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            name = await (_api?.GetGroupNameAsync(groupOpenId, timeout.Token) ?? Task.FromResult<string?>(null)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_config?.TraceEvents == true) Logger.Warning($"QQ group name lookup failed: {ex.Message}");
        }
        _groupNames[groupOpenId] = (name, DateTimeOffset.UtcNow.Add(name is null ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1)));
        return name;
    }

    private string? GetCachedGroupName(string groupOpenId) =>
        _groupNames.TryGetValue(groupOpenId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow
            ? cached.Name : null;
}

internal sealed class QQUserService : IUserService
{
    public User? Self { get; set; }
    public Task<User> GetSelfAsync() => Task.FromResult(Self ?? throw new InvalidOperationException("QQ bot identity is not ready."));
}

internal sealed class QQChannelService : IChannelService;
