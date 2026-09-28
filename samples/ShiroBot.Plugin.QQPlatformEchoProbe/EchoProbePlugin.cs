using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

[assembly: ShiroBotApiCompatibility("0.9", "0.9")]

namespace ShiroBot.Plugin.QQPlatformEchoProbe;

[BotPlugin("qqplatform.echo-probe", Name = "QQPlatform Echo Probe", Version = "1.0.0")]
public sealed class EchoProbePlugin : PluginBase
{
    protected override void ConfigureRoutes()
    {
        AllCommands.MapExact("#qqping", message => Context.Message.ReplyAsync(message, "QQPlatform pong"));
    }
}
