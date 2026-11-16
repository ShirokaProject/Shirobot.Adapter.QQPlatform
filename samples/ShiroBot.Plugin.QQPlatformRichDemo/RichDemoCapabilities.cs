using System.IO;
using System.Text.Json;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Plugin.QQPlatformRichDemo;

public sealed partial class RichDemoPlugin
{
    private const string DocsUrl = "https://bot.q.qq.com/wiki/develop/api-v2/changelog.html";

    private const string SamplePng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";

    private static QOfficialMessageTarget Target(Channel channel) => channel.Type switch
    {
        ChannelType.Group => new QOfficialMessageTarget(QOfficialMessageScene.Group, channel.Id),
        ChannelType.Direct => new QOfficialMessageTarget(QOfficialMessageScene.Direct, channel.Id),
        _ => throw new NotSupportedException("RichDemo 只支持 QQ 群聊和私聊。")
    };

    private static QOfficialMessageTarget Target(MessageEvent message) => Target(message.Channel);

    private async Task<bool> RequireAdminAsync(MessageEvent message)
    {
        if (Context.InstanceId is { Length: > 0 } instanceId
            && Context.IsAdmin(new UserReference(instanceId, message.Sender.Id)))
            return true;
        await ReplyAsync(message, "这个演示会通知全群或修改素材，需要宿主 owner_list/admin_list 中的管理员权限。")
            .ConfigureAwait(false);
        return false;
    }

    private Task SendAboutAsync(MessageEvent message) =>
        SendProfileAsync(message, message.Sender, message.Member?.Nick, "当前消息事件");

    private Task SendProfileAsync(MessageEvent message, User user, string? groupNickname, string source,
        string? details = null) =>
        ReplyAsync(message,
            ProfileMessage(user, message.Channel, groupNickname, source, details, message.InstanceId));

    private OutgoingMessage ProfileMessage(User user, Channel channel, string? groupNickname, string source,
        string? details = null, string? instanceId = null)
    {
        var text = $"关于我（{source}）\n名称: {user.Name ?? "未提供"}\n" +
            $"群昵称: {groupNickname ?? "不适用/未提供"}\n" +
            $"{(channel.Type == ChannelType.Direct ? "user_openid" : "member_openid")}: {user.Id}\n" +
            $"实例: {instanceId ?? "未提供"}\n宿主管理员: {Context.IsAdmin(user.Id)}" +
            (details is null ? "" : $"\n{details}");
        var segments = new List<MessageSegment> { new TextSegment(text) };
        if (Uri.TryCreate(user.AvatarUrl, UriKind.Absolute, out var avatar)
            && avatar.Scheme is "http" or "https")
            segments.Add(new ImageSegment(avatar.AbsoluteUri));
        else
            segments.Add(new TextSegment("\n头像: 未提供"));
        return new OutgoingMessage { Segments = segments };
    }

    private async Task SendUserInfoAsync(MessageEvent message)
    {
        if (!message.IsDirect)
        {
            await ReplyAsync(message, "请在私聊中执行 #userinfo。群内 member_openid 不能当作 user_openid 查询；群内资料请用 #about。")
                .ConfigureAwait(false);
            return;
        }
        var users = Context.GetAdapterExtension<IQOfficialUserApi>();
        if (users is null)
        {
            await ReplyAsync(message, "当前适配器未提供 IQOfficialUserApi。").ConfigureAwait(false);
            return;
        }
        var user = await users.GetUserInfoAsync(message.Sender.Id, Stopping).ConfigureAwait(false);
        await SendProfileAsync(message, new User(user.UserOpenId)
        {
            Name = user.Username,
            AvatarUrl = user.AvatarUrl
        }, null, "GET /v2/users/{user_openid}/info",
            $"union_openid: {user.UnionOpenId ?? "未提供"}\n接收主动推送: {user.AllowProactiveMessage}")
            .ConfigureAwait(false);
    }

