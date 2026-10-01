using ShiroBot.SDK.Config;

namespace ShiroBot.Adapter.QQPlatform;

[ConfigModel]
public sealed class QQPlatformConfig
{
    [ConfigField("QQ 开放平台中该机器人的 AppID，按字符串填写。",
        Label = "AppID", Placeholder = "必填",
        Group = "credentials", GroupLabel = "机器人凭证", GroupOrder = 10, Order = 10)]
    public string AppId { get; set; } = string.Empty;

    [ConfigField("同一机器人应用的 AppSecret，请勿提交到公开仓库。",
        Label = "AppSecret", Placeholder = "必填", Type = "password",
        Group = "credentials", GroupLabel = "机器人凭证", GroupOrder = 10, Order = 20)]
    public string AppSecret { get; set; } = string.Empty;

    [ConfigField("事件订阅位掩码，必须大于 0。默认 100663296 即 (1 << 25) | (1 << 26)：群聊与单聊消息，以及按钮等互动事件。",
        Label = "事件订阅 Intents", Min = 1, Default = 100663296L,
        Group = "events", GroupLabel = "事件", GroupOrder = 20, Order = 10)]
    public ulong Intents { get; set; } = (1UL << 25) | (1UL << 26);

    [ConfigField("是否在日志中输出收到的事件类型，排查问题时开启。",
        Label = "输出事件日志", Default = false,
        Group = "events", GroupLabel = "事件", GroupOrder = 20, Order = 20)]
    public bool TraceEvents { get; set; }

    [ConfigField("当前分片编号，从 0 开始且小于分片总数。单实例填 0。",
        Label = "分片编号", Min = 0, Default = 0,
        Group = "shard", GroupLabel = "分片", GroupOrder = 30, Order = 10)]
    [ConfigVisibleWhen(nameof(ShardCount), ConfigConditionOperator.GreaterThan, "1")]
    public int ShardId { get; set; }

    [ConfigField("分片总数，必须大于 0。单实例填 1。",
        Label = "分片总数", Min = 1, Default = 1,
        Group = "shard", GroupLabel = "分片", GroupOrder = 30, Order = 20)]
    public int ShardCount { get; set; } = 1;

    [ConfigField("与 QQ 网关断开后重连的最短等待时间，单位秒。",
        Label = "最短重连间隔（秒）", Min = 1, Default = 2,
        Group = "connection", GroupLabel = "连接", GroupOrder = 40, Order = 10)]
    public int ReconnectMinimumSeconds { get; set; } = 2;

    [ConfigField("重连等待时间逐次翻倍，最多等待这么久，单位秒；不得小于最短重连间隔。",
        Label = "最长重连间隔（秒）", Min = 1, Default = 30,
        Group = "connection", GroupLabel = "连接", GroupOrder = 40, Order = 20)]
    public int ReconnectMaximumSeconds { get; set; } = 30;

    [ConfigField("启动时等待网关就绪的超时时间，单位秒。",
        Label = "启动超时（秒）", Min = 1, Default = 30,
        Group = "connection", GroupLabel = "连接", GroupOrder = 40, Order = 30)]
    public int StartupTimeoutSeconds { get; set; } = 30;

    [ConfigField("获取访问令牌的 HTTPS 地址，通常无需修改。",
        Label = "令牌接口地址", Default = "https://api.bot.qq.com/app/getAppAccessToken",
        Group = "advanced", GroupLabel = "高级", GroupOrder = 50, Order = 10)]
    public string TokenEndpoint { get; set; } = "https://api.bot.qq.com/app/getAppAccessToken";

    [ConfigField("QQ OpenAPI 的 HTTPS 基础地址，通常无需修改。",
        Label = "OpenAPI 地址", Default = "https://api.bot.qq.com/",
        Group = "advanced", GroupLabel = "高级", GroupOrder = 50, Order = 20)]
    public string ApiBaseUrl { get; set; } = "https://api.bot.qq.com/";

    public bool NormalizeLegacyTokenEndpoint()
    {
        if (!string.Equals(TokenEndpoint, "https://bots.qq.com/app/getAppAccessToken", StringComparison.OrdinalIgnoreCase))
            return false;
        TokenEndpoint = "https://api.bot.qq.com/app/getAppAccessToken";
        return true;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AppId) || string.IsNullOrWhiteSpace(AppSecret))
            throw new ArgumentException("QQ Official requires AppId and AppSecret in config.toml.");
        if (Intents == 0) throw new ArgumentOutOfRangeException(nameof(Intents));
        if (ShardCount <= 0 || ShardId < 0 || ShardId >= ShardCount)
            throw new ArgumentOutOfRangeException(nameof(ShardId), "ShardId must be within ShardCount.");
        if (ReconnectMinimumSeconds <= 0 || ReconnectMaximumSeconds < ReconnectMinimumSeconds)
            throw new ArgumentOutOfRangeException(nameof(ReconnectMaximumSeconds));
        if (StartupTimeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(StartupTimeoutSeconds));
        if (!Uri.TryCreate(TokenEndpoint, UriKind.Absolute, out var token) || token.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("TokenEndpoint must be an HTTPS URL.");
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var api) || api.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("ApiBaseUrl must be an HTTPS URL.");
    }
}
