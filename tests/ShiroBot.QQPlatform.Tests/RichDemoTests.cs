using System.Reflection;
using ShiroBot.Model.QQ;
using ShiroBot.Plugin.QQPlatformRichDemo;
using ShiroBot.SDK.Config;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Xunit;

using ShiroBot.Adapter.QQPlatform.Transport;
using ShiroBot.Adapter.QQPlatform.Transport.Gateway;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Official;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Groups;
using ShiroBot.Adapter.QQPlatform.Protocol.Support;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
namespace ShiroBot.QQPlatform.Tests;

public sealed class RichDemoTests
{
    [Theory]
    [InlineData("#alltest", false, true, false)]
    [InlineData("#alltest", true, false, false)]
    [InlineData("#alltest", true, true, true)]
    [InlineData("/allmdtest", true, true, true)]
    public async Task EveryoneRequiresOptInAndInstanceScopedAdmin(string command, bool enabled, bool admin, bool sends)
    {
        var fixture = new Fixture { Admin = admin };
        fixture.Config.EnableEveryoneDemo = enabled;
        await fixture.Plugin.OnLoad(fixture.Context);
        await fixture.DispatchAsync(command);
        var outgoing = Assert.Single(fixture.Replies).Content;
        Assert.Equal(sends, outgoing.Segments.Any(segment => segment is MentionAllSegment
            || segment is MarkdownSegment md && md.Content.Contains("<qqbot-at-everyone />")));
        if (sends) Assert.Equal(MessageFallbackOptions.None, outgoing.AllowedFallbacks);
        await fixture.Plugin.OnUnload();
    }

    [Fact]
    public async Task EveryoneInDirectMessagesNeverNotifies()
    {
        var fixture = new Fixture { Admin = true };
        fixture.Config.EnableEveryoneDemo = true;
        await fixture.Plugin.OnLoad(fixture.Context);
        await fixture.DispatchAsync("#alltest", direct: true);
        Assert.IsType<TextSegment>(Assert.Single(Assert.Single(fixture.Replies).Content.Segments));
        await fixture.Plugin.OnUnload();
    }

    [Fact]
    public async Task InteractionReplyUsesUnifiedEventAndKeepsSourceInstance()
    {
        var fixture = new Fixture();
        await fixture.Plugin.OnLoad(fixture.Context);
        var interaction = new InteractionEvent
        {
            Platform = "qq-official", InstanceId = "qq-main", Kind = QEventKinds.OfficialButtonInteraction,
            Channel = Channel.Group("g"), InteractionId = "click", User = new User("u"), Data = "rich:help"
        };
        await ((IBotEventSubscriber)fixture.Plugin).OnEventAsync(interaction);
        var reply = Assert.Single(fixture.Replies);
        Assert.Same(interaction, reply.Source);
        Assert.IsType<MarkdownSegment>(Assert.Single(reply.Content.Segments));
        Assert.Contains("#about", ((MarkdownSegment)reply.Content.Segments[0]).Content);
        Assert.Contains(fixture.Plugin.GetEventRoutes(), route => route.EventType == typeof(InteractionEvent));
        Assert.DoesNotContain(fixture.Plugin.GetEventRoutes(), route => route.PlatformKind == QEventKinds.OfficialButtonInteraction);
        await fixture.Plugin.OnUnload();
    }

    [Fact]
    public async Task GenericCardUsesSdkContractAndGeneratedConfigModel()
    {
        var fixture = new Fixture();
        fixture.Config.CardImageUrl = "https://example.org/image.png";
        await fixture.Plugin.OnLoad(fixture.Context);
        Assert.Equal(typeof(RichDemoConfig), ((IConfigurableComponent)fixture.Plugin).ConfigType);
        await fixture.DispatchAsync("/cardtest");
        var card = Assert.IsType<CardSegment>(Assert.Single(Assert.Single(fixture.Replies).Content.Segments));
        Assert.Equal(fixture.Config.CardTitle, card.Title);
        Assert.Equal(fixture.Config.CardImageUrl, card.ImageUrl);
        await fixture.Plugin.OnUnload();
    }

