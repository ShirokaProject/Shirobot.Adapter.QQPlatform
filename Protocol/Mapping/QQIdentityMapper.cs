using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol.Mapping;

internal static class QQIdentityMapper
{
    public static MemberRole Role(string? value) => value?.ToLowerInvariant() switch
    {
        "owner" => MemberRole.Owner,
        "admin" => MemberRole.Admin,
        "member" => MemberRole.Member,
        _ => MemberRole.Unknown
    };

    public static MemberRole Role(QGroupRole role) => role switch
    {
        QGroupRole.Owner => MemberRole.Owner,
        QGroupRole.Admin => MemberRole.Admin,
        QGroupRole.Member => MemberRole.Member,
        _ => MemberRole.Unknown
    };
}
