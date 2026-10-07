using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal static class QQIdentityMapper
{
    public static MemberRole Role(string? value) => value?.ToLowerInvariant() switch
    {
        "owner" => MemberRole.Owner,
        "admin" => MemberRole.Admin,
        "member" => MemberRole.Member,
        _ => MemberRole.Unknown
    };
}
