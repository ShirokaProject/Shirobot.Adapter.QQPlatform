using System.Text.Json;
using ShiroBot.Adapter.QQPlatform;
using ShiroBot.Adapter.QQPlatform.AdapterImpl;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
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

public sealed class UserInfoTests
{
    [Fact]
    public async Task GenericAndOfficialUserLookupsUseRequestedOpenId()
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        var users = new QQUserService();
        users.Attach(handler.CreateApi(http));
        IUserService generic = users;
        var user = await generic.GetUserAsync("user/1");
        Assert.Equal("user/1", user!.Id);
        Assert.Equal("用户昵称", user.Name);
        Assert.Equal("https://example.org/avatar.png", user.AvatarUrl);
        var info = await ((IQOfficialUserApi)users).GetUserInfoAsync("user/1");
        Assert.Equal("union-user", info.UnionOpenId);
        Assert.True(info.AllowProactiveMessage);
        Assert.Contains(handler.Requests, x => x.Method == "GET" && x.Path == "/v2/users/user%2F1/info" && x.Authorization == "QQBot token");
    }

    [Fact]
    public async Task InvalidOrCanceledUserLookupDoesNotSend()
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        var users = new QQUserService();
        users.Attach(handler.CreateApi(http));
        await Assert.ThrowsAsync<ArgumentException>(() => users.GetUserInfoAsync(" "));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => users.GetUserInfoAsync("u", new CancellationToken(true)));
        Assert.Empty(handler.Requests);
        users.Attach(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => users.GetUserInfoAsync("u"));
    }

    [Fact]
    public async Task NoFriendRelationshipRemainsAnActionablePlatformError()
    {
        using var handler = new ChangelogHandler { UserInfoError = true };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<QQApiException>(() => handler.CreateApi(http).GetUserInfoAsync("u"));
        Assert.Equal(40184001, error.ErrorCode);
    }

    [Theory]
    [InlineData("C2C_MESSAGE_CREATE", false)]
    [InlineData("GROUP_MESSAGE_CREATE", true)]
    [InlineData("GROUP_AT_MESSAGE_CREATE", true)]
    public void IncomingMessagesKeepUserNameAvatarAndSeparateGroupNickname(string eventType, bool group)
    {
        using var json = JsonDocument.Parse("""
            {"id":"m","group_openid":"g","union_group_openid":"union-group","content":"hi <qqbot-at-user id=\"other\" />",
             "author":{"user_openid":"u","member_openid":"member","username":"用户昵称","member_nickname":"群名片","avatar":"https://example.org/avatar.png"},
             "mentions":[{"member_openid":"other","username":"昵称","member_nickname":"对方群名片"}]}
            """);
        var message = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(new GatewayPayload(0, json.RootElement, 1, eventType, null), "self"));
        Assert.Equal(group ? "member" : "u", message.Sender.Id);
        Assert.Equal("用户昵称", message.Sender.Name);
        Assert.Equal("https://example.org/avatar.png", message.Sender.AvatarUrl);
        if (group)
        {
            Assert.Equal("群名片", message.Member!.Nick);
            Assert.Equal("对方群名片", message.Segments.OfType<MentionSegment>().Single(x => x.UserId == "other").DisplayName);
        }
        else Assert.Null(message.Member);
        Assert.Equal("union-group", Assert.IsType<JsonElement>(message.Raw).GetProperty("union_group_openid").GetString());
    }

    [Fact]
    public void OlderPayloadsStillWorkWithoutNewFields()
    {
        using var json = JsonDocument.Parse("""{"id":"m","group_openid":"g","content":"hi","author":{"member_openid":"member","username":"old"}}""");
        var message = Assert.IsType<MessageEvent>(QQEventTranslator.Translate(new GatewayPayload(0, json.RootElement, 1, "GROUP_MESSAGE_CREATE", null), "self"));
        Assert.Equal("old", message.Member!.Nick);
        Assert.Null(message.Sender.AvatarUrl);
    }

    [Fact]
    public async Task GroupQueryKeepsUnionIdWithoutChangingRoutingId()
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        var group = await new QQGroupService(handler.CreateApi(http)).GetGroupInfoAsync("g");
        Assert.Equal("g", group.GroupId);
        Assert.Equal("union-group", group.UnionGroupOpenId);
    }

    [Theory]
    [InlineData("GROUP_ADD_ROBOT")]
    [InlineData("GROUP_DEL_ROBOT")]
    [InlineData("GROUP_MSG_RECEIVE")]
    [InlineData("GROUP_MSG_REJECT")]
    [InlineData("GROUP_MEMBER_ADD")]
    [InlineData("GROUP_MEMBER_REMOVE")]
    [InlineData("GROUP_JOIN_REQUEST")]
    public void TypedGroupEventsKeepUnionId(string eventType)
    {
        using var json = JsonDocument.Parse("""{"group_openid":"g","union_group_openid":"union-group","member_openid":"member","join_request_id":"request","op_member_openid":"operator"}""");
        var result = QQEventTranslator.Translate(new GatewayPayload(0, json.RootElement, 1, eventType, "event"), "self");
        Assert.NotNull(result);
        var unionId = result.Raw switch
        {
            QOfficialLifecycleEvent lifecycle => lifecycle.UnionGroupOpenId,
            QGroupMemberIncrease increase => increase.UnionGroupOpenId,
            QGroupMemberDecrease decrease => decrease.UnionGroupOpenId,
            QGroupJoinRequest request => request.UnionGroupOpenId,
            _ => throw new InvalidOperationException("Unexpected event payload.")
        };
        Assert.Equal("union-group", unionId);
    }
}
