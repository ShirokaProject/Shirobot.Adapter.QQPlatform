using ShiroBot.Adapter.QQPlatform.AdapterImpl;
using ShiroBot.SDK.Models;
using Xunit;

namespace ShiroBot.QQPlatform.Tests;

public sealed class ContextRetentionTests
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static MessageEvent Message(string id, TestClock clock) => new()
    {
        MessageId = id, Channel = Channel.Group("group"), Sender = new User("user"),
        Timestamp = clock.Now, Platform = "qq", Segments = [new TextSegment(id)]
    };

    [Fact]
    public void ExpirationRemovesMessageAndReferenceIndex()
    {
        var clock = new TestClock();
        using var tracker = new QQReplyTracker(TimeSpan.FromMinutes(5), clock);
        var message = Message("first", clock);
        tracker.Remember(message, "REFIDX_first");
        clock.Now += TimeSpan.FromMinutes(4);
        Assert.NotNull(tracker.Find(message.Channel, message.MessageId));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Null(tracker.Find(message.Channel, message.MessageId));
        Assert.Null(tracker.ReferenceIndexOf(message.Channel, message.MessageId));
        Assert.Null(tracker.ChainOf(message.Channel, message.MessageId));
    }

    [Fact]
    public void CleanupRemovesExpiredAncestorsWithoutExpiringNewReply()
    {
        var clock = new TestClock();
        using var tracker = new QQReplyTracker(TimeSpan.FromMinutes(5), clock);
        var first = Message("first", clock);
        tracker.Remember(first, "REFIDX_first");
        clock.Now += TimeSpan.FromMinutes(4);
        var second = Message("second", clock);
        tracker.Remember(second, "REFIDX_second", tracker.ChainOf(first.Channel, first.MessageId));
        clock.Now += TimeSpan.FromMinutes(1);
        tracker.Cleanup();
        Assert.Null(tracker.Find(first.Channel, first.MessageId));
        Assert.NotNull(tracker.Find(second.Channel, second.MessageId));
        Assert.Single(tracker.ChainOf(second.Channel, second.MessageId)!);
    }

    [Fact]
    public void CountLimitStillAppliesInsideRetentionWindow()
    {
        var clock = new TestClock();
        using var tracker = new QQReplyTracker(TimeSpan.FromHours(1), clock);
        for (var i = 0; i <= 4096; i++) tracker.Remember(Message(i.ToString(), clock), null);
        Assert.Null(tracker.Find(Channel.Group("group"), "0"));
        Assert.NotNull(tracker.Find(Channel.Group("group"), "4096"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5000)]
    public void CustomCountLimitEvictsOldestMessageAndIndex(int limit)
    {
        var clock = new TestClock();
        using var tracker = new QQReplyTracker(TimeSpan.FromHours(1), clock, limit);
        for (var i = 0; i <= limit; i++)
            tracker.Remember(Message(i.ToString(), clock), $"REFIDX_{i}");
        Assert.Null(tracker.Find(Channel.Group("group"), "0"));
        Assert.Null(tracker.ReferenceIndexOf(Channel.Group("group"), "0"));
        Assert.NotNull(tracker.Find(Channel.Group("group"), "1"));
        Assert.NotNull(tracker.Find(Channel.Group("group"), limit.ToString()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConfigAndTrackerRejectNonPositiveCountLimit(int limit)
    {
        var config = new ShiroBot.Adapter.QQPlatform.QQPlatformConfig
        {
            AppId = "app", AppSecret = "secret", MessageContextMaxMessages = limit
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new QQReplyTracker(maxCachedMessages: limit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConfigRejectsNonPositiveRetention(int minutes)
    {
        var config = new ShiroBot.Adapter.QQPlatform.QQPlatformConfig
        {
            AppId = "app", AppSecret = "secret", MessageContextRetentionMinutes = minutes
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
    }
}
