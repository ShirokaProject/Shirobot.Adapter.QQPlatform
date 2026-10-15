using System.Net;
using System.Reflection;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform;
using ShiroBot.Adapter.QQPlatform.AdapterImpl;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.SDK.Models;
using Xunit;

namespace ShiroBot.QQPlatform.Tests;

public sealed class MemoryLifecycleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ExpiredKeyReinsertionDoesNotEvictNewValueUsingOldOrderRecord()
    {
        var clock = new Clock();
        var cache = new QQBoundedCache<string>(2, clock);
        cache.Set("a", "old", TimeSpan.FromSeconds(1));
        cache.Set("b", "b", TimeSpan.FromHours(1));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.False(cache.TryGet("a", out _));
        cache.Set("a", "new", TimeSpan.FromHours(1));
        cache.Set("c", "c", TimeSpan.FromHours(1));
        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal("new", value);
        Assert.False(cache.TryGet("b", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void CacheIsBoundedAndCleansExpiredKeysOnActivity()
    {
        var clock = new Clock();
        var cache = new QQBoundedCache<string?>(4, clock);
        for (var i = 0; i < 1000; i++) cache.Set(i.ToString(), null, TimeSpan.FromMinutes(1));
        Assert.Equal(4, cache.Count);
        Assert.True(cache.TryGet("999", out var name));
        Assert.Null(name);
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.False(cache.TryGet("999", out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ManyTemporaryAliasesAreBoundedAndKeepRealReferenceIndex()
    {
        using var tracker = new QQReplyTracker();
        var channel = Channel.Group("g");
        var original = new MessageEvent
        {
            MessageId = "original", Channel = channel, Sender = new User("u"),
            Timestamp = DateTimeOffset.UtcNow, Platform = "qq-official", Segments = [new TextSegment("unique")]
        };
        tracker.Remember(original, "REFIDX_original");
        for (var i = 0; i < 1000; i++)
        {
            var incoming = original with
            {
                MessageId = $"quote-{i}", Timestamp = original.Timestamp.AddSeconds(1),
                Segments = [new QuoteSegment($"TMP_{i}"), new TextSegment("reply")],
                Raw = JsonSerializer.SerializeToElement(new
                {
                    id = $"quote-{i}", content = "reply",
                    msg_elements = new[] { new { msg_idx = $"TMP_{i}", content = "unique" } }
                })
            };
            Assert.Equal("content-match", tracker.Resolve(incoming).Source);
        }
        Assert.Equal("REFIDX_original", tracker.ReferenceIndexOf(channel, "original"));
        var indices = (System.Collections.IDictionary)Get(tracker, "_byIndex")!;
        Assert.InRange(indices.Count, 1, 64);
    }

    [Fact]
    public void ReadsBeforeNextExpiryDoNotAllocateFullCacheCopies()
    {
        var clock = new Clock();
        using var tracker = new QQReplyTracker(TimeSpan.FromHours(1), clock);
        var channel = Channel.Group("g");
        for (var i = 0; i < 4096; i++) tracker.Remember(new MessageEvent
        {
            MessageId = i.ToString(), Channel = channel, Sender = new User("u"),
            Platform = "qq-official", Segments = [new TextSegment("hello")]
        }, null);
        tracker.Find(channel, "4095");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) tracker.Find(channel, "4095");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4096, $"Repeated cache reads allocated {allocated} bytes.");
    }

    [Fact]
    public void DisposedTrackerReleasesCachesAndRejectsAccess()
    {
        var tracker = new QQReplyTracker();
        tracker.Remember(new MessageEvent
        {
            MessageId = "m", Channel = Channel.Group("g"), Sender = new User("u"),
            Platform = "qq-official", Segments = [new TextSegment("hello")]
        }, "REFIDX_m");
        tracker.Dispose();
        tracker.Dispose();
        tracker.Cleanup(); // A queued timer callback after disposal is harmless.
        Assert.Empty((System.Collections.IDictionary)Get(tracker, "_byId")!);
        Assert.Empty((System.Collections.IDictionary)Get(tracker, "_byIndex")!);
        Assert.Throws<ObjectDisposedException>(() => tracker.Find(Channel.Group("g"), "m"));
    }

    [Fact]
    public async Task StopFailureStillDisposesTrackerAndHttpAndDrainsWorker()
    {
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        using var http = new HttpClient();
        var tokens = new QQTokenProvider(http, config);
        var api = new QQOpenApiClient(http, config, tokens);
        var messages = new QQMessageService(api);
        var tracker = (QQReplyTracker)Get(messages, "_replies")!;
        var gateway = new QQGatewayClient(config, api, tokens, _ => Task.CompletedTask, _ => { }, null!);
        var lifetime = new CancellationTokenSource();
        Set(gateway, "_lifetime", lifetime);
        Set(gateway, "_runner", Task.FromException(new IOException("injected stop failure")));
        var worker = Task.Delay(10);
        Set(gateway, "_worker", worker);
        var adapter = new QQPlatformAdapter();
        Set(adapter, "_gateway", gateway);
        Set(adapter, "_messages", messages);
        Set(adapter, "_http", http);
        await Assert.ThrowsAsync<AggregateException>(() => adapter.StopAsync());
        Assert.True(worker.IsCompleted);
        Assert.Null(Get(gateway, "_lifetime"));
        Assert.Null(Get(adapter, "_messages"));
        Assert.Null(Get(adapter, "_http"));
        Assert.Throws<ObjectDisposedException>(() => tracker.Find(Channel.Group("g"), "m"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => http.GetAsync("https://example.org"));
    }

    private sealed class ApiHandler : HttpMessageHandler
    {
        public bool InvalidTimestamp;
        private int _nextId;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("getAppAccessToken"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":7200}")
                });
            var id = ++_nextId;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = $"m-{id}", timestamp = InvalidTimestamp ? long.MaxValue : 0,
                    ext_info = new { ref_idx = $"REFIDX_{id}" }
                }))
            });
        }
    }

    [Fact]
    public async Task UnconsumedSentIndicesStayBoundedAndRouteSendsConsumeThem()
    {
        var handler = new ApiHandler();
        using var http = new HttpClient(handler);
        var config = new QQPlatformConfig { AppId = "app", AppSecret = "secret" };
        var api = new QQOpenApiClient(http, config, new QQTokenProvider(http, config));
        var cache = (QQBoundedCache<string>)Get(api, "_sentReferenceIndices")!;
        for (var i = 0; i < 4100; i++)
            await api.SendMessageAsync("channels/c/messages", new QQSendRequest { Content = "hello" });
        Assert.Equal(4096, cache.Count);
        Assert.Null(api.TakeSentReferenceIndex("m-1"));
        Assert.Equal("REFIDX_4100", api.TakeSentReferenceIndex("m-4100"));
        cache.Clear();
        using var messages = new QQMessageService(api);
        var sent = await messages.SendOfficialAsync("channels/c/messages", "c",
            new QQSendRequest { Content = "hello" }, null, "hello");
        Assert.Null(api.TakeSentReferenceIndex(sent.MessageId));
        Assert.Equal(0, cache.Count);
        handler.InvalidTimestamp = true;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            api.SendMessageAsync("channels/c/messages", new QQSendRequest { Content = "hello" }));
        Assert.Equal(0, cache.Count);
    }

    private static object? Get(object target, string field) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
