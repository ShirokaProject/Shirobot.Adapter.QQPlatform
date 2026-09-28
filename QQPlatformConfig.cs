namespace ShiroBot.Adapter.QQPlatform;

public sealed class QQPlatformConfig
{
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
    public ulong Intents { get; set; } = (1UL << 25) | (1UL << 26);
    public int ShardId { get; set; }
    public int ShardCount { get; set; } = 1;
    public string TokenEndpoint { get; set; } = "https://api.bot.qq.com/app/getAppAccessToken";
    public string ApiBaseUrl { get; set; } = "https://api.bot.qq.com/";
    public bool TraceEvents { get; set; }
    public int ReconnectMinimumSeconds { get; set; } = 2;
    public int ReconnectMaximumSeconds { get; set; } = 30;
    public int StartupTimeoutSeconds { get; set; } = 30;

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
