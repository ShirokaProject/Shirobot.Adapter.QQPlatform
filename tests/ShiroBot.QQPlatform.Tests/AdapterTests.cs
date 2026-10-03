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
    public async Task GroupMessageWithSelfMentionMetadataTriggersMentionRoute()
    {
        var data = JsonDocument.Parse("""{"id":"message-mention-metadata","group_openid":"group-1","author":{"member_openid":"member-1"},"content":"<@bot-member-openid> 你好","mentions":[{"member_openid":"bot-member-openid","username":"测试机器人","bot":true}]}""").RootElement;
        var message = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, data, 3, "GROUP_MESSAGE_CREATE", "evt-mention-metadata"),
            "bot-user-openid", "测试机器人"));

        Assert.True(message.HasMention("bot-user-openid"));
        Assert.Equal("bot-user-openid", Assert.Single(message.Segments.OfType<MentionSegment>()).UserId);
        var called = false;
        var router = new CommandRouter<MessageEvent>();
        router.MapMention("bot-user-openid", _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        Assert.True(await router.DispatchAsync(message.GetPlainText().Trim(), message));
        Assert.True(called);
    }

    [Fact]
    public async Task MentioningAnotherBotDoesNotTriggerThisBotsMentionRoute()
    {
        var data = JsonDocument.Parse("""{"id":"message-other-bot-mention","group_openid":"group-1","author":{"member_openid":"member-1"},"content":"<@other-bot-member> 你好","mentions":[{"member_openid":"other-bot-member","username":"另一个机器人","bot":true}]}""").RootElement;
        var message = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, data, 4, "GROUP_MESSAGE_CREATE", "evt-other-bot-mention"),
            "bot-user-openid", "测试机器人"));

        Assert.True(message.HasMention("other-bot-member"));
        Assert.False(message.HasMention("bot-user-openid"));
        var called = false;
        var router = new CommandRouter<MessageEvent>();
        router.MapMention("bot-user-openid", _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        Assert.False(await router.DispatchAsync(message.GetPlainText().Trim(), message));
        Assert.False(called);
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
    public async Task ReplyChainTracksCachedMessagesAndBotRepliesIncludingMedia()
    {
        var firstData = JsonDocument.Parse("""{"id":"root-message","group_openid":"group-1","author":{"member_openid":"user-root","username":"最初发言者"},"content":"原始问题","attachments":[{"url":"https://example.org/original.png","content_type":"image/png"}],"message_scene":{"ext":["msg_idx=REF-ROOT"]}}""").RootElement;
        var first = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, firstData, 1, "GROUP_MESSAGE_CREATE", "event-root"), "bot-1"));
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var self = new User("bot-1") { Name = "测试机器人", IsBot = true };
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)),
            getSelfUser: () => self);
        messages.RegisterIncoming(first);

        await messages.SendMessageAsync(first.Channel, [new TextSegment("机器人回答")]);

        var userReplyData = JsonDocument.Parse("""{"id":"reply-to-bot","group_openid":"group-1","author":{"member_openid":"user-reply","username":"第二位发言者"},"content":"我补充一下","message_scene":{"ext":["msg_idx=REF-REPLY","ref_msg_idx=REF-OUT"]}}""").RootElement;
        var userReply = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, userReplyData, 2, "GROUP_MESSAGE_CREATE", "event-reply"), "bot-1"));
        Assert.Equal("REF-OUT", userReply.GetQuote()?.MessageId);
        userReply = messages.RegisterIncoming(userReply);

        var replyChain = ((JsonElement)userReply.Raw!).GetProperty("resolved_reply_chain");
        Assert.Equal(2, replyChain.GetArrayLength());
        Assert.Equal("outgoing-1", replyChain[0].GetProperty("messageId").GetString());
        Assert.Equal("测试机器人", replyChain[0].GetProperty("sender").GetProperty("name").GetString());
        Assert.Equal("机器人回答", replyChain[0].GetProperty("segments")[0].GetProperty("text").GetString());
        Assert.Equal("root-message", replyChain[1].GetProperty("messageId").GetString());
        Assert.Equal("最初发言者", replyChain[1].GetProperty("sender").GetProperty("name").GetString());
        Assert.Contains(replyChain[1].GetProperty("segments").EnumerateArray(), segment =>
            segment.TryGetProperty("type", out var type) && type.GetString() == "image"
            && segment.GetProperty("url").GetString() == "https://example.org/original.png");

        var followupData = JsonDocument.Parse("""{"id":"followup-message","group_openid":"group-1","author":{"member_openid":"user-followup","username":"第三位发言者"},"content":"继续补充","message_scene":{"ext":["msg_idx=REF-FOLLOWUP","ref_msg_idx=REF-REPLY"]}}""").RootElement;
        var followup = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, followupData, 3, "GROUP_MESSAGE_CREATE", "event-followup"), "bot-1"));
        followup = messages.RegisterIncoming(followup);
        var followupChain = ((JsonElement)followup.Raw!).GetProperty("resolved_reply_chain");
        Assert.Equal(new[] { "reply-to-bot", "outgoing-1", "root-message" },
            followupChain.EnumerateArray().Select(item => item.GetProperty("messageId").GetString()));
        Assert.Equal("user-reply", followupChain[0].GetProperty("sender").GetProperty("id").GetString());
    }

    [Fact]
    public void QuoteMessageElementsResolveStaleReferenceIndexAndProvideMediaFallback()
    {
        var data = JsonDocument.Parse("""{"id":"quote-message","group_openid":"group-1","author":{"member_openid":"user-1","username":"引用者"},"content":"回复内容","message_type":103,"message_scene":{"ext":["ref_msg_idx=TMP_stale","msg_idx=REFIDX_CURRENT"]},"msg_elements":[{"msg_idx":"REFIDX_ORIGINAL","content":"=== 消息 1 ===\n[消息内容] 原图说明\n[消息类型] 引用消息","attachments":[{"url":"https://example.org/quoted.png","content_type":"image/png","filename":"quoted.png"}]}]}""").RootElement;
        var incoming = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, data, 1, "GROUP_MESSAGE_CREATE", "event-quote"), "bot-1"));
        Assert.Equal("REFIDX_ORIGINAL", incoming.GetQuote()?.MessageId);

        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));
        var enriched = messages.RegisterIncoming(incoming);

        var original = Assert.Single(((JsonElement)enriched.Raw!).GetProperty("resolved_reply_chain").EnumerateArray());
        Assert.Equal("未知用户", original.GetProperty("sender").GetProperty("name").GetString());
        Assert.Contains(original.GetProperty("segments").EnumerateArray(), segment =>
            segment.TryGetProperty("type", out var type) && type.GetString() == "text"
            && segment.GetProperty("text").GetString() == "原图说明");
        var image = Assert.Single(original.GetProperty("segments").EnumerateArray(),
            segment => segment.GetProperty("type").GetString() == "image");
        Assert.Equal("https://example.org/quoted.png", image.GetProperty("url").GetString());
        Assert.Equal("quoted.png", image.GetProperty("filename").GetString());
    }

    [Fact]
    public void QuoteWithBrokenReferenceIndexMatchesUniqueCachedOriginal()
    {
        var sourceData = JsonDocument.Parse("""{"id":"source-message","group_openid":"group-1","author":{"member_openid":"source-user","username":"原作者"},"content":"唯一引用内容","message_scene":{"ext":["msg_idx=REFIDX_SOURCE"]}}""").RootElement;
        var source = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, sourceData, 1, "GROUP_MESSAGE_CREATE", "event-source"), "bot-1"));
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));
        messages.RegisterIncoming(source);

        var quoteData = JsonDocument.Parse("""{"id":"quote-message","group_openid":"group-1","author":{"member_openid":"reply-user","username":"回复者"},"content":"后续","message_type":103,"message_scene":{"ext":["ref_msg_idx=REFIDX_WRONG","msg_idx=REFIDX_QUOTE"]},"msg_elements":[{"content":"=== 消息 1 ===\n[消息内容] 唯一引用内容\n[消息类型] 引用消息\n[关联消息]\n--- 第1条 ---\n[消息内容] 其他内容"}]}""").RootElement;
        var quote = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, quoteData, 2, "GROUP_MESSAGE_CREATE", "event-quote"), "bot-1"));
        var enriched = messages.RegisterIncoming(quote);

        var original = ((JsonElement)enriched.Raw!).GetProperty("resolved_reply_chain")[0];
        Assert.Equal("source-message", original.GetProperty("messageId").GetString());
        Assert.Equal("原作者", original.GetProperty("sender").GetProperty("name").GetString());
    }

    [Fact]
    public void QuoteOfQuoteWithTemporaryIndexResolvesFullChainFromCache()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));
        MessageEvent Incoming(string json, long seq) => Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, JsonDocument.Parse(json).RootElement, seq, "GROUP_MESSAGE_CREATE", $"event-{seq}"), "bot-1"));

        messages.RegisterIncoming(Incoming("""{"id":"m1","group_openid":"g","author":{"member_openid":"u","username":"农夫山泉"},"content":"1","message_scene":{"ext":["msg_idx=REFIDX_M1"]}}""", 1));
        messages.RegisterIncoming(Incoming("""{"id":"m2","group_openid":"g","author":{"member_openid":"u","username":"农夫山泉"},"content":"2","message_type":103,"message_scene":{"ext":["msg_idx=REFIDX_M2","ref_msg_idx=REFIDX_M1"]},"msg_elements":[{"msg_idx":"REFIDX_M1","content":"=== 消息 1 ===\n[消息内容] 1\n[消息类型] 普通消息"}]}""", 2));
        var third = messages.RegisterIncoming(Incoming("""{"id":"m3","group_openid":"g","author":{"member_openid":"u","username":"农夫山泉"},"content":"3","message_type":103,"message_scene":{"ext":["msg_idx=REFIDX_M3","ref_msg_idx=TMP_x"]},"msg_elements":[{"content":"=== 消息 1 ===\n[消息内容] 2\n[消息类型] 引用消息\n[关联消息]\n--- 第1条 ---\n[消息内容] 1\n[消息类型] 普通消息"}]}""", 3));

        var chain = ((JsonElement)third.Raw!).GetProperty("resolved_reply_chain");
        Assert.Equal(new[] { "m2", "m1" }, chain.EnumerateArray().Select(item => item.GetProperty("messageId").GetString()));
        Assert.Equal("农夫山泉", chain[0].GetProperty("sender").GetProperty("name").GetString());
        Assert.Equal("2", third.GetQuote()?.Preview);

        var fourth = messages.RegisterIncoming(Incoming("""{"id":"m4","group_openid":"g","author":{"member_openid":"u","username":"农夫山泉"},"content":"4","message_scene":{"ext":["msg_idx=REFIDX_M4","ref_msg_idx=TMP_x"]}}""", 4));
        Assert.Equal(2, ((JsonElement)fourth.Raw!).GetProperty("resolved_reply_chain").GetArrayLength());
    }

    [Fact]
    public void QuoteOfQuoteWithoutCacheStillExposesAncestorsFromMessageElements()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var messages = new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));
        var incoming = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(new GatewayPayload(0,
            JsonDocument.Parse("""{"id":"m3","group_openid":"g","author":{"member_openid":"u","username":"甲"},"content":"3","message_type":103,"message_scene":{"ext":["ref_msg_idx=TMP_x"]},"msg_elements":[{"content":"=== 消息 1 ===\n[消息内容] 2\n[消息类型] 引用消息\n[关联消息]\n--- 第1条 ---\n[消息内容] 1\n[消息类型] 普通消息"}]}""").RootElement,
            1, "GROUP_MESSAGE_CREATE", "event-3"), "bot-1"));
        var chain = ((JsonElement)messages.RegisterIncoming(incoming).Raw!).GetProperty("resolved_reply_chain");
        Assert.Equal(2, chain.GetArrayLength());
        Assert.Equal("1", chain[1].GetProperty("segments")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ChannelServiceListsAllMemberPagesAndMapsRoles()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var channels = new QQChannelService();
        channels.Attach(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));

        var members = await channels.GetMembersAsync("group-1");

        Assert.Equal(new[] { "m1", "m2", "m3" }, members.Select(member => member.User.Id));
        Assert.Equal(new[] { MemberRole.Admin, MemberRole.Member, MemberRole.Owner }, members.Select(member => member.Role));
        Assert.True(members[1].User.IsBot);
        Assert.Equal(new DateTimeOffset(2025, 8, 20, 9, 15, 0, TimeSpan.FromHours(8)), members[0].JoinedAt);
        Assert.Contains(handler.Requests, request => request.Uri.EndsWith("/v2/groups/group-1/members?cursor=c1"));
        Assert.Equal("m1", (await channels.GetMemberAsync("group-1", "m1"))!.User.Id);
    }

    [Fact]
    public async Task ChannelServiceMutesAndKicksThroughOfficialEndpoints()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var channels = new QQChannelService();
        channels.Attach(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));

        await channels.MuteMemberAsync("group-1", "m1", TimeSpan.FromMinutes(10));
        var mute = JsonDocument.Parse(handler.Requests[^1].Body).RootElement.GetProperty("members")[0];
        Assert.Equal("add", mute.GetProperty("op").GetString());
        Assert.Equal("m1", mute.GetProperty("member_openid").GetString());
        Assert.True(DateTimeOffset.TryParse(mute.GetProperty("mute_expire_at").GetString(), out var expiry));
        Assert.InRange(expiry - DateTimeOffset.Now, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));

        await channels.MuteMemberAsync("group-1", "m1", TimeSpan.Zero);
        Assert.Equal("del", JsonDocument.Parse(handler.Requests[^1].Body).RootElement.GetProperty("members")[0].GetProperty("op").GetString());

        await channels.KickMemberAsync("group-1", "m1");
        Assert.EndsWith("/v2/groups/group-1/batch_remove_members", handler.Requests[^1].Uri);
        Assert.Equal("m1", JsonDocument.Parse(handler.Requests[^1].Body).RootElement.GetProperty("member_openids")[0].GetString());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            channels.MuteMemberAsync("group-1", "m1", TimeSpan.FromDays(31)));
    }

    [Fact]
    public async Task ChannelServiceNeedsAStartedAdapter() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => new QQChannelService().GetMembersAsync("group-1"));

    [Theory]
    [InlineData("GROUP_MSG_RECEIVE", QEventKinds.OfficialGroupMsgReceive, true, """{"timestamp":"2026-01-01T00:00:00+08:00","group_openid":"g1","op_member_openid":"op1"}""", "op1")]
    [InlineData("FRIEND_ADD", QEventKinds.OfficialFriendAdd, false, """{"timestamp":1767196800,"openid":"u1"}""", "u1")]
    [InlineData("C2C_MSG_REJECT", QEventKinds.OfficialC2CMsgReject, false, """{"timestamp":1767196800,"openid":"u1"}""", "u1")]
    public void LifecycleEventsKeepChannelEventIdAndOperator(string type, string kind, bool group, string json, string operatorId)
    {
        var translated = Assert.IsType<PlatformEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, JsonDocument.Parse(json).RootElement, 5, type, "event-9"), "bot-1"));
        Assert.Equal(kind, translated.Kind);
        Assert.Equal(group ? ChannelType.Group : ChannelType.Direct, translated.Channel!.Type);
        Assert.Equal(group ? "g1" : "u1", translated.Channel.Id);
        var payload = Assert.IsType<QOfficialLifecycleEvent>(translated.Raw);
        Assert.Equal("event-9", payload.EventId);
        Assert.Equal(operatorId, payload.OperatorId);
        Assert.Equal(group ? QOfficialMessageScene.Group : QOfficialMessageScene.Direct, payload.Target.Scene);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)), payload.EventTime);
    }

    [Fact]
    public void BotJoiningAndLeavingAGroupUseTheGenericSdkEvents()
    {
        var added = Assert.IsType<GuildInviteEvent>(QQEventTranslator.Translate(new GatewayPayload(0,
            JsonDocument.Parse("""{"timestamp":1767196800,"group_openid":"g1","op_member_openid":"op1"}""").RootElement,
            5, "GROUP_ADD_ROBOT", "event-add"), "bot-1"));
        Assert.Equal(("g1", "op1"), (added.GuildId, added.InviterId));
        Assert.Equal("event-add", Assert.IsType<QOfficialLifecycleEvent>(added.Raw).EventId);

        var removed = Assert.IsType<MemberLeftEvent>(QQEventTranslator.Translate(new GatewayPayload(0,
            JsonDocument.Parse("""{"timestamp":1767196800,"group_openid":"g1","op_member_openid":"op1"}""").RootElement,
            6, "GROUP_DEL_ROBOT", "event-del"), "bot-1"));
        Assert.Equal(("g1", "bot-1", "op1"), (removed.Channel.Id, removed.UserId, removed.OperatorId));
    }

    [Fact]
    public async Task IncomingQuoteCarriesTheQuotedMessagesOwnIdSoReplySubscriptionsMatch()
    {
        var handler = new FakeHandler();
        var messages = NewMessages(handler, out var http);
        using var httpScope = http;
        var channel = Channel.Group("g");
        var sent = await messages.SendMessageAsync(channel, [new TextSegment("question?")]);   // outgoing-1, ext_info REF-OUT
        var reply = messages.RegisterIncoming(new MessageEvent
        {
            Platform = "qq-official", MessageId = "user-reply", Channel = channel, Sender = new User("u"),
            Segments = [new QuoteSegment("REF-OUT"), new TextSegment("answer")]
        });
        Assert.Equal(sent.MessageId, reply.GetQuote()!.MessageId);
        Assert.Equal("question?", reply.GetQuote()!.Preview);

    }

    [Fact]
    public async Task UnresolvedQuoteKeepsItsRawIndexAndCanBeSentBackAsAReference()
    {
        var handler = new FakeHandler();
        var messages = NewMessages(handler, out var http);
        using var httpScope = http;
        var channel = Channel.Group("g");
        messages.RegisterIncoming(Incoming("incoming", channel));
        var reply = messages.RegisterIncoming(new MessageEvent
        {
            Platform = "qq-official", MessageId = "later", Channel = channel, Sender = new User("u"),
            Segments = [new QuoteSegment("REFIDX_UNKNOWN"), new TextSegment("hmm")]
        });
        Assert.Equal("REFIDX_UNKNOWN", reply.GetQuote()!.MessageId);

        await messages.SendMessageAsync(channel, [new QuoteSegment("REFIDX_UNKNOWN"), new TextSegment("re")]);
        var body = LastMessageBody(handler);
        Assert.Equal("REFIDX_UNKNOWN", body.GetProperty("message_reference").GetProperty("message_id").GetString());
        Assert.Equal("later", body.GetProperty("msg_id").GetString());   // replies to the latest real message
    }

    [Fact]
    public async Task GetMessageReturnsRecentMessagesAndNullForUnknownOnes()
    {
        var handler = new FakeHandler();
        var messages = NewMessages(handler, out var http);
        using var httpScope = http;
        var channel = Channel.Group("g");
        messages.RegisterIncoming(Incoming("seen", channel));
        var sent = await messages.SendMessageAsync(channel, [new TextSegment("hi")]);
        Assert.Equal("seen", (await messages.GetMessageAsync(channel, "seen"))!.MessageId);
        Assert.Equal("hi", (await messages.GetMessageAsync(channel, sent.MessageId))!.GetPlainText());
        Assert.Null(await messages.GetMessageAsync(channel, "never-seen"));
        Assert.Null(await messages.GetMessageAsync(Channel.Group("other"), "seen"));
    }

    [Fact]
    public void AttachmentsKeepVoiceTranscriptImageSizeAndFileSize()
    {
        var data = JsonDocument.Parse("""{"id":"m","group_openid":"g","author":{"member_openid":"u"},"content":"","attachments":[{"url":"https://x/a.silk","content_type":"audio/silk","voice_wav_url":"https://x/a.wav","asr_refer_text":"你好世界"},{"url":"https://x/i.png","content_type":"image/png","width":640,"height":480},{"url":"https://x/f.zip","content_type":"application/zip","filename":"f.zip","size":1234}]}""").RootElement;
        var message = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(
            new GatewayPayload(0, data, 1, "GROUP_MESSAGE_CREATE", "e"), "bot-1"));
        var audio = Assert.Single(message.Segments.OfType<AudioSegment>());
        Assert.Equal("https://x/a.wav", audio.Uri);
        Assert.Equal("你好世界", audio.Transcript);
        var image = Assert.Single(message.Segments.OfType<ImageSegment>());
        Assert.Equal((640, 480), (image.Width, image.Height));
        Assert.Equal(1234, Assert.Single(message.Segments.OfType<FileSegment>()).FileSize);
    }

    [Fact]
    public async Task RemoteUploadsAreReusedUntilTheFileInfoExpires()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        var channel = Channel.Group("g");
        var first = await api.UploadRemoteAsync(channel, 1, new Uri("https://example.org/a.png"));
        var second = await api.UploadRemoteAsync(channel, 1, new Uri("https://example.org/a.png"));
        await api.UploadRemoteAsync(Channel.Group("other"), 1, new Uri("https://example.org/a.png"));
        Assert.Equal(first, second);
        Assert.Equal(2, handler.Requests.Count(request => request.Uri.EndsWith("/files")));
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

    private static QQMessageService NewMessages(FakeHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        return new QQMessageService(new QQOpenApiClient(http, config, new QQTokenProvider(http, config)));
    }

    private static MessageEvent Incoming(string id, Channel channel) => new()
    {
        Platform = "qq-official", MessageId = id, Channel = channel,
        Sender = new User("user"), Segments = [new TextSegment("ping")]
    };

    private static JsonElement LastMessageBody(FakeHandler handler) => JsonDocument.Parse(handler.Requests[^1].Body).RootElement;

    [Fact]
    public async Task SendsProactivelyWhenThereIsNoRecentIncomingMessage()
    {
        var handler = new FakeHandler();
        var messages = NewMessages(handler, out var http);
        using var httpScope = http;
        await messages.SendMessageAsync(Channel.Direct("user"), [new TextSegment("hello")]);
        Assert.False(LastMessageBody(handler).TryGetProperty("msg_id", out _));
        Assert.False(LastMessageBody(handler).TryGetProperty("msg_seq", out _));
    }

    [Fact]
    public async Task GenericReplyUsesRecentIncomingMessageInSameChannel()
    {
        var handler = new FakeHandler();
        var messages = NewMessages(handler, out var http);
        using var httpScope = http;
        messages.RegisterIncoming(Incoming("incoming", Channel.Group("group-1")));
        await messages.SendMessageAsync(Channel.Group("group-1"), [new TextSegment("pong")]);
        Assert.Equal("incoming", LastMessageBody(handler).GetProperty("msg_id").GetString());
        await messages.SendMessageAsync(Channel.Group("other"), [new TextSegment("pong")]);
        Assert.False(LastMessageBody(handler).TryGetProperty("msg_id", out _));
    }

    [Theory]
    [InlineData(true, 5)]
    [InlineData(false, 4)]
    public async Task PassiveRepliesStopAtTheSceneLimitAndContinueProactively(bool group, int limit)
    {
        var handler = new FakeHandler();
        var messages = NewMessages(handler, out var http);
        using var httpScope = http;
        var channel = group ? Channel.Group("g") : Channel.Direct("u");
        messages.RegisterIncoming(Incoming("incoming", channel));
        for (var i = 1; i <= limit + 1; i++)
        {
            await messages.SendMessageAsync(channel, [new TextSegment($"reply {i}")]);
            var body = LastMessageBody(handler);
            if (i <= limit)
            {
                Assert.Equal("incoming", body.GetProperty("msg_id").GetString());
                Assert.Equal(i, body.GetProperty("msg_seq").GetInt32());
            }
            else Assert.False(body.TryGetProperty("msg_id", out _));
        }
    }

    [Fact]
    public async Task RefusedPassiveReplyIsResentProactively()
    {
        var handler = new FakeHandler { ExpirePassiveReply = true };
        var messages = NewMessages(handler, out var http);
        using var httpScope = http;
        messages.RegisterIncoming(Incoming("incoming", Channel.Group("g")));
        var sent = await messages.SendMessageAsync(Channel.Group("g"), [new TextSegment("late")]);
        Assert.Equal("outgoing-1", sent.MessageId);
        var posts = handler.Requests.Where(request => request.Uri.EndsWith("/messages")).ToArray();
        Assert.Equal(2, posts.Length);
        Assert.Contains("\"msg_id\"", posts[0].Body);
        Assert.DoesNotContain("\"msg_id\"", posts[1].Body);
    }

    [Fact]
    public async Task LongTextIsSplitIntoSeveralMessages()
    {
        var handler = new FakeHandler();
        var messages = NewMessages(handler, out var http);
        using var httpScope = http;
        var text = string.Join("\n", Enumerable.Range(0, 1500).Select(i => $"line {i}"));
        await messages.SendMessageAsync(Channel.Direct("u"), [new TextSegment(text)]);
        var contents = handler.Requests.Where(request => request.Uri.EndsWith("/messages"))
            .Select(request => JsonDocument.Parse(request.Body).RootElement.GetProperty("content").GetString()!).ToArray();
        Assert.True(contents.Length > 1);
        Assert.All(contents, content => Assert.True(content.Length <= QQTextChunker.MaxLength));
        Assert.Equal(text, string.Concat(contents));
    }

    [Theory]
    [InlineData(4914, true, false, false)]
    [InlineData(4915, true, false, false)]
    [InlineData(4004, false, false, true)]
    [InlineData(4009, false, true, true)]
    [InlineData(4006, false, true, true)]
    [InlineData(4907, false, true, true)]
    [InlineData(1006, false, false, false)]
    public void GatewayCloseCodesMapToTheRightRecovery(int code, bool fatal, bool clearSession, bool refreshToken)
    {
        var action = QQGatewayClosePolicy.Decide(code);
        Assert.Equal((fatal, clearSession, refreshToken), (action.Fatal, action.ClearSession, action.RefreshToken));
    }

    [Fact]
    public void GatewayRateLimitCloseWaitsAMinute() =>
        Assert.Equal(TimeSpan.FromSeconds(60), QQGatewayClosePolicy.Decide(4008).Delay);

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
        public bool ExpirePassiveReply { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
            var response = request.RequestUri!.AbsolutePath == "/app/getAppAccessToken"
                ? RejectToken ? """{"code":100016,"message":"invalid appid or secret"}""" : """{"access_token":"token-1","expires_in":7200}"""
                : OpenApiErrorStatus is not null
                    ? """{"err_code":40034005,"message":"msg_id expired","trace_id":"trace-1"}"""
                : request.RequestUri.AbsolutePath.EndsWith("/members") && request.RequestUri.Query.Contains("cursor=c1")
                    ? """{"members":[{"member_openid":"m3","username":"丙","member_role":"owner","bot":false}],"next_cursor":""}"""
                : request.RequestUri.AbsolutePath.EndsWith("/members")
                    ? """{"members":[{"member_openid":"m1","username":"甲","member_role":"admin","bot":false,"joined_at":"2025-08-20T09:15:00+08:00"},{"member_openid":"m2","username":"乙","member_role":"member","bot":true}],"next_cursor":"c1"}"""
                : request.RequestUri.AbsolutePath.Contains("/members/")
                    ? """{"member_openid":"m1","username":"甲","member_role":"admin","bot":false}"""
                : request.RequestUri.AbsolutePath.EndsWith("/restrict_chat_setting") || request.RequestUri.AbsolutePath.EndsWith("/batch_remove_members")
                    ? "{}"
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
                    : """{"id":"outgoing-1","timestamp":1767225600,"ext_info":{"ref_idx":"REF-OUT"}}""";
            if (ExpirePassiveReply && request.RequestUri.AbsolutePath.EndsWith("/messages") && body.Contains("\"msg_id\"", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"err_code":40034128,"message":"passive reply expired","trace_id":"trace-2"}""")
                };
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
