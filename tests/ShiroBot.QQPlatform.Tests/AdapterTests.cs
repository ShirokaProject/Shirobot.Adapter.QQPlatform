using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform;
using ShiroBot.Adapter.QQPlatform.AdapterImpl;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Xunit;

namespace ShiroBot.QQPlatform.Tests;

public sealed class AdapterTests
{
    [Theory]
    [InlineData("GROUP_AT_MESSAGE_CREATE", "group-1", "group-member", ChannelType.Group)]
    [InlineData("C2C_MESSAGE_CREATE", "c2c-user", "c2c-user", ChannelType.Direct)]
    public void TranslatesIncomingMessages(string type, string expectedChannel, string expectedSender, ChannelType expectedType)
    {
        var data = JsonDocument.Parse("""{"id":"message-1","group_openid":"group-1","author":{"member_openid":"group-member","user_openid":"c2c-user","username":"Alice"},"content":"hello","attachments":[{"url":"https://example.org/a.png","content_type":"image/png"}]}""").RootElement;
        var result = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(new GatewayPayload(0, data, 1, type, "evt-1"), "bot-1"));
        Assert.Equal(expectedChannel, result.Channel.Id);
        Assert.Equal(expectedType, result.Channel.Type);
        Assert.Equal(expectedSender, result.Sender.Id);
        Assert.Equal(expectedType == ChannelType.Group, result.Member is not null);
        Assert.Equal("hello", result.GetPlainText());
        Assert.Contains(result.Segments, x => x is ImageSegment);
        Assert.Equal("qq-official", result.Platform);
    }

    [Fact]
    public async Task GroupAtContentProducesMentionEmojiAndRoutableCommandText()
    {
        var data = JsonDocument.Parse("""{"id":"message-2","group_openid":"group-1","author":{"member_openid":"member-1"},"content":"<@!12345678901234567890> #ping<faceType=1,faceId=\"264\",ext=\"eyJ0ZXh0Ijoi5o2C6IS4In0=\">"}""").RootElement;
        var message = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(new GatewayPayload(0, data, 2, "GROUP_AT_MESSAGE_CREATE", "evt-2"), "12345678901234567890", "测试机器人"));
        Assert.True(message.HasMention("12345678901234567890"));
        Assert.Equal("测试机器人", Assert.Single(message.Segments.OfType<MentionSegment>()).DisplayName);
        Assert.Equal("#ping", message.GetPlainText().Trim());
        Assert.Equal("捂脸", Assert.Single(message.Segments.OfType<EmojiSegment>()).Name);
        var called = false;
        var router = new CommandRouter<MessageEvent>();
        router.MapPrefix("#ping", _ => { called = true; return Task.CompletedTask; });
        Assert.True(await router.DispatchAsync(message.GetPlainText().Trim(), message));
        Assert.True(called);
    }

    [Fact]
    public void GroupMessageMapsRoleAndMentionDisplayNameWithoutChangingOpenIds()
    {
        var data = JsonDocument.Parse("""{"id":"message-3","group_openid":"group-openid","author":{"member_openid":"sender-openid","username":"小明","member_role":"admin","union_openid":"union-openid"},"mentions":[{"member_openid":"target-openid","username":"小红"}],"content":"<@target-openid> 你好"}""").RootElement;
        var message = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, data, 3, "GROUP_MESSAGE_CREATE", "evt-3"), "bot-openid"));
        Assert.Equal("group-openid", message.Channel.Id);
        Assert.Equal("sender-openid", message.Sender.Id);
        Assert.Equal("小明", message.Sender.Name);
        Assert.Equal(MemberRole.Admin, message.Member!.Role);
        var mention = Assert.Single(message.Segments.OfType<MentionSegment>());
        Assert.Equal("target-openid", mention.UserId);
        Assert.Equal("小红", mention.DisplayName);
        Assert.Equal("你好", message.GetPlainText().Trim());
        Assert.Equal("union-openid", ((JsonElement)message.Raw!).GetProperty("author").GetProperty("union_openid").GetString());
    }

    [Fact]
    public async Task RepliesUseCorrectRouteTokenAndIncreasingSequence()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        var logger = new CapturingLogger();
        var messages = new QQMessageService(api, logger);
        var segments = new MessageSegment[] { new QuoteSegment("incoming"), new TextSegment("hello") };
        await messages.SendMessageAsync(Channel.Group("group-1"), segments);
        await messages.SendMessageAsync(Channel.Group("group-1"), segments);
        Assert.Equal(3, handler.Requests.Count);
        Assert.EndsWith("/v2/groups/group-1/messages", handler.Requests[1].Uri);
        Assert.Equal("QQBot token-1", handler.Requests[1].Authorization);
        Assert.Equal("incoming", JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("msg_id").GetString());
        Assert.Equal(0, JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("msg_type").GetInt32());
        Assert.Equal(1, JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("msg_seq").GetInt32());
        Assert.Equal(2, JsonDocument.Parse(handler.Requests[2].Body).RootElement.GetProperty("msg_seq").GetInt32());
        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, message => Assert.Contains("已发送群消息到 group-1: hello", message));
    }

    [Fact]
    public async Task QuoteReplyUsesOfficialReferenceIndexAndNativeGroupMention()
    {
        var data = JsonDocument.Parse("""{"id":"incoming-1","group_openid":"group-1","author":{"member_openid":"member-1","username":"小明"},"content":"<@bot-1> 你好","message_scene":{"ext":["msg_idx=REFIDX_123==","auth_token=not-used"]}}""").RootElement;
        var incoming = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, data, 1, "GROUP_AT_MESSAGE_CREATE", "event-1"), "bot-1"));
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var logger = new CapturingLogger();
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)), logger);
        messages.RegisterIncoming(incoming);
        await messages.SendMessageAsync(incoming.Channel with { Name = "测试群" },
            [new QuoteSegment(incoming.MessageId), new MentionSegment(incoming.Sender.Id) { DisplayName = incoming.Sender.Name },
                new TextSegment("收到你的 @ 了")]);
        var sent = JsonDocument.Parse(handler.Requests[^1].Body).RootElement;
        Assert.Equal("incoming-1", sent.GetProperty("msg_id").GetString());
        Assert.Equal("REFIDX_123==", sent.GetProperty("message_reference").GetProperty("message_id").GetString());
        Assert.Equal(2, sent.GetProperty("msg_type").GetInt32());
        Assert.False(sent.TryGetProperty("content", out _));
        Assert.Equal("<qqbot-at-user id=\"member-1\" /> 收到你的 @ 了",
            sent.GetProperty("markdown").GetProperty("content").GetString());
        Assert.Contains("已发送群消息到 测试群: @小明 收到你的 @ 了", logger.Messages);
    }

    [Fact]
    public async Task GroupInfoUsesOfficialEndpointAndReturnsGroupName()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        Assert.Equal("测试群", await api.GetGroupNameAsync("group-1"));
        Assert.EndsWith("/v2/groups/group-1/info", handler.Requests[1].Uri);
        Assert.Equal("QQBot token-1", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task OfficialMarkdownSerializesKeyboardAndEventReply()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        var official = new QQOfficialMessageService(api, new QQMessageService(api));
        var target = new QOfficialMessageTarget(QOfficialMessageScene.Group, "group-1");
        var keyboard = new QInlineKeyboard([new QKeyboardRow([
            OfficialButton("next", "下一页", QKeyboardActionType.Callback, "rich:next"),
            OfficialButton("docs", "文档", QKeyboardActionType.Jump, "https://example.org")
        ])]);
        Assert.True(official.CanSendMarkdown(target, new QCustomMarkdown("# 标题"), keyboard));
        Assert.Equal("outgoing-1", await official.SendMarkdownAsync(target, new QCustomMarkdown("# 标题"),
            keyboard, new QOfficialMessageReply { EventId = "event-1" }));
        using var json = JsonDocument.Parse(handler.Requests[^1].Body);
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("msg_type").GetInt32());
        Assert.Equal("event-1", root.GetProperty("event_id").GetString());
        Assert.False(root.TryGetProperty("msg_id", out _));
        Assert.Equal("# 标题", root.GetProperty("markdown").GetProperty("content").GetString());
        var buttons = root.GetProperty("keyboard").GetProperty("content").GetProperty("rows")[0].GetProperty("buttons");
        Assert.Equal(1, buttons[0].GetProperty("action").GetProperty("type").GetInt32());
        Assert.Equal(0, buttons[1].GetProperty("action").GetProperty("type").GetInt32());
    }

    [Fact]
    public async Task OfficialTemplateMarkdownAndKeyboardCanSendWithoutReply()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        var official = new QQOfficialMessageService(api, new QQMessageService(api));
        var target = new QOfficialMessageTarget(QOfficialMessageScene.Direct, "user-1");
        var markdown = new QTemplateMarkdown("template-1", [new QMarkdownParameter("name", ["Alice"])]);
        Assert.True(official.CanSendMarkdown(target, markdown, new QKeyboardTemplate("keyboard-1")));
        Assert.True(official.CanSendMarkdown(
            new QOfficialMessageTarget(QOfficialMessageScene.Channel, "channel-1"), markdown));
        await official.SendMarkdownAsync(target, markdown, new QKeyboardTemplate("keyboard-1"),
            new QOfficialMessageReply());
        var root = JsonDocument.Parse(handler.Requests[^1].Body).RootElement;
        Assert.Equal("template-1", root.GetProperty("markdown").GetProperty("custom_template_id").GetString());
        Assert.Equal("Alice", root.GetProperty("markdown").GetProperty("params")[0]
            .GetProperty("values")[0].GetString());
        Assert.Equal("keyboard-1", root.GetProperty("keyboard").GetProperty("id").GetString());
        Assert.False(root.TryGetProperty("msg_id", out _));
        Assert.False(root.TryGetProperty("event_id", out _));
    }

    [Fact]
    public async Task OfficialTextCanReplyToInteractionEvent()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        IQOfficialMessageApi official = new QQOfficialMessageService(api, new QQMessageService(api));

        var sent = await official.SendTextAsync(
            new QOfficialMessageTarget(QOfficialMessageScene.Group, "group-1"), "按钮回复",
            new QOfficialMessageReply { EventId = "gateway-event-1" });

        Assert.Equal("outgoing-1", sent);
        var root = JsonDocument.Parse(handler.Requests[^1].Body).RootElement;
        Assert.Equal(0, root.GetProperty("msg_type").GetInt32());
        Assert.Equal("gateway-event-1", root.GetProperty("event_id").GetString());
        Assert.False(root.TryGetProperty("msg_id", out _));
    }

    [Fact]
    public async Task OfficialReplyLogsCachedGroupName()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        var logger = new CapturingLogger();
        var official = new QQOfficialMessageService(api, new QQMessageService(api, logger), logger,
            groupId => groupId == "group-1" ? "测试群" : null);

        await official.SendMarkdownAsync(
            new QOfficialMessageTarget(QOfficialMessageScene.Group, "group-1"),
            new QCustomMarkdown("# 回复"),
            reply: new QOfficialMessageReply { EventId = "event-1" });

        Assert.Contains("已发送群消息到 测试群: [Markdown]", logger.Messages);
    }

    [Fact]
    public async Task OfficialArkCapabilityUsesSharedContract()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        IQOfficialMessageApi official = new QQOfficialMessageService(api, new QQMessageService(api));

        var sent = await official.SendArkAsync(
            new QOfficialMessageTarget(QOfficialMessageScene.Group, "group-1"), 23,
            new Dictionary<string, string> { ["name"] = "Alice" },
            new QOfficialMessageReply { MessageId = "incoming" });

        Assert.Equal("outgoing-1", sent);
        var root = JsonDocument.Parse(handler.Requests[^1].Body).RootElement;
        Assert.Equal(3, root.GetProperty("msg_type").GetInt32());
        Assert.Equal("incoming", root.GetProperty("msg_id").GetString());
        Assert.Equal(23, root.GetProperty("ark").GetProperty("template_id").GetInt32());
    }

    [Fact]
    public async Task TypingUsesC2CMessageEndpointAndNotificationPayload()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        IQOfficialDirectMessageApi official = new QQOfficialMessageService(api, new QQMessageService(api));
        var direct = new QOfficialMessageTarget(QOfficialMessageScene.Direct, "user-1");
        var reply = new QOfficialMessageReply { MessageId = "incoming" };
        await official.SendTypingAsync(direct, reply, TimeSpan.FromSeconds(3));
        Assert.EndsWith("/v2/users/user-1/messages", handler.Requests[^1].Uri);
        var root = JsonDocument.Parse(handler.Requests[^1].Body).RootElement;
        Assert.Equal(6, root.GetProperty("msg_type").GetInt32());
        Assert.Equal("incoming", root.GetProperty("msg_id").GetString());
        Assert.Equal(1, root.GetProperty("msg_seq").GetInt32());
        Assert.Equal(1, root.GetProperty("input_notify").GetProperty("input_type").GetInt32());
        Assert.Equal(3, root.GetProperty("input_notify").GetProperty("input_second").GetInt32());
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            official.SendTypingAsync(new QOfficialMessageTarget(QOfficialMessageScene.Group, "group-1"),
                reply, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task StreamFramesKeepSequenceAndAdvanceIndex()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        IQOfficialDirectMessageApi official = new QQOfficialMessageService(api, new QQMessageService(api));
        var reply = new QOfficialMessageReply { MessageId = "incoming" };
        await using var stream = official.BeginStream(
            new QOfficialMessageTarget(QOfficialMessageScene.Direct, "user-1"),
            reply, QOfficialStreamContentType.Markdown);
        await stream.AppendAsync("# 标题");
        await stream.AppendAsync("# 标题\n正文");
        Assert.Equal("outgoing-1", await stream.CompleteAsync());
        var frames = handler.Requests.Skip(1).Select(r => JsonDocument.Parse(r.Body).RootElement.Clone()).ToArray();
        Assert.Equal(3, frames.Length);
        Assert.All(handler.Requests.Skip(1), request => Assert.EndsWith("/v2/users/user-1/stream_messages", request.Uri));
        Assert.Equal([0, 1, 2], frames.Select(f => f.GetProperty("index").GetInt32()).ToArray());
        Assert.Equal([1, 1, 10], frames.Select(f => f.GetProperty("input_state").GetInt32()).ToArray());
        Assert.All(frames, frame =>
        {
            Assert.Equal(1, frame.GetProperty("msg_seq").GetInt32());
            Assert.Equal("incoming", frame.GetProperty("msg_id").GetString());
            Assert.Equal("incoming", frame.GetProperty("event_id").GetString());
            Assert.Equal("markdown", frame.GetProperty("content_type").GetString());
            Assert.Equal("replace", frame.GetProperty("input_mode").GetString());
        });
        Assert.False(frames[0].TryGetProperty("stream_msg_id", out _));
        Assert.Equal("outgoing-1", frames[1].GetProperty("stream_msg_id").GetString());
        Assert.Equal("# 标题\n正文", frames[2].GetProperty("content_raw").GetString());
        Assert.Throws<NotSupportedException>(() => official.BeginStream(
            new QOfficialMessageTarget(QOfficialMessageScene.Group, "group-1"), reply));
    }

    [Fact]
    public async Task InteractionIsTranslatedAndAcknowledged()
    {
        var data = JsonDocument.Parse("""{"id":"interaction-1","type":11,"scene":"group","chat_type":1,"group_openid":"group-1","group_member_openid":"member-1","data":{"resolved":{"button_data":"rich:main","button_id":"home"}}}""").RootElement;
        var platformEvent = Assert.IsType<PlatformEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, data, 10, "INTERACTION_CREATE", "event-1"), "bot-1"));
        Assert.Equal(QEventKinds.OfficialButtonInteraction, platformEvent.Kind);
        var interaction = Assert.IsType<QOfficialButtonInteraction>(platformEvent.Raw);
        Assert.Equal("group-1", interaction.Target.Id);
        Assert.Equal(QOfficialMessageScene.Group, interaction.Target.Scene);
        Assert.Equal("member-1", interaction.UserId);
        Assert.Equal("rich:main", interaction.ButtonData);
        Assert.Equal("event-1", interaction.EventId);
        Assert.Equal("group-1", platformEvent.Channel?.Id);

        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        await api.AcknowledgeInteractionAsync(interaction.InteractionId);
        await api.AcknowledgeInteractionAsync(interaction.InteractionId);
        Assert.EndsWith("/interactions/interaction-1", handler.Requests[^1].Uri);
        Assert.Equal(0, JsonDocument.Parse(handler.Requests[^1].Body).RootElement.GetProperty("code").GetInt32());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsMessageWithoutReplyReference()
    {
        using var http = new HttpClient(new FakeHandler());
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => messages.SendMessageAsync(Channel.Direct("user"), [new TextSegment("hello")]));
    }

    [Fact]
    public async Task GenericReplyUsesRecentIncomingMessageInSameChannel()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));
        messages.RegisterIncoming(new MessageEvent
        {
            Platform = "qq-official", MessageId = "incoming", Channel = Channel.Group("group-1"),
            Sender = new User("user"), Segments = [new TextSegment("ping")]
        });
        await messages.SendMessageAsync(Channel.Group("group-1"), [new TextSegment("pong")]);
        Assert.Equal("incoming", JsonDocument.Parse(handler.Requests[^1].Body).RootElement.GetProperty("msg_id").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => messages.SendMessageAsync(Channel.Group("other"), [new TextSegment("pong")]));
    }

    [Fact]
    public async Task RemoteImageUsesFilesThenMessagesEndpoint()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));
        await messages.SendMessageAsync(Channel.Direct("user-1"),
            [new QuoteSegment("incoming"), new ImageSegment("https://example.org/a.png")]);
        Assert.EndsWith("/v2/users/user-1/files", handler.Requests[1].Uri);
        Assert.EndsWith("/v2/users/user-1/messages", handler.Requests[2].Uri);
        var upload = JsonDocument.Parse(handler.Requests[1].Body).RootElement;
        Assert.Equal(1, upload.GetProperty("file_type").GetInt32());
        Assert.False(upload.GetProperty("srv_send_msg").GetBoolean());
        var sent = JsonDocument.Parse(handler.Requests[2].Body).RootElement;
        Assert.Equal(7, sent.GetProperty("msg_type").GetInt32());
        Assert.Equal("file-1", sent.GetProperty("media").GetProperty("file_info").GetString());
    }

    [Theory]
    [InlineData(QOfficialMessageScene.Channel, "channels/channel-1/messages")]
    [InlineData(QOfficialMessageScene.ChannelDirect, "dms/guild-1/messages")]
    public async Task OfficialEmbedSupportsChannelScenes(QOfficialMessageScene scene, string expectedRoute)
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        IQOfficialMessageApi official = new QQOfficialMessageService(api, new QQMessageService(api));

        await official.SendEmbedAsync(new QOfficialMessageTarget(scene,
                scene == QOfficialMessageScene.Channel ? "channel-1" : "guild-1"),
            new QOfficialEmbed
            {
                Title = "天气",
                Prompt = "深圳天气",
                Thumbnail = new QOfficialEmbedThumbnail("https://example.org/weather.png"),
                Fields = [new QOfficialEmbedField("晴，28°C")]
            },
            new QOfficialMessageReply { EventId = "event-1" });

        Assert.EndsWith(expectedRoute, handler.Requests[^1].Uri);
        var root = JsonDocument.Parse(handler.Requests[^1].Body).RootElement;
        Assert.Equal(4, root.GetProperty("msg_type").GetInt32());
        Assert.Equal("event-1", root.GetProperty("event_id").GetString());
        Assert.Equal("天气", root.GetProperty("embed").GetProperty("title").GetString());
        Assert.Equal("https://example.org/weather.png", root.GetProperty("embed").GetProperty("thumbnail").GetProperty("url").GetString());
        Assert.Equal("晴，28°C", root.GetProperty("embed").GetProperty("fields")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ChannelMarkdownSupportsRoleRestrictedKeyboard()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        var official = new QQOfficialMessageService(api, new QQMessageService(api));
        var keyboard = new QInlineKeyboard([new QKeyboardRow([new QKeyboardButton
        {
            Id = "role-only",
            RenderData = new QKeyboardRenderData("管理操作", "管理操作", QKeyboardButtonStyle.Blue),
            Action = new QKeyboardAction
            {
                Type = QKeyboardActionType.Callback,
                Data = "admin",
                Permission = new QKeyboardPermission
                {
                    Type = QKeyboardPermissionType.SpecifiedRoles,
                    SpecifyRoleIds = ["role-1"]
                },
                UnsupportTips = "请更新客户端"
            }
        }])]);

        Assert.True(official.CanSendMarkdown(new QOfficialMessageTarget(QOfficialMessageScene.Channel, "channel-1"),
            new QCustomMarkdown("# 操作"), keyboard));
        Assert.False(official.CanSendMarkdown(new QOfficialMessageTarget(QOfficialMessageScene.Group, "group-1"),
            new QCustomMarkdown("# 操作"), keyboard));
        await official.SendMarkdownAsync(new QOfficialMessageTarget(QOfficialMessageScene.Channel, "channel-1"),
            new QCustomMarkdown("# 操作"), keyboard, new QOfficialMessageReply { EventId = "event-1" });
        var roleIds = JsonDocument.Parse(handler.Requests[^1].Body).RootElement
            .GetProperty("keyboard").GetProperty("content").GetProperty("rows")[0]
            .GetProperty("buttons")[0].GetProperty("action").GetProperty("permission")
            .GetProperty("specify_role_ids");
        Assert.Equal("role-1", roleIds[0].GetString());
    }

    [Fact]
    public async Task GroupFilePostsDirectlyToFilesEndpoint()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));

        var sent = await messages.SendMessageAsync(Channel.Group("group-1"),
            [new FileSegment("https://example.org/report.pdf")]);

        Assert.Equal("direct-media-1", sent.MessageId);
        Assert.Equal(2, handler.Requests.Count); // token + one direct media POST
        Assert.EndsWith("/v2/groups/group-1/files", handler.Requests[^1].Uri);
        var upload = JsonDocument.Parse(handler.Requests[^1].Body).RootElement;
        Assert.Equal(4, upload.GetProperty("file_type").GetInt32());
        Assert.Equal("https://example.org/report.pdf", upload.GetProperty("url").GetString());
        Assert.Equal("report.pdf", upload.GetProperty("file_name").GetString());
        Assert.True(upload.GetProperty("srv_send_msg").GetBoolean());
    }

    [Fact]
    public async Task QuotedGroupMediaKeepsTwoStepReply()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));

        await messages.SendMessageAsync(Channel.Group("group-1"),
            [new QuoteSegment("incoming"), new ImageSegment("https://example.org/a.png")]);

        Assert.EndsWith("/v2/groups/group-1/files", handler.Requests[1].Uri);
        Assert.False(JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("srv_send_msg").GetBoolean());
        Assert.EndsWith("/v2/groups/group-1/messages", handler.Requests[2].Uri);
    }

    [Fact]
    public async Task RecentGroupMediaUsesReplyInsteadOfProactiveSend()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));
        messages.RegisterIncoming(new MessageEvent
        {
            Platform = "qq-official", MessageId = "incoming", Channel = Channel.Group("group-1"),
            Sender = new User("user"), Segments = [new TextSegment("ping")]
        });

        await messages.SendMessageAsync(Channel.Group("group-1"),
            [new FileSegment("https://example.org/report.pdf")]);

        Assert.False(JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("srv_send_msg").GetBoolean());
        var sent = JsonDocument.Parse(handler.Requests[2].Body).RootElement;
        Assert.Equal("incoming", sent.GetProperty("msg_id").GetString());
        Assert.Equal("file-1", sent.GetProperty("media").GetProperty("file_info").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalGroupFileUsesOfficialChunkedUpload(bool recentReply)
    {
        var path = Path.GetTempFileName();
        try
        {
            var bytes = Encoding.ASCII.GetBytes("abcdef");
            await File.WriteAllBytesAsync(path, bytes);
            var handler = new FakeHandler();
            using var http = new HttpClient(handler);
            var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
            var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));
            if (recentReply)
                messages.RegisterIncoming(new MessageEvent
                {
                    Platform = "qq-official", MessageId = "incoming", Channel = Channel.Group("group-1"),
                    Sender = new User("user"), Segments = [new TextSegment("ping")]
                });

            var sent = await messages.SendMessageAsync(Channel.Group("group-1"),
                [new FileSegment(path) { FileName = "small.bin" }]);

            Assert.Equal(recentReply ? "outgoing-1" : "direct-media-1", sent.MessageId);
            Assert.Equal(recentReply ? 8 : 7, handler.Requests.Count);
            Assert.EndsWith("/v2/groups/group-1/upload_prepare", handler.Requests[1].Uri);
            var prepare = JsonDocument.Parse(handler.Requests[1].Body).RootElement;
            Assert.Equal("6", prepare.GetProperty("file_size").GetString());
            Assert.Equal("small.bin", prepare.GetProperty("file_name").GetString());
            Assert.Equal(Convert.ToHexStringLower(MD5.HashData(bytes)), prepare.GetProperty("md5").GetString());
            Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(bytes)), prepare.GetProperty("sha1").GetString());
            Assert.Equal(prepare.GetProperty("md5").GetString(), prepare.GetProperty("md5_10m").GetString());
            Assert.Equal("abcd", handler.Requests[2].Body);
            Assert.Null(handler.Requests[2].Authorization);
            Assert.Equal(1, JsonDocument.Parse(handler.Requests[3].Body).RootElement.GetProperty("part_index").GetInt32());
            Assert.Equal("ef", handler.Requests[4].Body);
            Assert.Equal(2, JsonDocument.Parse(handler.Requests[5].Body).RootElement.GetProperty("part_index").GetInt32());
            var complete = JsonDocument.Parse(handler.Requests[6].Body).RootElement;
            Assert.Equal("upload-1", complete.GetProperty("upload_id").GetString());
            Assert.Equal(!recentReply, complete.GetProperty("srv_send_msg").GetBoolean());
            if (recentReply)
                Assert.EndsWith("/v2/groups/group-1/messages", handler.Requests[7].Uri);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficialMediaCapabilityUploadsStreamAndSends(bool reply)
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        IQOfficialMediaApi media = new QQOfficialMessageService(api, new QQMessageService(api));
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("image"));

        var messageId = await media.UploadAndSendAsync(
            new QOfficialMessageTarget(QOfficialMessageScene.Group, "group-1"),
            QOfficialMediaType.Image,
            stream,
            "cat.jpg",
            reply ? new QOfficialMessageReply { MessageId = "incoming" } : null);

        Assert.Equal(reply ? "outgoing-1" : "direct-media-1", messageId);
        var complete = handler.Requests.Single(request => request.Uri.EndsWith("/files", StringComparison.Ordinal));
        Assert.Equal(!reply, JsonDocument.Parse(complete.Body).RootElement.GetProperty("srv_send_msg").GetBoolean());
        if (reply)
        {
            var send = JsonDocument.Parse(handler.Requests[^1].Body).RootElement;
            Assert.Equal(7, send.GetProperty("msg_type").GetInt32());
            Assert.Equal("incoming", send.GetProperty("msg_id").GetString());
            Assert.Equal("file-1", send.GetProperty("media").GetProperty("file_info").GetString());
        }
    }

    [Fact]
    public async Task OfficialMediaCapabilitySendsC2CImageWithCaptionInOneMessage()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        IQOfficialMessageApi official = new QQOfficialMessageService(api, new QQMessageService(api));
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("image"));

        var messageId = await official.SendAsync(
            new QOfficialMessageTarget(QOfficialMessageScene.Direct, "user-1"),
            QOfficialMessage.Media(QOfficialMediaType.Image, stream, "cat.jpg",
                "图片说明第一行\n图片说明第二行"),
            new QOfficialMessageReply { MessageId = "incoming" });

        Assert.Equal("outgoing-1", messageId);
        Assert.Contains(handler.Requests, request => request.Uri.EndsWith("/v2/users/user-1/upload_prepare"));
        var send = JsonDocument.Parse(handler.Requests[^1].Body).RootElement;
        Assert.EndsWith("/v2/users/user-1/messages", handler.Requests[^1].Uri);
        Assert.Equal(7, send.GetProperty("msg_type").GetInt32());
        Assert.Equal("incoming", send.GetProperty("msg_id").GetString());
        Assert.Equal("图片说明第一行\n图片说明第二行", send.GetProperty("content").GetString());
        Assert.Equal("file-1", send.GetProperty("media").GetProperty("file_info").GetString());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public async Task UnifiedOfficialMessageDispatchesTextAndMarkdown(bool markdown, int expectedType)
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        IQOfficialMessageApi official = new QQOfficialMessageService(api, new QQMessageService(api));
        var target = new QOfficialMessageTarget(QOfficialMessageScene.Direct, "user-1");
        QOfficialMessage message = markdown
            ? QOfficialMessage.Markdown(new QCustomMarkdown("# hello"), new QKeyboardTemplate("keyboard-1"))
            : QOfficialMessage.Text("hello");

        await official.SendAsync(target, message, new QOfficialMessageReply { MessageId = "incoming" });

        var sent = JsonDocument.Parse(handler.Requests[^1].Body).RootElement;
        Assert.Equal(expectedType, sent.GetProperty("msg_type").GetInt32());
        Assert.Equal("incoming", sent.GetProperty("msg_id").GetString());
        Assert.Equal(markdown ? "# hello" : "hello",
            markdown ? sent.GetProperty("markdown").GetProperty("content").GetString()
                : sent.GetProperty("content").GetString());
        if (markdown)
            Assert.Equal("keyboard-1", sent.GetProperty("keyboard").GetProperty("id").GetString());
    }

    [Fact]
    public async Task TokenErrorReportsTencentCodeWithoutLeakingSecret()
    {
        using var http = new HttpClient(new FakeHandler { RejectToken = true });
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "private-secret" };
        var provider = new QQTokenProvider(http, config);
        var error = await Assert.ThrowsAsync<QQAuthenticationException>(() => provider.GetAsync());
        Assert.Contains("100016", error.Message);
        Assert.DoesNotContain("private-secret", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task OpenApiChecksBusinessErrorEvenWithSuccessHttpStatus(HttpStatusCode status)
    {
        using var http = new HttpClient(new FakeHandler { OpenApiErrorStatus = status });
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        var error = await Assert.ThrowsAsync<QQApiException>(() =>
            api.SendMessageAsync(Channel.Group("group-1"), new QQSendRequest { MessageType = 0, Content = "hi", MessageId = "incoming" }));
        Assert.Equal(40034005, error.ErrorCode);
        Assert.Equal("trace-1", error.TraceId);
    }

    [Fact]
    public void MigratesLegacyTokenEndpoint()
    {
        var config = new QQPlatformConfig { TokenEndpoint = "https://bots.qq.com/app/getAppAccessToken" };
        Assert.True(config.NormalizeLegacyTokenEndpoint());
        Assert.Equal("https://api.bot.qq.com/app/getAppAccessToken", config.TokenEndpoint);
        Assert.False(config.NormalizeLegacyTokenEndpoint());
    }

    private static QKeyboardButton OfficialButton(string id, string label, QKeyboardActionType type, string data) => new()
    {
        Id = id,
        RenderData = new QKeyboardRenderData(label, label, QKeyboardButtonStyle.Blue),
        Action = new QKeyboardAction
        {
            Type = type,
            Permission = new QKeyboardPermission { Type = QKeyboardPermissionType.Everyone },
            Data = data,
            UnsupportTips = "请更新 QQ 客户端"
        }
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = [];
        public bool RejectToken { get; init; }
        public HttpStatusCode? OpenApiErrorStatus { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
            var response = request.RequestUri!.AbsolutePath == "/app/getAppAccessToken"
                ? RejectToken ? """{"code":100016,"message":"invalid appid or secret"}""" : """{"access_token":"token-1","expires_in":7200}"""
                : OpenApiErrorStatus is not null
                    ? """{"err_code":40034005,"message":"msg_id expired","trace_id":"trace-1"}"""
                : request.RequestUri.AbsolutePath.EndsWith("/info")
                    ? """{"group_openid":"group-1","group_name":"测试群"}"""
                : request.RequestUri.AbsolutePath.EndsWith("/upload_prepare")
                    ? """{"upload_id":"upload-1","block_size":"4","parts":[{"index":1,"presigned_url":"https://upload.example/part/1"},{"index":2,"presigned_url":"https://upload.example/part/2"}]}"""
                : request.RequestUri.AbsolutePath.EndsWith("/upload_part_finish")
                    ? "{}"
                : request.RequestUri.AbsolutePath.EndsWith("/files")
                    ? body.Contains("\"srv_send_msg\":true", StringComparison.Ordinal)
                        ? """{"file_info":"file-1","id":"direct-media-1"}"""
                        : """{"file_info":"file-1"}"""
                    : """{"id":"outgoing-1","timestamp":1767225600}""";
            return new HttpResponseMessage(request.RequestUri.AbsolutePath != "/app/getAppAccessToken" && OpenApiErrorStatus is { } errorStatus
                ? errorStatus : HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

    private sealed class CapturingLogger : IConsoleLogger
    {
        public bool IsEnabled { get; set; } = true;
        public List<string> Messages { get; } = [];
        public void Log(string message) => Messages.Add(message);
        public void Info(string message) => Messages.Add(message);
        public void Success(string message) => Messages.Add(message);
        public void Warning(string message) => Messages.Add(message);
        public void Error(string message) => Messages.Add(message);
    }
}
