using System.Reflection;
using System.Text.Json;
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

[BotAdapter("qq-official", Name = "QQPlatformAdapter", Version = "0.3.0",
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
    private QQOfficialGroupService? _officialGroups;
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
        _officialMessages as TService ?? _officialGroups as TService ?? _messages as TService ?? this as TService;

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
        _officialGroups = new QQOfficialGroupService(api);
        _channels.Attach(api);
        try
        {
            var bot = await api.GetCurrentUserAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(bot.Id))
            {
                _selfId = bot.Id;
                _users.Self = new User(bot.Id) { Name = bot.Username, IsBot = true };
            }
            Logger.Info($"QQ Official bot identity: id={bot.Id ?? "<missing>"}, " +
                $"username={bot.Username ?? "<missing>"}, bot={bot.Bot}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.Warning($"QQ Official /users/@me lookup failed ({ex.GetType().Name}); " +
                "will use the Gateway READY identity.");
        }
        _messages = new QQMessageService(api, Logger, () => _users.Self);
        _officialMessages = new QQOfficialMessageService(api, _messages, Logger, GetCachedGroupName);
        _gateway = new QQGatewayClient(config, api, tokens, DispatchAsync, user =>
        {
            if (!string.IsNullOrWhiteSpace(user?.Id))
            {
                var identityWasMissing = string.IsNullOrWhiteSpace(_selfId);
                _selfId = user.Id;
                _users.Self = new User(_selfId) { Name = user.Username ?? _users.Self?.Name, IsBot = true };
                if (identityWasMissing)
                    Logger.Info($"QQ Official bot identity from Gateway READY: id={user.Id}, " +
                        $"username={user.Username ?? "<missing>"}, bot={user.Bot}");
            }
        }, Logger, AcknowledgeInteractionEarly, reason =>
            _ = _events.PublishAsync(new BotOfflineEvent { Platform = Platform, SelfId = _selfId, Reason = reason }));
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
        _officialGroups = null;
        _channels.Attach(null);
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
            if (translated is InteractionEvent platformEvent)
            {
                try
                {
                    await _api!.AcknowledgeInteractionAsync(platformEvent.InteractionId).ConfigureAwait(false);
                    platformEvent = platformEvent with { Acknowledgement = InteractionAcknowledgement.Acknowledged };
                }
                catch (Exception ex)
                {
                    platformEvent = platformEvent with { Acknowledgement = ex is QQApiException ? InteractionAcknowledgement.Failed : InteractionAcknowledgement.Unknown };
                    Logger.Warning($"QQ interaction acknowledgement failed: {ex.Message}");
                }
                _messages?.RememberInteraction(platformEvent);
                translated = platformEvent;
                if (platformEvent.Channel?.Type == ChannelType.Group)
                {
                    var groupName = await GetGroupNameAsync(platformEvent.Channel.Id).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(groupName))
                        translated = platformEvent with { Channel = platformEvent.Channel with { Name = groupName } };
                }
            }
            if (translated is MessageEvent { IsDirect: false } groupMessage)
            {
                LogMentionDiagnostics(payload, groupMessage);
                var name = await GetGroupNameAsync(groupMessage.Channel.Id).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(name))
                    translated = groupMessage with { Channel = groupMessage.Channel with { Name = name } };
            }
            if (translated is MessageEvent message && _messages is not null)
                translated = _messages.RegisterIncoming(message);
            if (translated is not null) await _events.PublishAsync(translated).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"QQ Official event {payload.EventType} failed: {ex.Message}");
        }
    }

    /// <summary>QQ expects the button callback to be answered quickly, so do it as soon as the event arrives.</summary>
    private void AcknowledgeInteractionEarly(GatewayPayload payload)
    {
        if (payload.EventType != "INTERACTION_CREATE" || _api is not { } api) return;
        var interaction = payload.Data.Deserialize<QQInteractionData>(QQJson.Options);
        if (interaction is not { Type: 11, Id: { Length: > 0 } id }) return;
        _ = Task.Run(async () =>
        {
            try { await api.AcknowledgeInteractionAsync(id).ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warning($"QQ interaction acknowledgement failed: {ex.Message}"); }
        });
    }

    private void LogMentionDiagnostics(GatewayPayload payload, MessageEvent message)
    {
        var mentionCount = 0;
        var botMentionCount = 0;
        if (payload.Data.ValueKind == JsonValueKind.Object
            && payload.Data.TryGetProperty("mentions", out var mentions)
            && mentions.ValueKind == JsonValueKind.Array)
        {
            foreach (var mention in mentions.EnumerateArray())
            {
                mentionCount++;
                if (mention.ValueKind == JsonValueKind.Object
                    && mention.TryGetProperty("bot", out var bot)
                    && bot.ValueKind == JsonValueKind.True)
                    botMentionCount++;
            }
        }

        var mentionSegments = message.Segments.OfType<MentionSegment>().Count();
        var selfName = _users.Self?.Name;
        var botNameMatches = 0;
        if (!string.IsNullOrWhiteSpace(selfName)
            && payload.Data.ValueKind == JsonValueKind.Object
            && payload.Data.TryGetProperty("mentions", out var mentionUsers)
            && mentionUsers.ValueKind == JsonValueKind.Array)
        {
            botNameMatches = mentionUsers.EnumerateArray().Count(mention =>
                mention.ValueKind == JsonValueKind.Object
                && mention.TryGetProperty("bot", out var bot)
                && bot.ValueKind == JsonValueKind.True
                && mention.TryGetProperty("username", out var username)
                && username.ValueKind == JsonValueKind.String
                && string.Equals(username.GetString(), selfName, StringComparison.Ordinal));
        }
        if (payload.EventType != "GROUP_AT_MESSAGE_CREATE" && mentionCount == 0 && mentionSegments == 0)
            return;

        var selfIdReady = !string.IsNullOrWhiteSpace(_selfId);
        var selfMentioned = selfIdReady && message.HasMention(_selfId!);
        Logger.Info($"QQ Official @ diagnostic: event={payload.EventType}, selfIdReady={selfIdReady}, " +
            $"selfMentioned={selfMentioned}, mentionMetadata={mentionCount}, botMetadata={botMentionCount}, " +
            $"botNameMatches={botNameMatches}, " +
            $"mentionSegments={mentionSegments}");
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
    public Task<User> GetSelfAsync(CancellationToken cancellationToken = default) => cancellationToken.IsCancellationRequested ? Task.FromCanceled<User>(cancellationToken) : Task.FromResult(Self ?? throw new InvalidOperationException("QQ bot identity is not ready."));
}
