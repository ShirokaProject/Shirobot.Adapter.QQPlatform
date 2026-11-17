using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

[assembly: RequiresShiroBotPackage("shirobot.model.qq", MinimumVersion = "1.0.0")]

namespace ShiroBot.Plugin.QQPlatformRichDemo;

[BotPlugin("qqplatform.rich-demo", Name = "QQPlatform Rich Demo", Version = "2.1.0",
    Description = "QQ 官方机器人能力演示", SharedAssemblies = "ShiroBot.Model.QQ")]
public sealed partial class RichDemoPlugin : PluginBase<RichDemoConfig>
{
    private const string HelpMenu = """
        #about 头像、名称、群昵称与 OpenID
        #userinfo 私聊用户资料
        #groupinfo 群资料、统一标识、机器人状态、黑名单
        #mdtest Markdown 与按钮
        #cardtest 图文卡片（SDK CardSegment）
        #imagetest 图片
        #mediaimage 图片文件
        #mediaaudio 语音 mp3
        #mediavideo 视频 mp4
        #mediafile PDF 文件
        #mediatest 依次发送图片、语音、视频、文件
        #replytext 文本回复
        #replyimage 图片回复
        #replymd 富媒体回复
        #replyaudio 语音回复
        #replyvideo 视频回复
        #replyfile 文件回复
        #materialtest 图片素材
        #alltest @全体
        #typingtest 输入状态
        #streamtest 流式消息
        #recalltest 撤回
        #capabilities 能力探测
        """;

    private CancellationToken Stopping => Context.Lifetime.Stopping;

    protected override void ConfigureRoutes()
    {
        Map("rich", SendMenuAsync);
        Map("about", SendAboutAsync);
        Map("me", SendAboutAsync);
        Map("whoami", SendAboutAsync);
        Map("userinfo", SendUserInfoAsync);
        Map("groupinfo", SendGroupInfoAsync);
        Map("mdtest", SendGenericMarkdownAsync);
        Map("genericmdtest", SendGenericMarkdownAsync);
        Map("actiontest", SendGenericMarkdownAsync);
        Map("replytext", message => ReplyAsync(message, "文本回复测试：引用刚收到的那条消息。"));
        Map("replyimage", SendImageAssetAsync);
        Map("replymd", SendGenericMarkdownAsync);
        Map("replyaudio", message => SendMediaCommandAsync(message, "demo.mp3", quote: true));
        Map("replyvideo", message => SendMediaCommandAsync(message, "demo.mp4", quote: true));
        Map("replyfile", message => SendMediaCommandAsync(message, "demo.pdf", quote: true));
        Map("cardtest", SendCardDemoAsync);
        Map("imagetest", SendImageAsync);
        Map("mediaimage", SendImageAssetAsync);
        Map("mediaaudio", message => SendMediaCommandAsync(message, "demo.mp3"));
        Map("mediavideo", message => SendMediaCommandAsync(message, "demo.mp4"));
        Map("mediafile", message => SendMediaCommandAsync(message, "demo.pdf"));
        Map("mediatest", SendAllMediaAsync);
        Map("materialtest", SendMaterialDemoAsync);
        Map("capabilities", SendCapabilitiesAsync);
        Map("alltest", message => SendEveryoneAsync(message, markdown: false));
        Map("allmdtest", message => SendEveryoneAsync(message, markdown: true));
        Map("typingtest", SendTypingDemoAsync);
        Map("streamtest", SendStreamDemoAsync);
        Map("recalltest", SendAndRecallAsync);

        MapArgument("materiallist", ListMaterialsAsync);
        MapArgument("materialdelete", DeleteMaterialAsync);

        Events.MapWhen<InteractionEvent>(
            interaction => interaction.Platform == "qq-official"
                && interaction.Kind == QEventKinds.OfficialButtonInteraction
                && interaction.Data?.StartsWith("rich:", StringComparison.Ordinal) == true,
            "RichDemo 菜单按钮", HandleInteractionAsync);
    }

    private void Map(string command, Func<MessageEvent, Task> handler)
    {
        AllCommands.MapExact($"#{command}", message => HandleAsync(message, handler));
        AllCommands.MapExact($"/{command}", message => HandleAsync(message, handler));
    }