    private async Task SendGroupInfoAsync(MessageEvent message)
    {
        if (message.Channel.Type != ChannelType.Group)
        {
            await ReplyAsync(message, "请在群聊中执行 #groupinfo。").ConfigureAwait(false);
            return;
        }

        string? eventUnionId = null;
        if (message.Raw is JsonElement { ValueKind: JsonValueKind.Object } raw
            && raw.TryGetProperty("union_group_openid", out var union)
            && union.ValueKind == JsonValueKind.String)
            eventUnionId = union.GetString();

        await ReplyAsync(message, await GroupInfoTextAsync(message.Channel, eventUnionId).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private async Task<string> GroupInfoTextAsync(Channel channel, string? eventUnionId = null)
    {
        var lines = new List<string>
        {
            $"事件 group_openid: {channel.Id}",
            $"事件 union_group_openid: {OrMissing(eventUnionId)}"
        };

        var groups = Context.GetAdapterExtension<IQGroupApi>();
        if (groups is null)
        {
            lines.Add("群接口: 当前适配器未提供 IQGroupApi");
            return string.Join("\n", lines);
        }

        var group = await groups.GetGroupInfoAsync(channel.Id, cancellationToken: Stopping)
            .ConfigureAwait(false);
        lines.Add($"群名: {OrMissing(group.GroupName)}");
        lines.Add($"简介: {OrMissing(group.Description)}");
        lines.Add($"分类: {OrMissing(group.ClassText)}");
        lines.Add($"标签: {JoinTags(group.Tags)}");
        lines.Add($"成员数: {group.MemberCount?.ToString() ?? "未提供"}");
        lines.Add($"查询 union_group_openid: {OrMissing(group.UnionGroupOpenId)}");
        lines.Add($"群能力: {Describe(groups.Capabilities)}");

        if (groups.Capabilities.HasFlag(QGroupCapabilities.BotState))
        {
            try
            {
                var state = await groups.GetBotStateAsync(channel.Id, Stopping).ConfigureAwait(false);
                lines.Add($"机器人状态: {OrMissing(state.MemberOpenId)} / {state.Role} / " +
                    $"主动推送 {state.AllowProactiveMessage}");
                lines.Add($"接收设置: {OrMissing(state.ReceiveSetting)}");
            }
            catch (QPermissionDeniedException)
            {
                lines.Add("机器人状态: 当前机器人未获接口权限");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                lines.Add($"机器人状态: 接口不可用（{ex.Message}）");
            }
        }

        if (groups.Capabilities.HasFlag(QGroupCapabilities.MemberBlacklist))
        {
            try
            {
                var page = await groups.GetGroupBlacklistAsync(channel.Id, limit: 1, cancellationToken: Stopping)
                    .ConfigureAwait(false);
                lines.Add($"群黑名单: 可查询，本页 {page.Users.Count} 条" +
                    (string.IsNullOrEmpty(page.NextCursor) ? "，已到末页" : "，还有下一页"));
            }
            catch (QPermissionDeniedException)
            {
                lines.Add("群黑名单: 当前机器人未获接口权限");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                lines.Add($"群黑名单: 接口不可用（{ex.Message}）");
            }
        }

        return string.Join("\n", lines);
    }

    private static string OrMissing(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未提供" : value;

    private static string JoinTags(IReadOnlyList<string> tags)
    {
        var valid = tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray();
        return valid.Length == 0 ? "未提供" : string.Join("、", valid);
    }

    private static string Describe(QGroupCapabilities capabilities)
    {
        if (capabilities == QGroupCapabilities.None) return "无";
        return string.Join(", ", Enum.GetValues<QGroupCapabilities>()
            .Where(value => value != QGroupCapabilities.None && capabilities.HasFlag(value)));
    }

    private Task SendCapabilitiesAsync(MessageEvent message) =>
        ReplyAsync(message, CapabilitiesMessage(message.Channel, message.InstanceId));

    private OutgoingMessage CapabilitiesMessage(Channel channel, string? instanceId) => new()
    {
        Segments = [new MarkdownSegment(CapabilitiesText(channel, instanceId))],
        Buttons = TestMenuButtons()
    };

    private static MessageButtonLayout TestMenuButtons() => new(
    [
        new MessageButtonRow(
        [
            new MessageButton("official", "官方消息", new CallbackAction("rich:test:official")),
            new MessageButton("media", "官方媒体", new CallbackAction("rich:test:media")),
            new MessageButton("material", "素材", new CallbackAction("rich:test:material"))
        ]),
        new MessageButtonRow(
        [
            new MessageButton("user", "用户资料", new CallbackAction("rich:test:user")),
            new MessageButton("file", "文件上传", new CallbackAction("rich:test:file")),
            new MessageButton("stream", "私聊流式", new CallbackAction("rich:test:stream"))
        ]),
        new MessageButtonRow(
        [
            new MessageButton("group", "群管理", new CallbackAction("rich:test:group")),
            new MessageButton("caps", "能力", new CallbackAction("rich:test:caps")),
            new MessageButton("help", "命令", new CallbackAction("rich:help"))
        ]),
        new MessageButtonRow(
        [
            new MessageButton("img", "图片", new CallbackAction("rich:test:image")),
            new MessageButton("audio", "语音", new CallbackAction("rich:test:audio")),
            new MessageButton("video", "视频", new CallbackAction("rich:test:video")),
            new MessageButton("pdf", "文件", new CallbackAction("rich:test:filemedia"))
        ])
    ]);

    private static OutgoingMessage Text(string text) =>
        new() { Segments = [new TextSegment(text)] };

    private static Stream OpenAsset(string fileName) =>
        typeof(RichDemoPlugin).Assembly.GetManifestResourceStream(
            $"ShiroBot.Plugin.QQPlatformRichDemo.Assets.{fileName}")
        ?? throw new InvalidOperationException($"Embedded asset is missing: {fileName}");

    private static string AssetPath(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "shirobot-richdemo");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        using var source = OpenAsset(fileName);
        using var target = File.Create(path);
        source.CopyTo(target);
        return path;
    }

    private static string SamplePngPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "shirobot-richdemo", "demo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllBytes(path, Convert.FromBase64String(SamplePng));
        return path;
    }

    private async Task SendMediaCommandAsync(MessageEvent message, string fileName, bool quote = false)
    {
        var path = AssetPath(fileName);
        MessageSegment segment = Path.GetExtension(fileName) switch
        {
            ".mp4" => new VideoSegment(path),
            ".mp3" => new AudioSegment(path),
            ".pdf" => new FileSegment(path) { FileName = fileName },
            _ => throw new NotSupportedException(fileName)
        };
        var caption = Path.GetExtension(fileName) switch
        {
            ".mp3" => "语音回复",
            ".mp4" => "视频回复",
            ".pdf" => "文件回复",
            _ => null
        };
        if (quote && caption is not null)
        {
            // QQ 客户端对语音/视频/文件消息通常不画引用气泡，先发一条可见的引用文本。
            await ReplyAsync(message, $"{caption}：").ConfigureAwait(false);
        }
        IReadOnlyList<MessageSegment> segments = caption is null
            ? [segment]
            : [new TextSegment($"{caption}\n"), segment];
        await ReplyAsync(message, new OutgoingMessage { Segments = segments }).ConfigureAwait(false);
    }

    private Task SendImageAssetAsync(MessageEvent message) =>
        ReplyAsync(message, new OutgoingMessage { Segments = [new ImageSegment(SamplePngPath())] });

    private async Task SendAllMediaAsync(MessageEvent message)
    {
        await SendImageAssetAsync(message).ConfigureAwait(false);
        await Task.Delay(300, Stopping).ConfigureAwait(false);
        await SendMediaCommandAsync(message, "demo.mp3").ConfigureAwait(false);
        await Task.Delay(300, Stopping).ConfigureAwait(false);
        await SendMediaCommandAsync(message, "demo.mp4").ConfigureAwait(false);
        await Task.Delay(300, Stopping).ConfigureAwait(false);
        await SendMediaCommandAsync(message, "demo.pdf").ConfigureAwait(false);
    }

    private async Task<OutgoingMessage> TestOfficialMessageAsync(Channel channel, QOfficialMessageReply reply)
    {
        var official = Context.GetAdapterExtension<IQOfficialMessageApi>();
        if (official is null) return Text("当前适配器未提供 IQOfficialMessageApi。");
        await official.SendTextAsync(Target(channel),
            "官方消息测试：通过 IQOfficialMessageApi.SendTextAsync 发送。", reply, Stopping)
            .ConfigureAwait(false);
        return Text("官方消息测试已发送。");
    }

    private async Task<OutgoingMessage> TestMediaAsync(Channel channel, QOfficialMessageReply reply)
    {
        var media = Context.GetAdapterExtension<IQOfficialMediaApi>();
        if (media is null) return Text("当前适配器未提供 IQOfficialMediaApi。");
        using var image = new MemoryStream(Convert.FromBase64String(SamplePng));
        await media.UploadAndSendAsync(Target(channel), QOfficialMediaType.Image, image, "richdemo.png",
            reply, Stopping).ConfigureAwait(false);
        return Text("官方媒体测试已发送。");
    }

    private async Task<OutgoingMessage> TestMediaAssetAsync(Channel channel, QOfficialMessageReply reply,
        QOfficialMediaType type, string fileName)
    {
        var media = Context.GetAdapterExtension<IQOfficialMediaApi>();
        if (media is null) return Text("当前适配器未提供 IQOfficialMediaApi。");
        using var source = OpenAsset(fileName);
        await media.UploadAndSendAsync(Target(channel), type, source, fileName, reply, Stopping)
            .ConfigureAwait(false);
        return Text($"已发送 {type} 富媒体：{fileName}");
    }

    private async Task<OutgoingMessage> TestMaterialAsync(Channel channel, string? instanceId,
        User user, QOfficialMessageReply reply)
    {
        if (instanceId is not { Length: > 0 } id || !Context.IsAdmin(new UserReference(id, user.Id)))
            return Text("素材测试需要宿主管理员权限。");
        var materials = Context.GetAdapterExtension<IQOfficialMaterialApi>();
        var official = Context.GetAdapterExtension<IQOfficialMessageApi>();
        if (materials is null || official is null) return Text("当前适配器未提供素材或官方 Markdown 能力。");
        using var image = new MemoryStream(Convert.FromBase64String(SamplePng));
        var material = await materials.UploadImageAsync(image, "richdemo.png", Stopping).ConfigureAwait(false);
        var markdown = new QCustomMarkdown($"## 平台图片素材\n\n![1px 测试图 #64px #64px]({material.Url})")
        {
            ForceVerifyImageResource = true
        };
        await official.SendAsync(Target(channel), QOfficialMessage.Markdown(markdown), reply, Stopping)
            .ConfigureAwait(false);
        return Text($"素材测试完成: {material.ImageId}\n引用: {material.Url}");
    }

    private async Task<OutgoingMessage> TestUserAsync(Channel channel, User user)
    {
        if (channel.Type != ChannelType.Direct)
            return Text("用户资料测试仅在私聊可用；群内请用 #about 查看群成员资料。");
        var users = Context.GetAdapterExtension<IQOfficialUserApi>();
        if (users is null) return Text("当前适配器未提供 IQOfficialUserApi。");
        var info = await users.GetUserInfoAsync(user.Id, Stopping).ConfigureAwait(false);
        return ProfileMessage(new User(info.UserOpenId) { Name = info.Username, AvatarUrl = info.AvatarUrl },
            channel, null, "IQOfficialUserApi", $"union_openid: {info.UnionOpenId ?? "未提供"}\n" +
            $"接收主动推送: {info.AllowProactiveMessage}");
    }

    private async Task<OutgoingMessage> TestFileUploadAsync(Channel channel)
    {
        var files = Context.GetAdapterExtension<IFileService>();
        if (files is null) return Text("当前适配器未提供 IFileService。");
        var result = await files.UploadAsync(channel, new FileUploadRequest
        {
            Uri = $"base64:{SamplePng}",
            FileName = "richdemo.png"
        }, Stopping).ConfigureAwait(false);
        return Text($"文件上传测试完成: {result.FileId}\n已发布: {result.IsPublished}");
    }

    private async Task<OutgoingMessage> TestStreamAsync(Channel channel, string? messageId)
    {
        if (channel.Type != ChannelType.Direct)
            return Text("流式消息仅支持私聊。");
        if (string.IsNullOrWhiteSpace(messageId))
            return Text("流式消息需要按钮所在的消息 ID；请在私聊发送 #streamtest。");
        var direct = Context.GetAdapterExtension<IQOfficialDirectMessageApi>();
        if (direct is null) return Text("当前适配器未提供 IQOfficialDirectMessageApi。");
        var stream = direct.BeginStream(Target(channel), new QOfficialMessageReply { MessageId = messageId });
        try
        {
            await stream.AppendAsync("ShiroBot", Stopping).ConfigureAwait(false);
            await stream.AppendAsync("ShiroBot 流式", Stopping).ConfigureAwait(false);
            await stream.AppendAsync("ShiroBot 流式消息按钮测试完成。", Stopping).ConfigureAwait(false);
            await stream.CompleteAsync(Stopping).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        return Text("流式消息测试已发送。");
    }

    private async Task<OutgoingMessage> TestGroupAsync(Channel channel) =>
        channel.Type == ChannelType.Group
            ? Text(await GroupInfoTextAsync(channel).ConfigureAwait(false))
            : Text("群管理测试仅支持群聊。");

    private string CapabilitiesText(Channel channel, string? instanceId)
    {
        var messageCaps = Context.Message.GetMessageCapabilities(
            new ChannelReference(instanceId ?? throw new InvalidOperationException("Instance required."), channel));
        var groups = Context.GetAdapterExtension<IQGroupApi>();
        var lines = new List<string>
        {
            $"ShiroBot: {Context.GetHostStatus()?.Version ?? "未知"}",
            $"SDK ABI: {typeof(IBotContext).Assembly.GetName().Version}",
            $"实例: {instanceId}",
            $"原生通用能力: {messageCaps.NativeFeatures}",
            $"按钮须 Markdown: {messageCaps.ButtonsRequireMarkdown}",
            $"最大文本长度: {messageCaps.MaxTextLength}",
            $"官方消息: {Context.GetAdapterExtension<IQOfficialMessageApi>() is not null}",
            $"官方媒体: {Context.GetAdapterExtension<IQOfficialMediaApi>() is not null}",
            $"素材管理: {Context.GetAdapterExtension<IQOfficialMaterialApi>() is not null}",
            $"用户资料: {Context.GetAdapterExtension<IQOfficialUserApi>() is not null}",
            $"文件上传: {Context.GetAdapterExtension<IFileService>() is not null}",
            $"私聊流式: {Context.GetAdapterExtension<IQOfficialDirectMessageApi>() is not null}",
            $"官方群管理: {groups is not null}",
            $"群能力: {Describe(groups?.Capabilities ?? QGroupCapabilities.None)}"
        };
        return string.Join("\n", lines);
    }

    private async Task SendCardDemoAsync(MessageEvent message, bool generic)
    {
        var config = Settings;
        if (!IsHttpsUrl(config.CardUrl) || !IsHttpsUrl(config.CardImageUrl))
        {
            await ReplyAsync(message, "请设置 card_url 和 card_image_url：必须为 HTTPS，且长度不超过 1024 字符。")
                .ConfigureAwait(false);
            return;
        }

        if (generic)
        {
            var outgoing = new OutgoingMessage
            {
                Segments =
                [
                    new CardSegment
                    {
                        Title = config.CardTitle,
                        Description = config.CardDescription,
                        Url = config.CardUrl,
                        ImageUrl = config.CardImageUrl
                    }
                ]
            };
            var assessment = Context.Message.AssessMessage(
                new ChannelReference(message.InstanceId ?? throw new InvalidOperationException("Instance required."),
                    message.Channel), outgoing);
            if (!assessment.IsSupported)
            {
                await ReplyAsync(message, "当前适配器或卡片参数不支持原生图文卡片，未静默降级。")
                    .ConfigureAwait(false);
                return;
            }
            await ReplyAsync(message, outgoing).ConfigureAwait(false);
            return;        }

        var official = Context.GetAdapterExtension<IQOfficialMessageApi>();
        if (official is null)
        {
            await ReplyAsync(message, "当前适配器未提供 IQOfficialMessageApi。").ConfigureAwait(false);
            return;
        }
        await official.SendAsync(Target(message),
            QOfficialMessage.Card(new QOfficialCard(config.CardTitle, config.CardDescription, config.CardUrl,
                config.CardImageUrl)),
            new QOfficialMessageReply { MessageId = message.MessageId }, Stopping).ConfigureAwait(false);
    }

    private static bool IsHttpsUrl(string? value) =>
        value is { Length: > 0 and <= 1024 }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo);

    private Task SendGenericMarkdownAsync(MessageEvent message) =>
        ReplyAsync(message, CreateMarkdownDemo());

    private static OutgoingMessage CreateMarkdownDemo() => new()
    {
        Segments =
        [
            new MarkdownSegment("# SDK 通用富消息\n\n一个命令演示一项能力。" +
                "按钮通过 InteractionEvent 回复；发送 #about 展示头像与名称。")
        ],
        Buttons = new MessageButtonLayout(
        [
            new MessageButtonRow(
            [
                new MessageButton("help", "命令帮助", new CallbackAction("rich:help")),
                new MessageButton("docs", "QQ 文档", new OpenUrlAction(DocsUrl))
            ])
        ])
    };

    private Task SendImageAsync(MessageEvent message) =>
        ReplyAsync(message, ImageMessage(Settings.ImageUrl));

    private static OutgoingMessage ImageMessage(string imageUrl) => new()
    {
        Segments = string.IsNullOrWhiteSpace(imageUrl)
            ? [new TextSegment("请设置 image_url：HTTP(S)、宿主本地路径、file:// 或 base64: 图片。")]
            : [new ImageSegment(imageUrl.Trim())]
    };

    private async Task SendEveryoneAsync(MessageEvent message, bool markdown)
    {
        if (message.Channel.Type != ChannelType.Group)
        {
            await ReplyAsync(message, "@全体仅支持群聊。").ConfigureAwait(false);
            return;
        }
        if (!await RequireAdminAsync(message).ConfigureAwait(false)) return;
        if (!Settings.EnableEveryoneDemo)
        {
            await ReplyAsync(message, "为避免误通知，默认关闭。确认后将 enable_everyone_demo 设为 true，再执行。")
                .ConfigureAwait(false);
            return;
        }
        var outgoing = new OutgoingMessage
        {
            AllowedFallbacks = MessageFallbackOptions.None,
            Segments = markdown
                ? [new MarkdownSegment("<qqbot-at-everyone /> **ShiroBot 全体通知演示**")]
                : [new MentionAllSegment(), new TextSegment("ShiroBot 全体通知演示")]
        };
        await ReplyAsync(message, outgoing).ConfigureAwait(false);
    }

    private async Task SendMaterialDemoAsync(MessageEvent message)
    {
        if (!await RequireAdminAsync(message).ConfigureAwait(false)) return;
        var materials = Context.GetAdapterExtension<IQOfficialMaterialApi>();
        var official = Context.GetAdapterExtension<IQOfficialMessageApi>();
        if (materials is null || official is null)
        {
            await ReplyAsync(message, "当前适配器未提供素材管理或官方 Markdown 能力。").ConfigureAwait(false);
            return;
        }

        using var image = new MemoryStream(Convert.FromBase64String(SamplePng));
        var material = await materials.UploadImageAsync(image, "richdemo.png", Stopping).ConfigureAwait(false);
        await ReplyAsync(message, $"素材已上传: {material.ImageId}\n引用: {material.Url}\n" +
            $"演示后可用 #materialdelete {material.ImageId} 释放配额。")
            .ConfigureAwait(false);
        var markdown = new QCustomMarkdown($"## 平台图片素材\n\n![1px 测试图 #64px #64px]({material.Url})")
        {
            ForceVerifyImageResource = true
        };
        await official.SendAsync(Target(message), QOfficialMessage.Markdown(markdown),
            new QOfficialMessageReply { MessageId = message.MessageId }, Stopping).ConfigureAwait(false);
    }

    private async Task ListMaterialsAsync(MessageEvent message, string? cursor)
    {
        if (!await RequireAdminAsync(message).ConfigureAwait(false)) return;
        var materials = Context.GetAdapterExtension<IQOfficialMaterialApi>();
        if (materials is null)
        {
            await ReplyAsync(message, "当前适配器不支持素材管理。").ConfigureAwait(false);
            return;
        }
        var page = await materials.GetImagesAsync(cursor, Stopping).ConfigureAwait(false);
        var lines = page.Images.Take(10).Select(image =>
            $"{image.ImageId} | {image.FileName ?? "未提供文件名"} | 到期 {image.ExpiresAt:O}");
        await ReplyAsync(message, $"素材总数: {page.Total}（本页显示前 10 条）\n{string.Join("\n", lines)}\n" +
            (string.IsNullOrEmpty(page.NextCursor) ? "已到末页。" : $"下一页: #materiallist {page.NextCursor}"))
            .ConfigureAwait(false);
    }

    private async Task DeleteMaterialAsync(MessageEvent message, string imageId)
    {
        if (!await RequireAdminAsync(message).ConfigureAwait(false)) return;
        if (string.IsNullOrWhiteSpace(imageId))
        {
            await ReplyAsync(message, "请提供 image_id。").ConfigureAwait(false);
            return;
        }
        var materials = Context.GetAdapterExtension<IQOfficialMaterialApi>();
        if (materials is null)
        {
            await ReplyAsync(message, "当前适配器不支持素材管理。").ConfigureAwait(false);
            return;
        }
        await materials.DeleteImageAsync(imageId, Stopping).ConfigureAwait(false);
        await ReplyAsync(message, $"已删除素材 {imageId}，引用立即失效，已发送消息不受影响。")
            .ConfigureAwait(false);
    }

    private async Task SendAndRecallAsync(MessageEvent message)
    {
        var sent = await Context.Message.ReplyAsync(message,
            WithReply(message, new OutgoingMessage { Segments = [new TextSegment("这条消息将在 3 秒后撤回。")] }),
            Stopping).ConfigureAwait(false);
        if (!sent.IsSuccess || sent.Reference is null) return;
        await Task.Delay(TimeSpan.FromSeconds(3), Stopping).ConfigureAwait(false);
        await Context.Message.DeleteMessageAsync(sent.Reference, Stopping).ConfigureAwait(false);
    }

    private async Task SendTypingDemoAsync(MessageEvent message)
    {
        var direct = await GetDirectApiAsync(message).ConfigureAwait(false);
        if (direct is null) return;
        await direct.SendTypingAsync(Target(message),
            new QOfficialMessageReply { MessageId = message.MessageId }, TimeSpan.FromSeconds(3), Stopping)
            .ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2), Stopping).ConfigureAwait(false);
        await ReplyAsync(message, "输入状态演示结束。").ConfigureAwait(false);
    }

    private async Task SendStreamDemoAsync(MessageEvent message)
    {
        var direct = await GetDirectApiAsync(message).ConfigureAwait(false);
        if (direct is null) return;
        var stream = direct.BeginStream(Target(message),
            new QOfficialMessageReply { MessageId = message.MessageId });
        try
        {
            await stream.AppendAsync("ShiroBot", Stopping).ConfigureAwait(false);
            await stream.AppendAsync("ShiroBot 流式", Stopping).ConfigureAwait(false);
            await stream.AppendAsync("ShiroBot 流式消息演示完成。", Stopping).ConfigureAwait(false);
            await stream.CompleteAsync(Stopping).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IQOfficialDirectMessageApi?> GetDirectApiAsync(MessageEvent message)
    {
        var direct = message.IsDirect ? Context.GetAdapterExtension<IQOfficialDirectMessageApi>() : null;
        if (direct is null)
            await ReplyAsync(message, "输入状态和流式演示仅支持提供 IQOfficialDirectMessageApi 的 QQ 私聊。")
                .ConfigureAwait(false);
        return direct;
    }
}
