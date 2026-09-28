using ShiroBot.Adapter.QQPlatform.AdapterImpl;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.QQPlatform.Contracts;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

[assembly: ShiroBotApiCompatibility("0.9", "0.9")]

namespace ShiroBot.Adapter.QQPlatform;

[BotAdapter("qq-official", Name = "QQPlatformAdapter", Version = "0.1.0",
    Description = "QQ Official Bot OpenAPI adapter", Author = "ShirokaProject",
    Protocol = "qq-official", IsSingleFile = true,
    SharedAssemblies = "ShiroBot.QQPlatform.Contracts")]
public sealed class QQPlatformAdapter : IBotAdapter
{
    private readonly QQEventService _events = new();
    private readonly QQUserService _users = new();
    private readonly QQChannelService _channels = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _seen = [];
    private readonly Dictionary<string, (string? Name, DateTimeOffset ExpiresAt)> _groupNames = [];
    private HttpClient? _http;
    private QQGatewayClient? _gateway;
    private QQMessageService? _messages;
    private QQOpenApiClient? _api;
    private string? _selfId;
    private QQPlatformConfig? _config;

    public string Platform => QQEventTranslator.Platform;
    public IConfigContext Config { get; set; } = null!;
    public IConsoleLogger Logger { get; set; } = null!;
    public IMessageService Message => _messages ?? throw new InvalidOperationException("QQ Official adapter is not started.");
    public IChannelService Channel => _channels;
    public IUserService User => _users;
    public IEventService Event => _events;

    public TService? GetExtension<TService>() where TService : class =>
        _messages as TService ?? this as TService;

    public async Task StartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        var config = Config.Load<QQPlatformConfig>();
        var configChanged = config.NormalizeLegacyTokenEndpoint();
        if (config.Intents == 1UL << 25)
        {
            config.Intents |= 1UL << 26;
            configChanged = true;
        }
        if (configChanged) Config.Save(config);
        config.Validate();
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var tokens = new QQTokenProvider(_http, config);
        var api = new QQOpenApiClient(_http, config, tokens);
        _api = api;
        _messages = new QQMessageService(api, Logger);
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

    public async Task StopAsync()
    {
        if (_gateway is not null) await _gateway.StopAsync().ConfigureAwait(false);
        _gateway = null;
        _messages = null;
        _api = null;
        _http?.Dispose();
        _http = null;
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
            var translated = QQEventTranslator.Translate(payload, _selfId);
            if (translated is QQInteractionEvent interaction)
            {
                try { await _api!.AcknowledgeInteractionAsync(interaction.InteractionId).ConfigureAwait(false); }
                catch (Exception ex) { Logger.Warning($"QQ interaction acknowledgement failed: {ex.Message}"); }
                if (interaction.Channel.Type == ChannelType.Group)
                {
                    var groupName = await GetGroupNameAsync(interaction.Channel.Id).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(groupName))
                        translated = interaction with { Channel = interaction.Channel with { Name = groupName } };
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
}

internal sealed class QQUserService : IUserService
{
    public User? Self { get; set; }
    public Task<User> GetSelfAsync() => Task.FromResult(Self ?? throw new InvalidOperationException("QQ bot identity is not ready."));
}

internal sealed class QQChannelService : IChannelService;