    private void MapArgument(string command, Func<MessageEvent, string, Task> handler)
    {
        foreach (var prefix in new[] { "#", "/" })
        {
            var route = $"{prefix}{command} ";
            AllCommands.MapPrefix(route,
                message => HandleAsync(message, incoming => handler(incoming, Argument(incoming, route))));
        }
    }

    private static string Argument(MessageEvent message, string route)
    {
        var text = message.GetPlainText().Trim();
        return text.StartsWith(route, StringComparison.Ordinal) ? text[route.Length..].Trim() : string.Empty;
    }

    private async Task HandleAsync(MessageEvent message, Func<MessageEvent, Task> handler)
    {
        if (message.Sender.IsBot || message.Platform != "qq-official") return;
        try
        {
            await handler(message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Stopping.IsCancellationRequested)
        {
        }
        catch (QLimitExceededException ex)
        {
            await ReplyAsync(message, $"{ex.Operation}: 今日次数已用完。").ConfigureAwait(false);
        }
        catch (QPermissionDeniedException ex)
        {
            await ReplyAsync(message, $"{ex.Operation}: 当前机器人未获权限。").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BotLog.Warning($"RichDemo 演示失败（{ex.GetType().Name}），未自动重试。");
            await ReplyAsync(message, "演示失败，请检查配置及宿主日志。不会自动重发，以免重复发送。")
                .ConfigureAwait(false);
        }
    }

    private Task ReplyAsync(MessageEvent message, OutgoingMessage content) =>
        Context.Message.ReplyAsync(message, WithReply(message, content), Stopping);

    private Task ReplyAsync(MessageEvent message, string text) =>
        ReplyAsync(message, new OutgoingMessage { Segments = [new TextSegment(text)] });

    private static OutgoingMessage WithReply(MessageEvent message, OutgoingMessage content) =>
        content with { ReplyTo = message.Reference };

    private static OutgoingMessage MenuMessage() => new()
    {
        Segments =
        [
            new MarkdownSegment("## RichDemo\n\n每个命令演示一项能力，支持 `#` 或 `/` 前缀。" +
                "群内可测试真实群资料与内邀接口状态。")
        ],
        Buttons = new MessageButtonLayout(
        [
            new MessageButtonRow(
            [
                new MessageButton("about", "关于我", new CallbackAction("rich:about")),
                new MessageButton("md", "Markdown", new CallbackAction("rich:md")),
                new MessageButton("caps", "能力", new CallbackAction("rich:caps"))
            ]),
            new MessageButtonRow(
            [
                new MessageButton("group", "群资料", new CallbackAction("rich:group")),
                new MessageButton("image", "图片", new CallbackAction("rich:image")),
                new MessageButton("help", "命令", new CallbackAction("rich:help"))
            ]),
            new MessageButtonRow(
            [
                new MessageButton("replytext", "文本回复", new CallbackAction("rich:reply:text")),
                new MessageButton("replyimage", "图片回复", new CallbackAction("rich:reply:image")),
                new MessageButton("replymd", "富媒体回复", new CallbackAction("rich:reply:md"))
            ]),
            new MessageButtonRow(
            [
                new MessageButton("mediaimage", "图片", new CallbackAction("rich:test:image")),
                new MessageButton("mediaaudio", "语音", new CallbackAction("rich:test:audio")),
                new MessageButton("mediavideo", "视频", new CallbackAction("rich:test:video")),
                new MessageButton("mediafile", "文件", new CallbackAction("rich:test:filemedia"))
            ]),
            new MessageButtonRow(
            [
                new MessageButton("replyaudio", "语音回复", new CallbackAction("rich:reply:audio")),
                new MessageButton("replyvideo", "视频回复", new CallbackAction("rich:reply:video")),
                new MessageButton("replyfile", "文件回复", new CallbackAction("rich:reply:file"))
            ])
        ])
    };

    private Task SendMenuAsync(MessageEvent message) => ReplyAsync(message, MenuMessage());

    private async Task HandleInteractionAsync(InteractionEvent interaction)
    {
        try
        {
            var channel = interaction.Channel ?? throw new InvalidOperationException("Interaction has no channel.");
            OutgoingMessage reply;
            switch (interaction.Data)
            {
                case "rich:about":
                    reply = ProfileMessage(interaction.User, channel, null, "按钮交互", null,
                        interaction.InstanceId);
                    break;
                case "rich:md":
                    reply = CreateMarkdownDemo();
                    break;
                case "rich:caps":
                    reply = CapabilitiesMessage(channel, interaction.InstanceId);
                    break;
                case "rich:group":
                    reply = new OutgoingMessage
                    {
                        Segments = [new TextSegment(await GroupInfoTextAsync(channel).ConfigureAwait(false))]
                    };
                    break;
                case "rich:test:official":
                    reply = await TestOfficialMessageAsync(channel, EventReply(interaction)).ConfigureAwait(false);
                    break;
                case "rich:test:media":
                    reply = await TestMediaAsync(channel, EventReply(interaction)).ConfigureAwait(false);
                    break;
                case "rich:test:material":
                    reply = await TestMaterialAsync(channel, interaction.InstanceId, interaction.User,
                        EventReply(interaction)).ConfigureAwait(false);
                    break;
                case "rich:test:user":
                    reply = await TestUserAsync(channel, interaction.User).ConfigureAwait(false);
                    break;
                case "rich:test:file":
                    reply = await TestFileUploadAsync(channel).ConfigureAwait(false);
                    break;
                case "rich:test:image":
                    reply = await TestMediaAsync(channel, EventReply(interaction)).ConfigureAwait(false);
                    break;
                case "rich:test:audio":
                    reply = await TestMediaAssetAsync(channel, EventReply(interaction),
                        QOfficialMediaType.Audio, "demo.mp3").ConfigureAwait(false);
                    break;
                case "rich:test:video":
                    reply = await TestMediaAssetAsync(channel, EventReply(interaction),
                        QOfficialMediaType.Video, "demo.mp4").ConfigureAwait(false);
                    break;
                case "rich:test:filemedia":
                    reply = await TestMediaAssetAsync(channel, EventReply(interaction),
                        QOfficialMediaType.File, "demo.pdf").ConfigureAwait(false);
                    break;
                case "rich:test:stream":
                    reply = await TestStreamAsync(channel, interaction.MessageId).ConfigureAwait(false);
                    break;
                case "rich:test:group":
                    reply = await TestGroupAsync(channel).ConfigureAwait(false);
                    break;
                case "rich:test:caps":
                    reply = CapabilitiesMessage(channel, interaction.InstanceId);
                    break;
                case "rich:image":
                    reply = ImageMessage(Settings.ImageUrl);
                    break;
                case "rich:reply:text":
                    reply = new OutgoingMessage { Segments = [new TextSegment("文本回复测试：引用刚收到的那条消息。")] };
                    break;
                case "rich:reply:image":
                    reply = ImageMessage(Settings.ImageUrl);
                    break;
                case "rich:reply:md":
                    reply = CreateMarkdownDemo();
                    break;
                case "rich:reply:audio":
                    reply = await TestMediaAssetAsync(channel, EventReply(interaction),
                        QOfficialMediaType.Audio, "demo.mp3").ConfigureAwait(false);
                    break;
                case "rich:reply:video":
                    reply = await TestMediaAssetAsync(channel, EventReply(interaction),
                        QOfficialMediaType.Video, "demo.mp4").ConfigureAwait(false);
                    break;
                case "rich:reply:file":
                    reply = await TestMediaAssetAsync(channel, EventReply(interaction),
                        QOfficialMediaType.File, "demo.pdf").ConfigureAwait(false);
                    break;
                default:
                    reply = new OutgoingMessage { Segments = [new MarkdownSegment($"**命令**\n\n{HelpMenu}")] };
                    break;
            }
            await Context.Message.ReplyAsync(interaction, reply, Stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Stopping.IsCancellationRequested)
        {
        }
        catch (QLimitExceededException ex)
        {
            await Context.Message.ReplyAsync(interaction,
                new OutgoingMessage { Segments = [new TextSegment($"{ex.Operation}: 今日次数已用完。")] },
                Stopping).ConfigureAwait(false);
        }
        catch (QPermissionDeniedException ex)
        {
            await Context.Message.ReplyAsync(interaction,
                new OutgoingMessage { Segments = [new TextSegment($"{ex.Operation}: 当前机器人未获权限。")] },
                Stopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BotLog.Warning($"RichDemo 按钮回复失败（{ex.GetType().Name}），未重复确认或重发。");
        }
    }

    private static QOfficialMessageReply EventReply(InteractionEvent interaction) =>
        new() { EventId = interaction.InteractionId };
}
