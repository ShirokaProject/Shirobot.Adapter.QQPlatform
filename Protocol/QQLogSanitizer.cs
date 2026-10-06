namespace ShiroBot.Adapter.QQPlatform.Protocol;

internal static class QQLogSanitizer
{
    public static string RedactBase64(string message)
    {
        var index = message.IndexOf("base64:", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? message : message[..index] + "[Base64 内容已省略]";
    }
}
