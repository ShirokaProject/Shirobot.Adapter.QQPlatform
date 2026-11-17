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
    private static CardSegment Card => new()
    {
        Title = "标题", Description = "描述", Url = "https://example.org/page", ImageUrl = "https://example.org/image.png"
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CardsUseTypeEightWithoutTextOrMarkdown(bool group)
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        using var messages = new QQMessageService(handler.CreateApi(http));
        await messages.SendMessageAsync(group ? Channel.Group("target") : Channel.Direct("target"),
            new OutgoingMessage { Segments = [Card] });
        var request = handler.Requests.Last();
        Assert.Equal(group ? "/v2/groups/target/messages" : "/v2/users/target/messages", request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(8, body.RootElement.GetProperty("msg_type").GetInt32());
        var card = body.RootElement.GetProperty("card");
        Assert.Equal("tuwen", card.GetProperty("type").GetString());
        Assert.Equal(Card.Title, card.GetProperty("content").GetProperty("title").GetString());
        Assert.Equal(Card.ImageUrl, card.GetProperty("content").GetProperty("pic_url").GetString());
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
        var outgoing = new OutgoingMessage { Segments = [Card] };
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
            "picture" => Card with { ImageUrl = "qqbot-media://image/v1/token" },
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
    public async Task CanceledCardsAndCardsWithFieldsDoNotSend()
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        using var messages = new QQMessageService(handler.CreateApi(http));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => messages.SendMessageAsync(Channel.Group("g"),
            new OutgoingMessage { Segments = [Card] }, new CancellationToken(true)));
        Assert.Throws<NotSupportedException>(() => QQCardMapper.Map(Card with { Fields = [new CardField("k", "v")] }));
        Assert.Empty(handler.Requests);
    }
}
