using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.AdapterImpl;
using ShiroBot.Adapter.QQPlatform.Protocol;
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

public sealed class CardTests
{
    private static QOfficialCard Card => new("标题", "描述", "https://example.org/page", "https://example.org/image.png");

    [Theory]
    [InlineData(QOfficialMessageScene.Group)]
    [InlineData(QOfficialMessageScene.Direct)]
    public async Task TypedCardsUseTypeEightAndPreserveReply(QOfficialMessageScene scene)
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        var api = handler.CreateApi(http);
        using var messages = new QQMessageService(api);
        var service = new QQOfficialMessageService(api, messages);
        await service.SendAsync(new(scene, "target"), QOfficialMessage.Card(Card),
            new() { EventId = "event", MessageSequence = 2 });
        var request = handler.Requests.Last();
        Assert.Equal(scene == QOfficialMessageScene.Group ? "/v2/groups/target/messages" : "/v2/users/target/messages", request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(8, body.RootElement.GetProperty("msg_type").GetInt32());
        Assert.Equal("event", body.RootElement.GetProperty("event_id").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("msg_seq").GetInt32());
        var card = body.RootElement.GetProperty("card");
        Assert.Equal("tuwen", card.GetProperty("type").GetString());
        Assert.Equal(Card.Title, card.GetProperty("content").GetProperty("title").GetString());
        Assert.Equal(Card.PictureUrl, card.GetProperty("content").GetProperty("pic_url").GetString());
        Assert.False(body.RootElement.TryGetProperty("content", out _));
        Assert.False(body.RootElement.TryGetProperty("markdown", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GenericCardsAreNativeInGroupsAndDirectMessages(bool group)
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        using var messages = new QQMessageService(handler.CreateApi(http));
        var channel = group ? Channel.Group("g") : Channel.Direct("u");
        var outgoing = new OutgoingMessage { Segments = [new CardSegment
        {
            Title = Card.Title, Description = Card.Description, Url = Card.Url, ImageUrl = Card.PictureUrl
        }] };
        Assert.True(messages.AssessMessage(channel, outgoing).IsNative);
        var sent = await messages.SendMessageAsync(channel, outgoing);
        Assert.Empty(sent.Transformations);
        using var body = JsonDocument.Parse(handler.Requests.Last().Body);
        Assert.Equal(8, body.RootElement.GetProperty("msg_type").GetInt32());
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("url")]
    [InlineData("picture")]
    [InlineData("long-url")]
    public void InvalidCardsAreRejected(string failure)
    {
        var card = failure switch
        {
            "title" => Card with { Title = new string('a', 51) },
            "description" => Card with { Description = "" },
            "url" => Card with { Url = "http://example.org" },
            "picture" => Card with { PictureUrl = "qqbot-media://image/v1/token" },
            _ => Card with { Url = "https://example.org/" + new string('a', 1024) }
        };
        Assert.Throws<ArgumentException>(() => QQCardMapper.Map(card));
    }

    [Fact]
    public void MaximumUnicodeTitleAndDescriptionAreAccepted()
    {
        var mapped = QQCardMapper.Map(Card with { Title = string.Concat(Enumerable.Repeat("😀", 50)), Description = new string('a', 200) });
        Assert.Equal("tuwen", mapped.Type);
    }

    [Fact]
    public async Task UnsupportedSceneAndCanceledCardsDoNotSend()
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        var api = handler.CreateApi(http);
        using var messages = new QQMessageService(api);
        var service = new QQOfficialMessageService(api, messages);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SendAsync(new(QOfficialMessageScene.Channel, "c"), QOfficialMessage.Card(Card)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendAsync(new(QOfficialMessageScene.Group, "g"), QOfficialMessage.Card(Card), cancellationToken: new(true)));
        Assert.Empty(handler.Requests);
    }
}