    [Fact]
    public async Task MaterialCommandsAreGuardedAndAcceptExplicitCursorAndImageId()
    {
        var fixture = new Fixture();
        var requests = new List<string>();
        fixture.Materials = Proxy<IQOfficialMaterialApi>((method, args) =>
        {
            if (method.Name == "GetImagesAsync")
            {
                requests.Add("list:" + args[0]);
                return Task.FromResult(new QOfficialImageMaterialPage([], null, 0));
            }
            if (method.Name == "DeleteImageAsync")
            {
                requests.Add("delete:" + args[0]);
                return Task.CompletedTask;
            }
            throw new NotSupportedException(method.Name);
        });
        await fixture.Plugin.OnLoad(fixture.Context);
        await fixture.DispatchAsync("#materialdelete image-1");
        Assert.Empty(requests);
        fixture.Admin = true;
        await fixture.DispatchAsync("#materiallist opaque-cursor");
        await fixture.DispatchAsync("/materialdelete image-1");
        Assert.Equal(new[] { "list:opaque-cursor", "delete:image-1" }, requests);
        await fixture.Plugin.OnUnload();
    }

    [Fact]
    public async Task NonQqCommandsAreIgnoredAndRichSendsInteractiveMenu()
    {
        var fixture = new Fixture();
        await fixture.Plugin.OnLoad(fixture.Context);
        await fixture.DispatchAsync("#rich", platform: "other");
        Assert.Empty(fixture.Replies);
        await fixture.DispatchAsync("#rich");
        var menu = Assert.Single(fixture.Replies).Content;
        var markdown = Assert.IsType<MarkdownSegment>(Assert.Single(menu.Segments));
        Assert.Contains("RichDemo", markdown.Content);
        Assert.NotNull(menu.Buttons);
        Assert.Contains(menu.Buttons.Rows.SelectMany(row => row.Buttons),
            button => button.Action is CallbackAction { Data: "rich:help" });
        await fixture.Plugin.OnUnload();
    }

    [Fact]
    public async Task MenuButtonRepliesByCallbackDataAndOffersCapabilityTests()
    {
        var fixture = new Fixture();
        await fixture.Plugin.OnLoad(fixture.Context);
        var interaction = new InteractionEvent
        {
            Platform = "qq-official", InstanceId = "qq-main", Kind = QEventKinds.OfficialButtonInteraction,
            Channel = Channel.Group("g"), InteractionId = "click", User = new User("u"), Data = "rich:caps"
        };
        await ((IBotEventSubscriber)fixture.Plugin).OnEventAsync(interaction);
        var content = Assert.Single(fixture.Replies).Content;
        var text = string.Concat(content.Segments.Select(segment => segment switch
        {
            TextSegment textSegment => textSegment.Text,
            MarkdownSegment markdown => markdown.Content,
            _ => ""
        }));
        Assert.Contains("原生通用能力", text);
        Assert.NotNull(content.Buttons);
        var callbacks = content.Buttons.Rows.SelectMany(row => row.Buttons)
            .Select(button => button.Action).OfType<CallbackAction>().Select(action => action.Data).ToArray();
        Assert.Contains("rich:test:official", callbacks);
        Assert.Contains("rich:test:media", callbacks);
        Assert.Contains("rich:test:material", callbacks);
        Assert.Contains("rich:test:user", callbacks);
        Assert.Contains("rich:test:file", callbacks);
        Assert.Contains("rich:test:stream", callbacks);
        Assert.Contains("rich:test:group", callbacks);
        await fixture.Plugin.OnUnload();
    }

