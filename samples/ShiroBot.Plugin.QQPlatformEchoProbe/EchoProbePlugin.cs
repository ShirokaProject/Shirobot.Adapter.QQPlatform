using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;


namespace ShiroBot.Plugin.QQPlatformEchoProbe;

[BotPlugin("qqplatform.echo-probe", Name = "QQPlatform Echo Probe", Version = "1.0.0")]
public sealed class EchoProbePlugin : PluginBase
{
    protected override void ConfigureRoutes()
    {
        AllCommands.MapExact("#qqping", message => Context.Message.ReplyAsync(message, "QQPlatform pong"));
    }
}
