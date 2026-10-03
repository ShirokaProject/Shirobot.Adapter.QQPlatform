namespace ShiroBot.Adapter.QQPlatform.Protocol;

/// <summary>The gateway closed the socket with a QQ-specific close code.</summary>
internal sealed class QQGatewayClosedException(int? code, string? description)
    : IOException($"QQ gateway closed the socket: code {code?.ToString() ?? "none"}; {description ?? "no reason"}")
{
    public int? Code { get; } = code;
}

internal readonly record struct QQGatewayCloseAction(
    bool Fatal, bool ClearSession, bool RefreshToken, TimeSpan? Delay, string Reason);

/// <summary>Maps QQ gateway close codes to a reconnect strategy.</summary>
internal static class QQGatewayClosePolicy
{
    public static readonly TimeSpan RateLimitDelay = TimeSpan.FromSeconds(60);

    public static QQGatewayCloseAction Decide(int? code) => code switch
    {
        // Sandbox-only or banned bot: reconnecting cannot help.
        4914 => new(true, false, false, null, "bot is offline or sandbox-only (4914)"),
        4915 => new(true, false, false, null, "bot is banned (4915)"),
        4004 => new(false, false, true, null, "invalid token (4004)"),
        4008 => new(false, false, false, RateLimitDelay, "rate limited (4008)"),
        4006 => new(false, true, true, null, "session no longer valid (4006)"),
        4007 => new(false, true, true, null, "invalid seq on resume (4007)"),
        4009 => new(false, true, true, null, "session timed out (4009)"),
        >= 4900 and <= 4913 => new(false, true, true, null, $"internal error ({code})"),
        _ => new(false, false, false, null, $"close code {code?.ToString() ?? "none"}")
    };
}