    [Theory]
    [InlineData("#about", "https://example.org/avatar.png", true)]
    [InlineData("/me", null, false)]
    [InlineData("#whoami", "file:///private.png", false)]
    public async Task AboutShowsNameAndGroupNicknameAndOnlyUsesHttpAvatars(string command, string? avatar, bool showsImage)
    {
        var fixture = new Fixture();
        await fixture.Plugin.OnLoad(fixture.Context);
        var user = new User("member-id") { Name = "账号名称", AvatarUrl = avatar };
        await ((IBotEventSubscriber)fixture.Plugin).OnEventAsync(new MessageEvent
        {
            Platform = "qq-official", InstanceId = "qq-main", MessageId = "m", Channel = Channel.Group("g"),
            Sender = user, Member = new Member(user) { Nick = "群内昵称" }, Segments = [new TextSegment(command)]
        });
        var content = Assert.Single(fixture.Replies).Content;
        var text = string.Concat(content.Segments.OfType<TextSegment>().Select(segment => segment.Text));
        Assert.Contains("账号名称", text);
        Assert.Contains("群内昵称", text);
        Assert.Contains("member_openid: member-id", text);
        Assert.Equal(showsImage, content.Segments.OfType<ImageSegment>().Any());
        if (showsImage) Assert.Equal(avatar, Assert.Single(content.Segments.OfType<ImageSegment>()).Uri);
        await fixture.Plugin.OnUnload();
    }

    [Fact]
    public async Task UserInfoUsesDirectOpenIdAndShowsReturnedAvatar()
    {
        var fixture = new Fixture();
        var calls = new List<string>();
        fixture.Users = Proxy<IQSystemApi>((_, args) =>
        {
            calls.Add((string)args[0]!);
            return Task.FromResult(new QUserProfile
            {
                UserId = (string)args[0]!, Nickname = "查询名称", AvatarUrl = "https://example.org/user.png",
                UnionId = "union-user", AllowProactiveMessage = true
            });
        });
        await fixture.Plugin.OnLoad(fixture.Context);
        await fixture.DispatchAsync("#userinfo");
        Assert.Empty(calls);
        fixture.Replies.Clear();
        await fixture.DispatchAsync("#userinfo", direct: true);
        Assert.Equal(new[] { "u" }, calls);
        var content = Assert.Single(fixture.Replies).Content;
        Assert.Equal("https://example.org/user.png", Assert.Single(content.Segments.OfType<ImageSegment>()).Uri);
        Assert.Contains("union-user", Assert.Single(content.Segments.OfType<TextSegment>()).Text);
        await fixture.Plugin.OnUnload();
    }

