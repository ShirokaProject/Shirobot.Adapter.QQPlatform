using System.Net;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform;
using ShiroBot.Adapter.QQPlatform.AdapterImpl;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using Xunit;

namespace ShiroBot.QQPlatform.Tests;

public sealed class GenericMessageTests
{
    [Fact]
    public async Task MarkdownAndBasicButtonsMapWithoutQqContractsAtCallSite()
    {
        using var fixture = new Fixture();
        IMessageService service = fixture.Messages;
        var request = new OutgoingMessage
        {
            Segments = [new MarkdownSegment("**hello**")],
            Buttons = new([new([new("open", "打开", new OpenUrlAction("https://example.org")), new("next", "下一页", new CallbackAction("next:1"))])])
        };
        Assert.True(service.AssessMessage(Channel.Group("g"), request).IsNative);
        var sent = await service.SendMessageAsync(Channel.Group("g"), request);
        Assert.Empty(sent.Transformations);
        Assert.Equal(2, fixture.LastBody.GetProperty("msg_type").GetInt32());
        var buttons = fixture.LastBody.GetProperty("keyboard").GetProperty("content").GetProperty("rows")[0].GetProperty("buttons");
        Assert.Equal("next:1", buttons[1].GetProperty("action").GetProperty("data").GetString());
        Assert.Equal("**hello**", fixture.LastBody.GetProperty("markdown").GetProperty("content").GetString());
    }

    [Fact]
    public async Task PlainTextButtonsPreserveLiteralTextAndCardFallbackIsExplicit()
    {
        using var fixture = new Fixture();
        IMessageService service = fixture.Messages;
        var text = new OutgoingMessage { Segments = [new TextSegment("*literal*")], Buttons = new([new([new("a", "打开", new OpenUrlAction("https://example.org"))])]) };
        var sent = await service.SendMessageAsync(Channel.Direct("u"), text);
        Assert.False(Assert.Single(sent.Transformations).IsDowngrade);
        Assert.Equal("\\*literal\\*", fixture.LastBody.GetProperty("markdown").GetProperty("content").GetString());
        var card = new OutgoingMessage { Segments = [new CardSegment { Title = "title", Description = "desc", Fields = [new("key", "value")] }] };
        Assert.False(service.AssessMessage(Channel.Group("g"), card).IsSupported);
        var converted = await service.SendMessageAsync(Channel.Group("g"), card with { AllowedFallbacks = MessageFallbackOptions.CardAsMarkdown });
        Assert.Equal(MessageTransformationKind.CardToMarkdown, Assert.Single(converted.Transformations).Kind);
        Assert.Contains("key: value", fixture.LastBody.GetProperty("markdown").GetProperty("content").GetString());
    }

    [Fact]
    public async Task UnsupportedCombinationsAndImplicitSplittingAreRejectedBeforeSending()
    {
        using var fixture = new Fixture();
        IMessageService service = fixture.Messages;
        var mixed = new OutgoingMessage { Segments = [new MarkdownSegment("hello"), new ImageSegment("https://example.org/a.png")] };
        Assert.False(service.AssessMessage(Channel.Group("g"), mixed).IsSupported);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SendMessageAsync(Channel.Group("g"), mixed));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SendMessageAsync(Channel.Group("g"), new OutgoingMessage { Segments = [new TextSegment(new string('a', 5001))] }));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task InteractionReplyUsesCachedPlatformCredential()
    {
        using var fixture = new Fixture();
        using var json = JsonDocument.Parse("""{"id":"click","type":11,"chat_type":1,"group_openid":"g","group_member_openid":"u","data":{"resolved":{"button_id":"b","button_data":"next"}}}""");
        var interaction = Assert.IsType<InteractionEvent>(QQEventTranslator.Translate(new GatewayPayload(0, json.RootElement, 1, "INTERACTION_CREATE", "event-id"), "self")) with { InstanceId = "qq-main" };
        Assert.Equal("next", interaction.Data);
        Assert.Equal("u", interaction.User.Id);
        fixture.Messages.RememberInteraction(interaction);
        await ((IMessageService)fixture.Messages).SendMessageAsync(Channel.Group("g"), new OutgoingMessage { Segments = [new TextSegment("done")], ReplyToInteraction = interaction.Reference });
        Assert.Equal("event-id", fixture.LastBody.GetProperty("event_id").GetString());
        Assert.IsType<QOfficialButtonInteraction>(interaction.Raw);
    }

    [Fact]
    public async Task ConcurrentAcknowledgementsWaitForOneRequestAndShareFailure()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnRequest = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var first = fixture.Api.AcknowledgeInteractionAsync("click");
        await entered.Task;
        var second = fixture.Api.AcknowledgeInteractionAsync("click");
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Single(fixture.Requests);
        fixture.OnRequest = (_, _) => throw new HttpRequestException("lost connection");
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Api.AcknowledgeInteractionAsync("uncertain"));
        var count = fixture.Requests.Count;
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Api.AcknowledgeInteractionAsync("uncertain"));
        Assert.Equal(count, fixture.Requests.Count);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly HttpClient _http;
        internal QQOpenApiClient Api { get; }
        internal QQMessageService Messages { get; }
        internal List<string> Requests { get; } = [];
        internal Func<HttpRequestMessage, CancellationToken, Task>? OnRequest;
        internal JsonElement LastBody => JsonDocument.Parse(Requests[^1]).RootElement.Clone();
        internal Fixture()
        {
            _http = new(this, false);
            var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
            Api = new(_http, config, new QQTokenProvider(_http, config));
            Messages = new(Api);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/app/getAppAccessToken")
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"token","expires_in":7200}""") };
            Requests.Add(request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(token));
            if (OnRequest is not null) await OnRequest(request, token);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"id":"sent"}""") };
        }
        protected override void Dispose(bool disposing) { if (disposing) _http.Dispose(); base.Dispose(disposing); }
    }
}
