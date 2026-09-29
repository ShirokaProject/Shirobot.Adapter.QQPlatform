using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

[assembly: ShiroBotApiCompatibility("0.9", "0.9")]
[assembly: RequiresShiroBotPackage("shirobot.model.qq", MinimumVersion = "0.9.2")]

namespace ShiroBot.Plugin.QQPlatformRichDemo;

[BotPlugin("qqplatform.rich-demo", Name = "QQPlatform Rich Demo", Version = "1.0.0",
    Description = "QQ 官方机器人富消息能力示例", SharedAssemblies = "ShiroBot.Model.QQ")]
public sealed class RichDemoPlugin : PluginBase
{
    private const string Article = """
        # ShiroBot 富消息演示

        这是通过上游 IQOfficialMessageApi 发送的 Markdown 消息。

        ## 可测试的能力

        1. `#imagetest`：发送配置中的远程图片。
        2. `#recalltest`：发送消息，三秒后撤回。
        3. `#actiontest`：查看文本命令菜单。

        菜单支持原生按钮回调；输入状态和流式消息可在私聊中测试。
        """;

    private RichDemoConfig _config = new();
    private IDisposable? _configWatcher;

    protected override void ConfigureRoutes()
    {
        Map("imagetest", SendImageAsync);
        Map("mdtest", message => SendPageAsync(message.Channel, message.Sender.Id,
            new QOfficialMessageReply { MessageId = message.MessageId }, "article"));
        Map("recalltest", SendAndRecallAsync);
        Map("typingtest", SendTypingDemoAsync);
        Map("streamtest", SendStreamDemoAsync);
        Map("actiontest", message => SendPageAsync(message.Channel, message.Sender.Id,
            new QOfficialMessageReply { MessageId = message.MessageId }, "main"));
        Map("action/features", message => SendPageAsync(message.Channel, message.Sender.Id,
            new QOfficialMessageReply { MessageId = message.MessageId }, "features"));
        Map("action/tools", message => SendPageAsync(message.Channel, message.Sender.Id,
            new QOfficialMessageReply { MessageId = message.MessageId }, "tools"));
        Map("action/status", message => SendPageAsync(message.Channel, message.Sender.Id,
            new QOfficialMessageReply { MessageId = message.MessageId }, "status"));
        Map("action/help", message => SendPageAsync(message.Channel, message.Sender.Id,
            new QOfficialMessageReply { MessageId = message.MessageId }, "help"));
        Map("rich", message => ReplyAsync(message, HelpMenu));
        GroupCommands.MapWhen(message => !message.Sender.IsBot
                && message.SelfId is { Length: > 0 } selfId
                && message.HasMention(selfId),
            HandleMentionAsync);
        Events.MapPlatform(QEventKinds.OfficialButtonInteraction, HandleInteractionAsync);
    }

    protected override Task LoadAsync()
    {
        _config = Context.Config.Load<RichDemoConfig>();
        Context.Config.Save(_config);
        _configWatcher = Context.Config.Watch<RichDemoConfig>(updated => _config = updated);
        return Task.CompletedTask;
    }

    protected override Task OnUnloadAsync()
    {
        _configWatcher?.Dispose();
        _configWatcher = null;
        return Task.CompletedTask;
    }

    private void Map(string command, Func<MessageEvent, Task> handler)
    {
        AllCommands.MapExact($"#{command}", message => HandleAsync(message, handler));
        AllCommands.MapExact($"/{command}", message => HandleAsync(message, handler));
    }

    private static Task HandleAsync(MessageEvent message, Func<MessageEvent, Task> handler) =>
        message.Sender.IsBot ? Task.CompletedTask : handler(message);

    private Task ReplyAsync(MessageEvent message, string text) =>
        Context.Message.ReplyAsync(message, text);

    private Task HandleMentionAsync(MessageEvent message) =>
        Context.Message.QuoteReplyAsync(message,
            new MentionSegment(message.Sender.Id)
            {
                DisplayName = message.Member?.Nick ?? message.Sender.Name
            },
            new TextSegment("收到你的 @ 了，这是对原消息的引用回复。"));

    private async Task SendImageAsync(MessageEvent message)
    {
        var imageUrl = _config.ImageUrl.Trim();
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            await ReplyAsync(message, "请先在插件 config.toml 中设置公网 HTTP(S) 图片地址 image_url。")
                .ConfigureAwait(false);
            return;
        }

