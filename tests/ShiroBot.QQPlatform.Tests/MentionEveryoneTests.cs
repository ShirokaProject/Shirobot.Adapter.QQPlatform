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

public sealed class MentionEveryoneTests
{
    [Theory]
    [InlineData("a<qqbot-at-everyone />b")]
    [InlineData("a<qqbot-at-everyone/>b")]
    [InlineData("a<@all>b")]
    public void ParsesEveryoneWithoutLosingSurroundingText(string content)
    {
        var segments = QQContentParser.Parse(content);
        Assert.Equal("a", Assert.IsType<TextSegment>(segments[0]).Text);
        Assert.IsType<MentionAllSegment>(segments[1]);
        Assert.Equal("b", Assert.IsType<TextSegment>(segments[2]).Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GenericEveryoneIsNativeOnlyInGroups(bool group)
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        using var messages = new QQMessageService(handler.CreateApi(http));
        var channel = group ? Channel.Group("g") : Channel.Direct("u");
        Assert.Equal(group, messages.GetMessageCapabilities(channel).NativeFeatures.HasFlag(MessageFeatures.MentionAll));
        await messages.SendMessageAsync(channel, new MessageSegment[] { new MentionAllSegment(), new TextSegment("通知") });
        using var body = JsonDocument.Parse(handler.Requests.Last().Body);
        var text = group ? body.RootElement.GetProperty("markdown").GetProperty("content").GetString()
            : body.RootElement.GetProperty("content").GetString();
        Assert.Equal(group ? "<qqbot-at-everyone /> 通知" : "@全体成员 通知", text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfficialTextAndMarkdownPreserveEveryoneTag(bool markdown)
    {
        using var handler = new ChangelogHandler();
        using var http = new HttpClient(handler);
        var api = handler.CreateApi(http);
        using var messages = new QQMessageService(api);
        var service = new QQOfficialMessageService(api, messages);
        var target = new QOfficialMessageTarget(QOfficialMessageScene.Group, "g");
        const string text = "<qqbot-at-everyone /> 通知";
        await service.SendAsync(target, markdown ? QOfficialMessage.Markdown(new QCustomMarkdown(text)) : QOfficialMessage.Text(text));
        using var body = JsonDocument.Parse(handler.Requests.Last().Body);
        Assert.Equal(markdown ? 2 : 0, body.RootElement.GetProperty("msg_type").GetInt32());
        Assert.Equal(text, markdown ? body.RootElement.GetProperty("markdown").GetProperty("content").GetString()
            : body.RootElement.GetProperty("content").GetString());
    }
}
