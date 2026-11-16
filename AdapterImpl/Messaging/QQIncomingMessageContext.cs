using System.Text.Json;
using System.Text.Json.Nodes;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Protocol.Mapping;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;

/// <summary>
///     Per-adapter message context: recently seen messages, quote chains, arrival times and interaction
///     reply targets. No sending policy lives here.
/// </summary>
internal sealed class QQIncomingMessageContext : IDisposable
{
    private const int Capacity = 4096;

    private readonly QQReplyTracker _replies;
    private readonly IConsoleLogger? _logger;
    private readonly Dictionary<string, (string MessageId, DateTimeOffset SeenAt)> _recent = [];
    private readonly Queue<string> _recentOrder = new();
    private readonly Dictionary<string, DateTimeOffset> _arrivals = [];
    private readonly Queue<string> _arrivalOrder = new();
    private readonly Dictionary<string, QOfficialMessageReply> _interactionReplies = new(StringComparer.Ordinal);
    private readonly Queue<string> _interactionReplyOrder = new();

    public QQIncomingMessageContext(TimeSpan? retention = null, int maxMessages = Capacity,
        IConsoleLogger? logger = null)
    {
        _replies = new QQReplyTracker(retention, maxCachedMessages: maxMessages);
        _logger = logger;
    }

    public MessageEvent RegisterIncoming(MessageEvent message)
    {
        var key = ChannelKey(message.Channel);
        lock (_recent)
        {
            if (!_recent.ContainsKey(key)) _recentOrder.Enqueue(key);
            _recent[key] = (message.MessageId, DateTimeOffset.UtcNow);
            while (_recentOrder.Count > Capacity) _recent.Remove(_recentOrder.Dequeue());
        }
        lock (_arrivals)
        {
            if (_arrivals.TryAdd(message.MessageId, DateTimeOffset.UtcNow)) _arrivalOrder.Enqueue(message.MessageId);
            while (_arrivalOrder.Count > Capacity) _arrivals.Remove(_arrivalOrder.Dequeue());
        }

        var resolution = _replies.Resolve(message);
        if (resolution.Chain.Count > 0)
        {
            var preview = QQReplyTracker.Preview(resolution.Chain[0]);
            // Plugins match replies by message ID, not by QQ's REFIDX_ value; only a message found in the
            // cache is known by its own ID.
            var knownId = resolution.Source is "index" or "content-match" ? resolution.Chain[0].MessageId : null;
            message = message with
            {
                Segments = message.Segments.Select(segment => segment is QuoteSegment quoteSegment
                    ? quoteSegment with { MessageId = knownId ?? quoteSegment.MessageId, Preview = preview }
                    : segment).ToArray()
            };
        }

        var enriched = AddResolvedReplyChain(message.Raw, resolution.Chain) is { } rawWithChain
            ? message with { Raw = rawWithChain }
            : message;
        _replies.Remember(enriched, QQReplyTracker.OwnIndexOf(message), resolution.Chain);
        if (message.GetQuote() is { } quote)
        {
            var entries = string.Join(" <- ", resolution.Chain.Select(item =>
                $"{item.Sender.Name ?? item.Sender.Id}:{string.Join('+', item.Segments.Select(segment => segment.GetType().Name))}"));
            _logger?.Info($"QQ Official reply chain: source={resolution.Source}, " +
                $"referenceKey={QQReplyTracker.Fingerprint(quote.MessageId)}, depth={resolution.Chain.Count}, " +
                $"messages=[{entries}], {resolution.Detail}" +
                (resolution.Chain.Count == 0
                    ? $"; recentCache=[{_replies.DescribeRecent(message.Channel, message.MessageId)}]"
                    : ""));
        }
        return enriched;
    }

    public void RememberInteraction(InteractionEvent interaction)
    {
        if (interaction.Raw is not QOfficialButtonInteraction raw) return;
        lock (_interactionReplies)
        {
            if (!_interactionReplies.ContainsKey(interaction.InteractionId))
                _interactionReplyOrder.Enqueue(interaction.InteractionId);
            _interactionReplies[interaction.InteractionId] = new() { EventId = raw.EventId ?? raw.InteractionId };
            while (_interactionReplyOrder.Count > Capacity)
                _interactionReplies.Remove(_interactionReplyOrder.Dequeue());
        }
    }

    public QOfficialMessageReply ResolveInteractionReply(InteractionReference reference)
    {
        lock (_interactionReplies)
        {
            return _interactionReplies.TryGetValue(reference.InteractionId, out var reply)
                ? reply
                : throw new InvalidOperationException("Interaction reply context is no longer available.");
        }
    }