    [Fact]
    public async Task MaterialDemoUploadsAValidPngAndUsesReturnedMarkdownReference()
    {
        var fixture = new Fixture { Admin = true };
        byte[]? png = null;
        QOfficialMessage? sent = null;
        fixture.Materials = Proxy<IQOfficialMaterialApi>((method, args) =>
        {
            Assert.Equal("UploadImageAsync", method.Name);
            png = ((MemoryStream)args[0]!).ToArray();
            return Task.FromResult(new QOfficialImageMaterial("demo-id", "qqbot-media://image/v1/demo"));
        });
        fixture.Official = Proxy<IQOfficialMessageApi>((method, args) =>
        {
            Assert.Equal("SendAsync", method.Name);
            sent = (QOfficialMessage)args[1]!;
            return Task.FromResult("sent");
        });
        await fixture.Plugin.OnLoad(fixture.Context);
        await fixture.DispatchAsync("#materialtest");
        Assert.NotNull(png);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var offset = 8;
        while (offset < png.Length)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            uint crc = uint.MaxValue;
            foreach (var value in png.AsSpan(offset + 4, length + 4))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
            Assert.Equal(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4)), ~crc);
            offset += length + 12;
        }
        var markdown = Assert.IsType<QCustomMarkdown>(Assert.IsType<QOfficialMarkdownMessage>(sent).Content);
        Assert.Contains("qqbot-media://image/v1/demo", markdown.Content);
        Assert.True(markdown.ForceVerifyImageResource);
        Assert.Contains("#materialdelete demo-id", Assert.Single(fixture.Replies).Content.Segments.OfType<TextSegment>().Single().Text);
        await fixture.Plugin.OnUnload();
    }

    [Fact]
    public async Task ReplyCommandsQuoteTheSourceMessage()
    {
        var fixture = new Fixture();
        fixture.Config.ImageUrl = "https://example.org/image.png";
        await fixture.Plugin.OnLoad(fixture.Context);
        await fixture.DispatchAsync("#replytext");
        var text = Assert.Single(fixture.Replies).Content;
        Assert.Equal("m", text.ReplyTo?.MessageId);
        Assert.IsType<TextSegment>(Assert.Single(text.Segments));

        fixture.Replies.Clear();
        await fixture.DispatchAsync("#replyimage");
        var image = Assert.Single(fixture.Replies).Content;
        Assert.Equal("m", image.ReplyTo?.MessageId);
        Assert.IsType<ImageSegment>(Assert.Single(image.Segments));

        fixture.Replies.Clear();
        await fixture.DispatchAsync("#replymd");
        var markdown = Assert.Single(fixture.Replies).Content;
        Assert.Equal("m", markdown.ReplyTo?.MessageId);
        Assert.IsType<MarkdownSegment>(Assert.Single(markdown.Segments));
        Assert.NotNull(markdown.Buttons);

        fixture.Replies.Clear();
        await fixture.DispatchAsync("#replyaudio");
        var audio = fixture.Replies[^1].Content;
        Assert.Equal("m", audio.ReplyTo?.MessageId);
        Assert.IsType<AudioSegment>(Assert.Single(audio.Segments.OfType<AudioSegment>()));

        fixture.Replies.Clear();
        await fixture.DispatchAsync("#replyvideo");
        var video = fixture.Replies[^1].Content;
        Assert.Equal("m", video.ReplyTo?.MessageId);
        Assert.IsType<VideoSegment>(Assert.Single(video.Segments.OfType<VideoSegment>()));

        fixture.Replies.Clear();
        await fixture.DispatchAsync("#replyfile");
        var file = fixture.Replies[^1].Content;
        Assert.Equal("m", file.ReplyTo?.MessageId);
        Assert.Equal("demo.pdf", Assert.IsType<FileSegment>(Assert.Single(file.Segments.OfType<FileSegment>())).FileName);
        await fixture.Plugin.OnUnload();
    }

    [Fact]
    public async Task CapabilityTestButtonActuallyCallsOfficialTextApi()
    {
        var fixture = new Fixture();
        var calls = new List<string>();
        fixture.Official = Proxy<IQOfficialMessageApi>((method, args) =>
        {
            calls.Add(method.Name);
            return method.Name == "SendTextAsync" ? Task.FromResult("sent") : throw new NotSupportedException(method.Name);
        });
        await fixture.Plugin.OnLoad(fixture.Context);
        var interaction = new InteractionEvent
        {
            Platform = "qq-official", InstanceId = "qq-main", Kind = QEventKinds.OfficialButtonInteraction,
            Channel = Channel.Direct("u"), InteractionId = "click", User = new User("u"),
            Data = "rich:test:official"
        };
        await ((IBotEventSubscriber)fixture.Plugin).OnEventAsync(interaction);
        Assert.Equal(new[] { "SendTextAsync" }, calls);
        Assert.Equal("官方消息测试已发送。",
            Assert.IsType<TextSegment>(Assert.Single(Assert.Single(fixture.Replies).Content.Segments)).Text);
        await fixture.Plugin.OnUnload();
    }

    [Fact]
    public async Task GroupInfoShowsApiFieldsAndReportsInviteOnlyFailures()
    {
        var fixture = new Fixture
        {
            Groups = new FakeGroups
            {
                Capabilities = QGroupCapabilities.GroupInfo | QGroupCapabilities.BotState
                    | QGroupCapabilities.MemberBlacklist
            }
        };
        await fixture.Plugin.OnLoad(fixture.Context);
        await fixture.DispatchAsync("#groupinfo");
        var text = Assert.Single(Assert.Single(fixture.Replies).Content.Segments.OfType<TextSegment>()).Text;
        Assert.Contains("简介: 每周共读", text);
        Assert.Contains("分类: 文化", text);
        Assert.Contains("标签: 阅读、文学", text);
        Assert.Contains("查询 union_group_openid: union-g", text);
        Assert.Contains("未获接口权限", text);
        Assert.Contains("群黑名单", text);
        await fixture.Plugin.OnUnload();
    }

    private sealed class FakeGroups : IQGroupApi
    {
        public QGroupCapabilities Capabilities { get; init; }

        public Task<QGroup> GetGroupInfoAsync(string groupId, bool noCache = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new QGroup
            {
                GroupId = groupId,
                GroupName = "读书会",
                Description = "每周共读",
                ClassText = "文化",
                Tags = ["阅读", "文学"],
                MemberCount = 256,
                UnionId = "union-g"
            });

        public Task<QGroupBotState> GetBotStateAsync(string groupId,
            CancellationToken cancellationToken = default) =>
            throw new QPermissionDeniedException("机器人群内状态", 40012010, "应用无接口访问权限");

        public Task<QGroupBlacklistPage> GetGroupBlacklistAsync(string groupId, string? cursor = null,
            int limit = 20, CancellationToken cancellationToken = default) =>
            throw new QPermissionDeniedException("群黑名单查询", 40012010, "应用无接口访问权限");
    }

    private sealed class Fixture
    {
        public RichDemoPlugin Plugin { get; } = new();
        public RichDemoConfig Config { get; } = new();
        public bool Admin { get; set; }
        public IQOfficialMaterialApi? Materials { get; set; }
        public IQSystemApi? Users { get; set; }
        public IQOfficialMessageApi? Official { get; set; }
        public IQGroupApi? Groups { get; set; }
        public List<(object Source, OutgoingMessage Content)> Replies { get; } = [];
        public IBotContext Context { get; }

        public Fixture()
        {
            var config = Proxy<IConfigContext>((method, _) => method.Name == "Load" ? Config : throw new NotSupportedException(method.Name));
            var lifetime = Proxy<IPluginLifetime>((method, _) => method.Name == "get_Stopping" ? CancellationToken.None : throw new NotSupportedException(method.Name));
            var messages = Proxy<IMessageContext>((method, args) =>
            {
                if (method.Name == "ReplyAsync")
                {
                    Replies.Add((args[0]!, (OutgoingMessage)args[1]!));
                    return Task.FromResult(new SentMessage("reply"));
                }
                if (method.Name == "AssessMessage") return new MessageSendAssessment { IsSupported = true };
                if (method.Name == "GetMessageCapabilities") return new MessageCapabilities { NativeFeatures = MessageFeatures.Text };
                throw new NotSupportedException(method.Name);
            });
            Context = Proxy<IBotContext>((method, args) => method.Name switch
            {
                "get_Config" => config,
                "get_Lifetime" => lifetime,
                "get_Message" => messages,
                "get_Platform" => "qq-official",
                "get_InstanceId" => "qq-main",
                "GetHostStatus" => null,
                "IsAdmin" => Admin && args[0] is UserReference { InstanceId: "qq-main" },
                "GetAdapterExtension" => method.GetGenericArguments()[0] == typeof(IQOfficialMaterialApi) ? (object?)Materials
                    : method.GetGenericArguments()[0] == typeof(IQSystemApi) ? (object?)Users
                    : method.GetGenericArguments()[0] == typeof(IQOfficialMessageApi) ? Official
                    : method.GetGenericArguments()[0] == typeof(IQGroupApi) ? Groups : null,
                _ => throw new NotSupportedException(method.Name)
            });
        }

        public Task DispatchAsync(string command, bool direct = false, string platform = "qq-official") =>
            ((IBotEventSubscriber)Plugin).OnEventAsync(new MessageEvent
            {
                Platform = platform, InstanceId = "qq-main", MessageId = "m", Channel = direct ? Channel.Direct("u") : Channel.Group("g"),
                Sender = new User("u"), Segments = [new TextSegment(command)]
            });
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, DemoProxy>();
        ((DemoProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class DemoProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
}
