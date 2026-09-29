using ShiroBot.SDK.Models;
using ShiroBot.Model.QQ;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

/// <summary>All QQ OpenAPI paths live here so route changes are isolated from services.</summary>
internal static class QQApiRoutes
{
    public const string Gateway = "gateway/bot";

    public static string Messages(Channel channel) => ForChannel(channel, "messages");
    public static string Messages(QOfficialMessageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Id);
        var id = Uri.EscapeDataString(target.Id);
        return target.Scene switch
        {
            QOfficialMessageScene.Direct => $"v2/users/{id}/messages",
            QOfficialMessageScene.Group => $"v2/groups/{id}/messages",
            QOfficialMessageScene.Channel => $"channels/{id}/messages",
            QOfficialMessageScene.ChannelDirect => $"dms/{id}/messages",
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
    }
    public static string Files(Channel channel) => ForChannel(channel, "files");
    public static string UploadPrepare(Channel channel) => ForChannel(channel, "upload_prepare");
    public static string UploadPartFinish(Channel channel) => ForChannel(channel, "upload_part_finish");
    public static string StreamMessages(Channel channel)
    {
        if (channel.Type != ChannelType.Direct)
            throw new NotSupportedException("QQ Official stream_messages supports C2C only.");
        return ForChannel(channel, "stream_messages");
    }
    public static string Message(Channel channel, string messageId) =>
        ForChannel(channel, $"messages/{Uri.EscapeDataString(messageId)}");

    public static string GroupInfo(string groupOpenId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupOpenId);
        return $"v2/groups/{Uri.EscapeDataString(groupOpenId)}/info";
    }

    public static string Interaction(string interactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interactionId);
        return $"interactions/{Uri.EscapeDataString(interactionId)}";
    }


    private static string ForChannel(Channel channel, string resource)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (string.IsNullOrWhiteSpace(channel.Id)) throw new ArgumentException("Channel ID is empty.", nameof(channel));
        var id = Uri.EscapeDataString(channel.Id);
        return channel.Type switch
        {
            ChannelType.Direct => $"v2/users/{id}/{resource}",
            ChannelType.Group => $"v2/groups/{id}/{resource}",
            _ => throw new NotSupportedException($"QQ Official does not support {channel.Type} in this adapter version.")
        };
    }
}