        await Context.Message.QuoteReplyAsync(message, new ImageSegment(uri.AbsoluteUri))
            .ConfigureAwait(false);
    }

    private Task HandleInteractionAsync(PlatformEvent evt)
    {
        if (evt.Raw is not QOfficialButtonInteraction interaction || evt.Channel is null)
            return Task.CompletedTask;
        if (!interaction.ButtonData.StartsWith("rich:", StringComparison.Ordinal)) return Task.CompletedTask;
        var page = interaction.ButtonData["rich:".Length..];
        if (page is not ("main" or "features" or "tools" or "status" or "help" or "article"))
            return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(interaction.EventId))
        {
            BotLog.Warning("RichDemo 按钮事件缺少 Gateway 事件 ID，无法发送被动回复。");
            return Task.CompletedTask;
        }
        return SendPageAsync(evt.Channel, interaction.UserId,
            new QOfficialMessageReply { EventId = interaction.EventId }, page);
    }

    private async Task SendPageAsync(Channel channel, string userId, QOfficialMessageReply? reply, string page)
    {
        var (content, keyboard) = CreatePage(channel, userId, page);
        var target = channel.Type switch
        {
            ChannelType.Group => new QOfficialMessageTarget(QOfficialMessageScene.Group, channel.Id),
            ChannelType.Direct => new QOfficialMessageTarget(QOfficialMessageScene.Direct, channel.Id),
            _ => null
        };
        var official = Context.GetAdapterExtension<IQOfficialMessageApi>();
        var markdown = new QCustomMarkdown(content);
        if (target is null || official?.CanSendMarkdown(target, markdown, keyboard) != true)
        {
            if (target is not null)
                await SendPlainTextPageAsync(official, target, reply, page, content).ConfigureAwait(false);
            return;
        }

        try
        {
            await official.SendMarkdownAsync(target, markdown, keyboard, reply)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"RichDemo 富消息发送失败，改用普通文本：{ex.Message}");
            await SendPlainTextPageAsync(official, target, reply, page, content).ConfigureAwait(false);
        }
    }

    private async Task SendPlainTextPageAsync(IQOfficialMessageApi? official,
        QOfficialMessageTarget target, QOfficialMessageReply? reply, string page, string content)
    {
        if (official is null || reply is null) return;
        try
        {
            await official.SendTextAsync(target, PlainTextPage(page, content), reply).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"RichDemo 普通文本回复失败：{ex.Message}");
        }
    }

    private static (string Content, QInlineKeyboard Keyboard) CreatePage(Channel channel, string userId, string page) => page switch
    {
        "main" => ("# ShiroBot 交互控制中心\n\n选择下面的功能或工具。",
            Keyboard([Callback("features", "功能中心", "features"), Callback("tools", "工具菜单", "tools")],
                [Callback("article", "阅读文章", "article"), Link("docs", ".NET 文档", "https://learn.microsoft.com/dotnet/")])),
        "features" => ("## 功能中心\n\n查看运行状态与使用指南。",
            Keyboard([Callback("status", "运行状态", "status", users: [userId]),
                    Callback("help", "使用指南", "help")],
                [Callback("back-main", "返回首页", "main", QKeyboardButtonStyle.Gray)])),
        "tools" => ("## 工具菜单\n\nMarkdown、图片和撤回演示。",
            Keyboard([Callback("article", "Markdown", "article"), Command("images", "图片测试", "#imagetest")],
                [Command("recall", "撤回测试", "#recalltest"), Callback("back-main", "返回首页", "main", QKeyboardButtonStyle.Gray)])),
        "status" => ($"## 运行状态\n\n平台：{channel.Type}\n会话：{channel.Name ?? channel.Id}\n交互回调：可用",
            Keyboard([Callback("back-features", "返回功能", "features", QKeyboardButtonStyle.Gray),
                Callback("back-main", "返回首页", "main", QKeyboardButtonStyle.Gray)])),
        "help" => ("## 使用指南\n\n点击按钮进入下一页，也可以发送 `#actiontest`、`#mdtest`、`#imagetest`、`#recalltest`。",
            Keyboard([Callback("back-features", "返回功能", "features", QKeyboardButtonStyle.Gray),
                Callback("back-main", "返回首页", "main", QKeyboardButtonStyle.Gray)])),
        "article" => (Article,
            Keyboard([Callback("back-main", "返回首页", "main", QKeyboardButtonStyle.Gray),
                Link("docs", ".NET 文档", "https://learn.microsoft.com/dotnet/")])),
        _ => throw new ArgumentOutOfRangeException(nameof(page))
    };

    private static QInlineKeyboard Keyboard(params QKeyboardButton[][] rows) =>
        new(rows.Select(row => new QKeyboardRow(row)).ToArray());

    private static QKeyboardButton Callback(string id, string label, string page,
        QKeyboardButtonStyle style = QKeyboardButtonStyle.Blue, IReadOnlyList<string>? users = null) =>
        Button(id, label, QKeyboardActionType.Callback, $"rich:{page}", style, users);

    private static QKeyboardButton Command(string id, string label, string command) =>
        Button(id, label, QKeyboardActionType.Command, command, QKeyboardButtonStyle.Blue, enter: true);

    private static QKeyboardButton Link(string id, string label, string url) =>
        Button(id, label, QKeyboardActionType.Jump, url, QKeyboardButtonStyle.Gray);

    private static QKeyboardButton Button(string id, string label, QKeyboardActionType type, string data,
        QKeyboardButtonStyle style, IReadOnlyList<string>? users = null, bool? enter = null) => new()
    {
        Id = id,
        RenderData = new QKeyboardRenderData(label, label, style),
        Action = new QKeyboardAction
        {
            Type = type,
            Data = data,
            Permission = new QKeyboardPermission
            {
                Type = users is null ? QKeyboardPermissionType.Everyone : QKeyboardPermissionType.SpecifiedUsers,
                SpecifyUserIds = users
            },
            UnsupportTips = "请更新 QQ 客户端",
            Enter = enter
        }
    };

    private static string PlainTextPage(string page, string content) => page switch
    {
        "main" => MainMenu,
        "features" => FeaturesMenu,
        "tools" => ToolsMenu,
        "help" => HelpMenu,
        "article" => Article,
        _ => content
    };

    private async Task SendAndRecallAsync(MessageEvent message)
    {
        var sent = await Context.Message.QuoteReplyAsync(message, "这条消息将在 3 秒后撤回。")
            .ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        try
        {
            await Context.Message.DeleteMessageAsync(message.Channel, sent.MessageId).ConfigureAwait(false);
            BotLog.Info($"已撤回演示消息 {sent.MessageId}");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"撤回演示失败：{ex.Message}");
            await ReplyAsync(message, "撤回失败，请查看 CLI 日志。").ConfigureAwait(false);
        }
    }

    private async Task SendTypingDemoAsync(MessageEvent message)
    {
        if (message.Channel.Type != ChannelType.Direct)
        {
            await ReplyAsync(message, "输入状态仅支持 QQ 私聊，请在私聊中发送 #typingtest。").ConfigureAwait(false);
            return;
        }
        var qq = Context.GetAdapterExtension<IQOfficialDirectMessageApi>();
        if (qq is null)
        {
            await ReplyAsync(message, "当前适配器不支持 QQ 官方私聊输入状态。").ConfigureAwait(false);
            return;
        }
        try
        {
            await qq.SendTypingAsync(
                new QOfficialMessageTarget(QOfficialMessageScene.Direct, message.Channel.Id),
                new QOfficialMessageReply { MessageId = message.MessageId },
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await Context.Message.QuoteReplyAsync(message, "输入状态演示结束。").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"输入状态演示失败：{ex.Message}");
            await ReplyAsync(message, "输入状态发送失败，请查看 CLI 日志。").ConfigureAwait(false);
        }
    }

    private async Task SendStreamDemoAsync(MessageEvent message)
    {
        if (message.Channel.Type != ChannelType.Direct)
        {
            await ReplyAsync(message, "流式消息仅支持 QQ 私聊，请在私聊中发送 #streamtest。").ConfigureAwait(false);
            return;
        }
        var qq = Context.GetAdapterExtension<IQOfficialDirectMessageApi>();
        if (qq is null)
        {
            await ReplyAsync(message, "当前适配器不支持 QQ 官方私聊流式消息。").ConfigureAwait(false);
            return;
        }
        try
        {
            await using var stream = qq.BeginStream(
                new QOfficialMessageTarget(QOfficialMessageScene.Direct, message.Channel.Id),
                new QOfficialMessageReply { MessageId = message.MessageId });
            await stream.AppendAsync("QQPlatform").ConfigureAwait(false);
            await stream.AppendAsync("QQPlatform 流式").ConfigureAwait(false);
            await stream.AppendAsync("QQPlatform 流式消息演示完成。 ").ConfigureAwait(false);
            await stream.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"流式消息演示失败：{ex.Message}");
            await ReplyAsync(message, "流式消息发送失败，请查看 CLI 日志。").ConfigureAwait(false);
        }
    }

    private const string MainMenu = "功能中心：#action/features\n工具菜单：#action/tools\n使用指南：#action/help\n文章：#mdtest";
    private const string FeaturesMenu = "功能中心\n运行状态：#action/status\n使用指南：#action/help\n返回首页：#actiontest";
    private const string ToolsMenu = "工具菜单\nMarkdown：#mdtest\n图片：#imagetest\n撤回：#recalltest\n输入状态：#typingtest\n流式消息：#streamtest\n返回首页：#actiontest";
    private const string HelpMenu = "RichDemo 命令：#actiontest、#mdtest、#imagetest、#recalltest、#typingtest、#streamtest。支持 / 前缀。Markdown 菜单附带原生按钮；QQ 平台未开放权限时回退为文本命令。";
}

public sealed class RichDemoConfig
{
    [ConfigField("imagetest 使用的公网 HTTP(S) 图片地址", Label = "测试图片 URL")]
    public string ImageUrl { get; set; } = "";
}
