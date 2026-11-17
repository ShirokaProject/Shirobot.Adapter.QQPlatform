using System.Net;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform;
using ShiroBot.Adapter.QQPlatform.AdapterImpl;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;
using Xunit;

using ShiroBot.Adapter.QQPlatform.Transport;
using ShiroBot.Adapter.QQPlatform.Transport.Gateway;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Official;
using ShiroBot.Adapter.QQPlatform.AdapterImpl.Groups;
using ShiroBot.Adapter.QQPlatform.Protocol.Support;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
namespace ShiroBot.QQPlatform.Tests;

public sealed class OfficialGroupTests
{
    [Theory]
    [InlineData("GROUP_MEMBER_ADD", true)]
    [InlineData("GROUP_MEMBER_REMOVE", false)]
    public void MemberEventsKeepMemberAndUserOpenIdsDistinct(string type, bool joined)
    {
        using var json = JsonDocument.Parse("""{"group_openid":"group-a","member_openid":"member-a","user_openid":"user-a","timestamp":1784276757}""");
        var result = QQEventTranslator.Translate(new GatewayPayload(0, json.RootElement, 1, type, "evt"), "123");
        if (joined)
        {
            var entry = Assert.IsType<MemberJoinedEvent>(result);
            Assert.Equal("member-a", entry.UserId);
            Assert.Equal(Channel.Group("group-a"), entry.Channel);
            Assert.Null(entry.OperatorId);
        }
        else Assert.Equal("member-a", Assert.IsType<MemberLeftEvent>(result).UserId);
        var raw = Assert.IsAssignableFrom<QEventPayload>(result!.Raw);
        Assert.Equal("user-a", joined ? ((QGroupMemberIncrease)raw).GlobalUserId : ((QGroupMemberDecrease)raw).GlobalUserId);
        Assert.Equal("evt", raw.EventId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784276757), raw.Time);
    }

    private const string JoinJson = """{"group_openid":"group-a","member_openid":"member-a","join_request_id":"request-1","username":"Alice","risk_tips":"tip","apply_at":"2026-08-05T16:21:40+08:00","apply_source":"invited","invited_by":"inviter","verify_info":{"method":"admin_review_qa","review_qa_list":[{"question":"why","answer":"hello"}]},"auto_approved":{"strategy_id":"strategy-a"}}""";

    [Fact]
    public void JoinRequestKeepsVerificationAndAutomaticApproval()
    {
        using var json = JsonDocument.Parse(JoinJson);
        var result = Assert.IsType<PlatformEvent>(QQEventTranslator.Translate(new GatewayPayload(0, json.RootElement, 1, "GROUP_JOIN_REQUEST", "evt"), "bot-openid"));
        Assert.Equal(QEventKinds.GroupJoinRequest, result.Kind);
        Assert.Equal(Channel.Group("group-a"), result.Channel);
        var request = Assert.IsType<QGroupJoinRequest>(result.Raw);
        Assert.Equal("request-1", request.RequestId);
        Assert.Equal("bot-openid", request.SelfId);
        Assert.Equal("member-a", request.UserId);
        Assert.True(request.IsInvited);
        Assert.Equal("evt", request.EventId);
        Assert.Equal("strategy-a", request.AutoApprovedStrategyId);
        Assert.Equal("inviter", request.InviterId);
        Assert.Equal("hello", Assert.Single(request.Verification!.Answers).Answer);
    }

    [Theory]
    [InlineData("GROUP_MEMBER_ADD")]
    [InlineData("GROUP_MEMBER_REMOVE")]
    [InlineData("GROUP_JOIN_REQUEST")]
    public void InvalidMemberEventsAreIgnored(string type)
    {
        using var json = JsonDocument.Parse("""{"group_openid":"group-a"}""");
        Assert.Null(QQEventTranslator.Translate(new GatewayPayload(0, json.RootElement, 1, type, "evt"), null));
    }

    [Fact]
    public void MemberSubscriptionIsExplicitAndPreservesCustomIntents()
    {
        var config = new QQPlatformConfig { Intents = 1UL << 26 };
        Assert.Equal(1UL << 26, config.EffectiveIntents);
        config.SubscribeGroupMemberEvents = true;
        Assert.Equal((1UL << 26) | (1UL << 24), config.EffectiveIntents);
    }

    [Fact]
    public async Task GroupKeyboardAndMarkdownSerializeNewFields()
    {
        using var fixture = new Fixture();
        var official = new QQOfficialMessageService(fixture.Api, new QQMessageService(fixture.Api));
        var keyboard = new QInlineKeyboard([new QKeyboardRow([new QKeyboardButton
        {
            Id = "delete", GroupId = "decision",
            RenderData = new QKeyboardRenderData("删除", "已删除", QKeyboardButtonStyle.Red),
            Action = new QKeyboardAction
            {
                Type = QKeyboardActionType.Callback,
                Permission = new QKeyboardPermission { Type = QKeyboardPermissionType.Managers },
                Data = "delete:1", UnsupportTips = "请升级", Modal = new QKeyboardModal("确定删除？", "删除", "取消")
            }
        }])]);
        await official.SendMarkdownAsync(new QOfficialMessageTarget(QOfficialMessageScene.Group, "group-a"),
            new QCustomMarkdown("hello") { ForceVerifyImageResource = true }, keyboard);
        var request = fixture.LastBody;
        Assert.True(request.GetProperty("markdown").GetProperty("force_verify_image_resource").GetBoolean());
        var button = request.GetProperty("keyboard").GetProperty("content").GetProperty("rows")[0].GetProperty("buttons")[0];
        Assert.Equal("decision", button.GetProperty("group_id").GetString());
        Assert.Equal(3, button.GetProperty("render_data").GetProperty("style").GetInt32());
        Assert.Equal("确定删除？", button.GetProperty("action").GetProperty("modal").GetProperty("content").GetString());
        Assert.Equal(4, (int)QKeyboardButtonStyle.BlueFilled);
    }

    [Theory]
    [InlineData("https://example.org", "确定")]
    [InlineData("确认", "超过四个文字")]
    public void InvalidModalIsRejected(string content, string confirm)
    {
        var button = new QKeyboardButton
        {
            RenderData = new QKeyboardRenderData("test", "test", QKeyboardButtonStyle.Blue),
            Action = new QKeyboardAction
            {
                Type = QKeyboardActionType.Callback, Data = "test", UnsupportTips = "upgrade",
                Permission = new QKeyboardPermission { Type = QKeyboardPermissionType.Everyone },
                Modal = new QKeyboardModal(content, confirm)
            }
        };
        Assert.Throws<ArgumentException>(() => QQOfficialMessageMapper.MapKeyboard(new QInlineKeyboard([new QKeyboardRow([button])])));
    }

    [Fact]
    public async Task JoinQueriesAndDecisionsUseEscapedOpenIdsAndCorrectBodies()
    {
        using var fixture = new Fixture { Response = "{\"list\":[" + JoinJson + "],\"next_cursor\":\"next\"}" };
        var page = await fixture.Groups.GetJoinRequestsAsync("group/a", "a+b", 50);
        Assert.Equal("next", page.NextCursor);
        Assert.Equal("group/a", Assert.Single(page.Requests).GroupId);
        Assert.EndsWith("/group%2Fa/join_request_list?limit=50&cursor=a%2Bb", fixture.Requests[^1].Uri);
        fixture.Response = "{}";
        await fixture.Groups.AcceptJoinRequestAsync(new QGroupJoinRequest { GroupId = "group/a", UserId = "member/a", RequestId = "request-1" });
        Assert.Equal("POST", fixture.Requests[^1].Method);
        Assert.EndsWith("/approval_join_request/member%2Fa", fixture.Requests[^1].Uri);
        Assert.Equal("approve", fixture.LastBody.GetProperty("op").GetString());
        Assert.False(fixture.LastBody.TryGetProperty("add_to_member_blacklist", out _));
        await fixture.Groups.RejectJoinRequestAsync(new QGroupJoinRequest { GroupId = "group/a", UserId = "member/a", RequestId = "request-1" }, "reason", true);
        Assert.Equal("decline", fixture.LastBody.GetProperty("op").GetString());
        Assert.True(fixture.LastBody.GetProperty("add_to_member_blacklist").GetBoolean());
        Assert.Equal("reason", fixture.LastBody.GetProperty("reject_reason").GetString());
    }

    [Fact]
    public async Task MuteQueryMapsRulesAndBatchPreservesOperations()
    {
        using var fixture = new Fixture { Response = """{"global_rule":{"mode":"schedule","schedule_rules":[{"task_id":"t1","start_at":"2026-08-01T12:00:00+08:00","end_at":"2026-08-01T13:00:00+08:00","enabled":true}],"recurring_rules":[{"task_id":"t2","weekdays":[1,2],"start_time":"22:00","end_time":"07:00","enabled":false}]},"members":[{"member_openid":"m1","mute_expire_at":"2026-08-01T12:00:00+08:00","username":"Alice"}]}""" };
        var state = await fixture.Groups.GetMuteStateAsync("group-a");
        Assert.Equal("schedule", state.Mode);
        Assert.True(Assert.Single(state.Schedules).Enabled);
        Assert.Equal(new[] { 1, 2 }, Assert.Single(state.RecurringRules).Weekdays);
        Assert.Equal("Alice", Assert.Single(state.Members).Username);
        fixture.Response = "{}";
        var result = await fixture.Groups.SetMemberMutesAsync("group-a", [new() { UserId = "m1", Duration = TimeSpan.Zero }, new() { UserId = "m2", Duration = TimeSpan.FromMinutes(2) }, new() { UserId = "m3", Duration = TimeSpan.FromMinutes(3) }]);
        Assert.True(result.IsSuccess);
        var batches = fixture.Requests.Where(x => x.Method == "POST").Select(x => JsonDocument.Parse(x.Body).RootElement.Clone().GetProperty("members")[0]).ToArray();
        Assert.Equal("del", batches[0].GetProperty("op").GetString());
        Assert.Equal("add", batches[1].GetProperty("op").GetString());
        Assert.Equal("add", batches[2].GetProperty("op").GetString());
        Assert.True(DateTimeOffset.TryParse(batches[2].GetProperty("mute_expire_at").GetString(), out _));
        var count = fixture.Requests.Count;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Groups.SetMemberMutesAsync("g", [new() { UserId = "m", Duration = TimeSpan.FromDays(31) }]));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Groups.SetMemberMutesAsync("g", [new() { UserId = "m", Duration = TimeSpan.Zero }, new() { UserId = "m", Duration = TimeSpan.Zero }]));
        Assert.Equal(count, fixture.Requests.Count);
    }

    [Fact]
    public async Task StrategyLifecycleAndWhitelistFollowOfficialContract()
    {
        using var fixture = new Fixture { Response = """{"strategy_id":"s1","is_enable":"on","expire_at":"2027-01-01T00:00:00+08:00"}""" };
        var created = await fixture.Strategies.CreateApprovalStrategyAsync(new() { GroupNumbers = [ulong.MaxValue.ToString()], Remark = "test" });
        Assert.Equal("s1", created.StrategyId);
        Assert.True(created.Enabled);
        Assert.Equal(ulong.MaxValue, fixture.LastBody.GetProperty("group_ids")[0].GetUInt64());
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Strategies.CreateApprovalStrategyAsync(new() { GroupNumbers = ["1"], GroupIds = ["g"] }));
        fixture.Response = """{"strategies":[{"strategy_id":"s1","is_enable":"on","group_ids":["10****499"],"whitelist_user_count":2}],"next_cursor":"c2"}""";
        var page = await fixture.Strategies.GetApprovalStrategiesAsync("c1", 50);
        Assert.Equal("10****499", Assert.Single(page.Strategies).GroupNumbers[0]);
        Assert.Equal("c2", page.NextCursor);
        fixture.Response = """{"is_enable":"off"}""";
        var updated = await fixture.Strategies.UpdateApprovalStrategyAsync("s/1", new() { Enabled = false, Groups = new() { Add = false, GroupIds = ["g"] } });
        Assert.Equal("s/1", updated.StrategyId);
        Assert.False(updated.Enabled);
        Assert.Equal("PATCH", fixture.Requests[^1].Method);
        Assert.Equal("del", fixture.LastBody.GetProperty("group_action").GetProperty("op").GetString());
        fixture.Response = """{"strategy_id":"s1","whitelist_user_count":3}""";
        Assert.Equal(3, await fixture.Strategies.UpdateApprovalWhitelistAsync("s1", ["1234567"], false));
        Assert.Equal("del", fixture.LastBody.GetProperty("op").GetString());
        fixture.Response = "{}";
        await fixture.Strategies.ExecuteApprovalStrategyAsync("s1");
        Assert.EndsWith("/s1/execute", fixture.Requests[^1].Uri);
        await fixture.Strategies.DeleteApprovalStrategyAsync("s1");
        Assert.Equal("DELETE", fixture.Requests[^1].Method);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancelledTypedMessagesNeverReachHttp(int type)
    {
        using var fixture = new Fixture();
        var official = new QQOfficialMessageService(fixture.Api, new QQMessageService(fixture.Api));
        var message = type switch
        {
            0 => QOfficialMessage.Text("hello"),
            1 => QOfficialMessage.Markdown(new QCustomMarkdown("hello")),
            _ => QOfficialMessage.Media(new QOfficialMedia("file", QOfficialMediaType.Image, "a.png"))
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => official.SendAsync(new(QOfficialMessageScene.Group, "g"), message, cancellationToken: new CancellationToken(true)));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task CancellationDuringTextSendReachesTransport()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        fixture.OnRequest = async (_, token) => { cancel.Cancel(); await Task.Delay(1000, token); };
        var official = new QQOfficialMessageService(fixture.Api, new QQMessageService(fixture.Api));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => official.SendAsync(new(QOfficialMessageScene.Group, "g"), QOfficialMessage.Text("hello"), cancellationToken: cancel.Token));
    }

    [Fact]
    public async Task GenericImageAndCaptionSendAsOneMessage()
    {
        using var fixture = new Fixture();
        var service = new QQMessageService(fixture.Api);
        await service.SendMessageAsync(Channel.Group("g"), [new ImageSegment("https://example.org/a.png"), new TextSegment("caption")]);
        Assert.Equal(7, fixture.LastBody.GetProperty("msg_type").GetInt32());
        Assert.Equal("caption", fixture.LastBody.GetProperty("content").GetString());
        Assert.Equal("file", fixture.LastBody.GetProperty("media").GetProperty("file_info").GetString());
        Assert.Single(fixture.Requests, x => x.Uri.EndsWith("/messages"));
    }

    [Fact]
    public async Task CommonGroupApiMapsGroupAndMemberQueries()
    {
        using var fixture = new Fixture { Response = """{"group_openid":"group/a","group_name":"test","member_count":2}""" };
        Assert.True(fixture.Groups.Capabilities.HasFlag(QGroupCapabilities.GroupInfo));
        Assert.False(fixture.Groups.Capabilities.HasFlag(QGroupCapabilities.GroupList));
        var group = await fixture.Groups.GetGroupInfoAsync("group/a");
        Assert.Equal("group/a", group.GroupId);
        Assert.Equal("test", group.GroupName);
        fixture.Response = """{"member_openid":"member-a","username":"Alice","member_role":"admin"}""";
        var member = await fixture.Groups.GetGroupMemberInfoAsync("group/a", "member-a");
        Assert.Equal("member-a", member.UserId);
        Assert.Equal(QGroupRole.Admin, member.Role);
        fixture.Response = """{"members":[{"member_openid":"member-a","username":"Alice"}],"next_cursor":"next"}""";
        fixture.OnRequest = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("next"))
                fixture.Response = """{"members":[{"member_openid":"member-b","username":"Bob"}]}""";
            return Task.CompletedTask;
        };
        var members = await fixture.Groups.GetGroupMemberListAsync("group/a");
        Assert.Equal(new[] { "member-a", "member-b" }, members.Select(x => x.UserId));
    }

    [Fact]
    public async Task BlacklistAndBotStateFollowOfficialContract()
    {
        using var fixture = new Fixture { Response = """{"users":[{"member_openid":"member/a","union_openid":"union-a","username":"Alice","banned_at":"2025-07-01T10:30:00+08:00","bot":false}],"next_cursor":""}""" };
        var page = await fixture.Groups.GetGroupBlacklistAsync("group/a", "a+b", 100);
        var entry = Assert.Single(page.Members);
        Assert.Equal("member/a", entry.UserId);
        Assert.Equal("union-a", entry.UnionId);
        Assert.Equal("Alice", entry.Username);
        Assert.Equal(DateTimeOffset.Parse("2025-07-01T10:30:00+08:00"), entry.BlacklistedAt);
        Assert.False(entry.IsBot);
        Assert.EndsWith("/group%2Fa/member_blacklist?limit=100&cursor=a%2Bb", fixture.Requests[^1].Uri);
        fixture.Response = """{"fail_openids":["member/b"]}""";
        var failed = await fixture.Groups.UpdateGroupBlacklistAsync("group/a", ["member/a", "member/b"], false);
        Assert.Equal(new[] { "member/b" }, failed);
        Assert.Equal("del", fixture.LastBody.GetProperty("op").GetString());
        Assert.EndsWith("/group%2Fa/member_blacklist", fixture.Requests[^1].Uri);
        fixture.Response = """{"member_openid":"bot-1","joined_at":"2025-06-15T14:30:00+08:00","allow_proactive_msg":false,"recv_msg_setting":"only_mention","member_role":"admin"}""";
        var state = await fixture.Groups.GetBotStateAsync("group/a");
        Assert.Equal("bot-1", state.Member.UserId);
        Assert.Equal("group/a", state.Member.GroupId);
        Assert.True(state.Member.IsBot);
        Assert.Equal(QGroupRole.Admin, state.Member.Role);
        Assert.Equal(DateTimeOffset.Parse("2025-06-15T14:30:00+08:00"), state.Member.JoinTime);
        Assert.Equal(QGroupReceiveSetting.MentionOnly, state.ReceiveSetting);
        Assert.False(state.AllowProactiveMessage);
        Assert.EndsWith("/group%2Fa/bot_state", fixture.Requests[^1].Uri);
        var count = fixture.Requests.Count;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Groups.GetGroupBlacklistAsync("g", limit: 101));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Groups.UpdateGroupBlacklistAsync("g", ["a", "a"]));
        Assert.Equal(count, fixture.Requests.Count);
    }

    [Fact]
    public async Task GroupInfoKeepsFingerMemoClassTagsAndMemberUnionBot()
    {
        using var fixture = new Fixture { Response = """{"group_openid":"g","group_name":"读书会","group_finger_memo":"每周共读","group_class_text":"文化","group_tags":["阅读","文学"],"group_member_num":256,"union_group_openid":"union-g"}""" };
        var group = await fixture.Groups.GetGroupInfoAsync("g");
        Assert.Equal("每周共读", group.Description);
        Assert.Equal("文化", group.ClassText);
        Assert.Equal(new[] { "阅读", "文学" }, group.Tags);
        Assert.Equal("union-g", group.UnionId);
        fixture.Response = """{"member_openid":"m1","username":"Alice","member_role":"owner","union_openid":"union-u","bot":true}""";
        var member = await fixture.Groups.GetGroupMemberInfoAsync("g", "m1");
        Assert.Equal("union-u", member.UnionId);
        Assert.True(member.IsBot);
        Assert.Equal(QGroupRole.Owner, member.Role);
    }

    [Fact]
    public async Task PermissionDeniedIsTranslatedForInviteOnlyGroupApis()
    {
        using var fixture = new Fixture
        {
            Response = """{"err_code":40012010,"message":"应用无接口访问权限"}"""
        };
        var error = await Assert.ThrowsAsync<QPermissionDeniedException>(
            () => fixture.Groups.GetGroupBlacklistAsync("group/a"));
        Assert.Equal("群黑名单查询", error.Operation);
        Assert.Equal(40012010, error.ErrorCode);

        var stateError = await Assert.ThrowsAsync<QPermissionDeniedException>(
            () => fixture.Groups.GetBotStateAsync("group/a"));
        Assert.Equal("机器人群内状态", stateError.Operation);
    }

    [Fact]
    public async Task EveryoneMentionPermissionErrorIsTranslated()
    {
        using var fixture = new Fixture
        {
            Response = """{"err_code":40034143,"message":"消息发送失败，@全体成员需要管理员权限"}"""
        };
        var error = await Assert.ThrowsAsync<QPermissionDeniedException>(() =>
            fixture.Api.SendMessageAsync(Channel.Group("g"),
                new QQSendRequest { MessageType = 2, Markdown = new QQMarkdown { Content = "hi" } }));
        Assert.Equal("发送消息", error.Operation);
        Assert.Equal(40034143, error.ErrorCode);
    }

    [Fact]
    public async Task EveryoneMentionDailyLimitIsTranslated()
    {
        using var fixture = new Fixture
        {
            Response = """{"err_code":40034142,"message":"消息发送失败，@全体成员次数超限"}"""
        };
        var error = await Assert.ThrowsAsync<QLimitExceededException>(() =>
            fixture.Api.SendMessageAsync(Channel.Group("g"),
                new QQSendRequest { MessageType = 2, Markdown = new QQMarkdown { Content = "hi" } }));
        Assert.Equal("发送消息", error.Operation);
        Assert.Equal(40034142, error.ErrorCode);
    }

    [Fact]
    public async Task RepeatedMemberCursorFailsInsteadOfLooping()
    {
        using var fixture = new Fixture { Response = """{"members":[],"next_cursor":"repeat"}""" };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Groups.GetGroupMemberListAsync("g"));
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task UnknownProfileFieldsAreNotInvented()
    {
        using var fixture = new Fixture { Response = """{"group_openid":"g"}""" };
        var group = await fixture.Groups.GetGroupInfoAsync("g");
        Assert.Null(group.GroupName);
        Assert.Null(group.MemberCount);
        Assert.Null(group.MaxMemberCount);
        fixture.Response = """{"member_openid":"u"}""";
        var member = await fixture.Groups.GetGroupMemberInfoAsync("g", "u");
        Assert.Null(member.Nickname);
        Assert.Equal(QGroupRole.Unknown, member.Role);
        Assert.Equal("u", member.DisplayName);
    }

    [Fact]
    public async Task PatchDistinguishesUnsetSetAndClear()
    {
        using var fixture = new Fixture();
        await fixture.Strategies.UpdateApprovalStrategyAsync("s", new() { Enabled = false });
        Assert.False(fixture.LastBody.TryGetProperty("remark", out _));
        await fixture.Strategies.UpdateApprovalStrategyAsync("s", new() { Remark = QPatch<string>.Clear() });
        Assert.Equal("", fixture.LastBody.GetProperty("remark").GetString());
        var expiry = DateTimeOffset.UtcNow.AddDays(1);
        await fixture.Strategies.UpdateApprovalStrategyAsync("s", new() { ExpiresAt = QPatch<DateTimeOffset>.Set(expiry) });
        Assert.Equal(expiry, DateTimeOffset.Parse(fixture.LastBody.GetProperty("expire_at").GetString()!));
        var count = fixture.Requests.Count;
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Strategies.UpdateApprovalStrategyAsync("s", new() { ExpiresAt = QPatch<DateTimeOffset>.Clear() }));
        Assert.Equal(count, fixture.Requests.Count);
    }

    [Fact]
    public async Task BatchRetainsSuccessAndUnknownOutcomesAndContinues()
    {
        using var fixture = new Fixture();
        fixture.OnRequest = (_, _) => fixture.Requests.Count == 2 ? throw new HttpRequestException("Disconnected") : Task.CompletedTask;
        var result = await fixture.Groups.SetMemberMutesAsync("g", [
            new() { UserId = "a", Duration = TimeSpan.Zero },
            new() { UserId = "b", Duration = TimeSpan.Zero },
            new() { UserId = "c", Duration = TimeSpan.Zero }]);
        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { QOperationStatus.Succeeded, QOperationStatus.Unknown, QOperationStatus.Succeeded }, result.Items.Select(x => x.Status));
    }

    [Fact]
    public async Task BatchCancellationIncludesPartialResults()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        fixture.OnRequest = (_, _) => { cancel.Cancel(); return Task.CompletedTask; };
        var error = await Assert.ThrowsAsync<QBatchOperationCanceledException>(() => fixture.Groups.SetMemberMutesAsync("g", [
            new() { UserId = "a", Duration = TimeSpan.Zero }, new() { UserId = "b", Duration = TimeSpan.Zero }], cancel.Token));
        Assert.Equal(QOperationStatus.NotExecuted, error.PartialResult.Items[1].Status);
        Assert.Single(fixture.Requests);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly HttpClient _http;
        internal QQOpenApiClient Api { get; }
        internal IQGroupApi Groups { get; }
        internal IQGroupApprovalStrategyApi Strategies { get; }
        internal string Response { get; set; } = """{"id":"sent","file_info":"file"}""";
        internal Func<HttpRequestMessage, CancellationToken, Task>? OnRequest { get; set; }
        internal List<(string Method, string Uri, string Body)> Requests { get; } = [];
        internal JsonElement LastBody => JsonDocument.Parse(Requests[^1].Body).RootElement.Clone();
        internal Fixture()
        {
            _http = new HttpClient(this, disposeHandler: false);
            var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
            Api = new QQOpenApiClient(_http, config, new QQTokenProvider(_http, config));
            var groups = new QQGroupService(Api);
            var strategies = new QQGroupApprovalService(Api);
            Groups = groups; Strategies = strategies;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/app/getAppAccessToken")
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"token","expires_in":7200}""") };
            Requests.Add((request.Method.Method, request.RequestUri.AbsoluteUri, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (OnRequest is not null) await OnRequest(request, cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(Response) };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _http.Dispose();
            base.Dispose(disposing);
        }
    }
}