    public MessageEvent? Find(Channel channel, string messageId) => _replies.Find(channel, messageId);

    public string? ReferenceIndexOf(Channel channel, string messageId) =>
        _replies.ReferenceIndexOf(channel, messageId);

    public IReadOnlyList<QQQuotedMessage>? ChainOf(Channel channel, string messageId) =>
        _replies.ChainOf(channel, messageId);

    public DateTimeOffset? ArrivalOf(string messageId)
    {
        lock (_arrivals) return _arrivals.TryGetValue(messageId, out var arrivedAt) ? arrivedAt : null;
    }

    public string? TryRecentMessageId(Channel channel, TimeSpan window)
    {
        lock (_recent)
        {
            if (_recent.TryGetValue(ChannelKey(channel), out var entry)
                && DateTimeOffset.UtcNow - entry.SeenAt < window)
                return entry.MessageId;
        }
        return null;
    }

    public void RememberSent(Channel channel, SentMessage sent, string? referenceIndex,
        string? replyToMessageId, IReadOnlyList<MessageSegment> segments, User? self)
    {
        var chain = string.IsNullOrWhiteSpace(replyToMessageId) ? null : _replies.ChainOf(channel, replyToMessageId);
        var snapshot = new MessageEvent
        {
            Platform = QQEventTranslator.Platform,
            SelfId = self?.Id,
            MessageId = sent.MessageId,
            Channel = channel,
            Sender = self ?? new User("qq-official-bot") { Name = "机器人", IsBot = true },
            Segments = segments.Where(segment => segment is not QuoteSegment).Select(SnapshotSegment).ToArray(),
            Timestamp = sent.Timestamp ?? DateTimeOffset.UtcNow
        };
        _replies.Remember(snapshot, referenceIndex, chain);
    }

    public void Clear()
    {
        _replies.Dispose();
        lock (_recent) { _recent.Clear(); _recentOrder.Clear(); }
        lock (_arrivals) { _arrivals.Clear(); _arrivalOrder.Clear(); }
        lock (_interactionReplies) { _interactionReplies.Clear(); _interactionReplyOrder.Clear(); }
    }

    public void Dispose() => Clear();

    internal static JsonElement? AddResolvedReplyChain(object? raw, IReadOnlyList<QQQuotedMessage> chain)
    {
        if (raw is not JsonElement { ValueKind: JsonValueKind.Object } rawEvent) return null;
        var document = JsonNode.Parse(rawEvent.GetRawText())?.AsObject();
        if (document is null) return null;
        document["resolved_reply_chain"] = new JsonArray(chain.Select(item => (JsonNode)new JsonObject
        {
            ["messageId"] = item.MessageId,
            ["sender"] = new JsonObject
            {
                ["id"] = item.Sender.Id,
                ["name"] = item.Sender.Name,
                ["isBot"] = item.Sender.IsBot
            },
            ["timestamp"] = item.Timestamp,
            ["segments"] = new JsonArray(item.Segments.Select(ToRawSegment).ToArray())
        }).ToArray());
        using var enriched = JsonDocument.Parse(document.ToJsonString());
        return enriched.RootElement.Clone();
    }

    private static JsonNode ToRawSegment(MessageSegment segment) => segment switch
    {
        TextSegment text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
        ImageSegment image => new JsonObject { ["type"] = "image", ["url"] = image.Uri, ["filename"] = image.FileName },
        VideoSegment video => new JsonObject { ["type"] = "video", ["url"] = video.Uri, ["filename"] = video.FileName },
        AudioSegment audio => new JsonObject { ["type"] = "audio", ["url"] = audio.Uri, ["filename"] = audio.FileName },
        FileSegment file => new JsonObject { ["type"] = "file", ["url"] = file.Uri, ["filename"] = file.FileName },
        EmojiSegment emoji => new JsonObject { ["type"] = "emoji", ["id"] = emoji.Id, ["name"] = emoji.Name },
        MentionSegment mention => new JsonObject { ["type"] = "mention", ["id"] = mention.UserId, ["name"] = mention.DisplayName },
        MentionAllSegment => new JsonObject { ["type"] = "mention_all" },
        _ => new JsonObject { ["type"] = segment.GetType().Name }
    };

    private static MessageSegment SnapshotSegment(MessageSegment segment) =>
        segment is ResourceSegment resource && resource.Uri.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)
            ? resource with { Uri = "base64:[内容已省略]" }
            : segment;

    private static string ChannelKey(Channel channel) => $"{(int)channel.Type}:{channel.Id}";
}
