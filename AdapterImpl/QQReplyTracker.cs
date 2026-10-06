using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Wire;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

internal sealed record QQReplyResolution(IReadOnlyList<QQQuotedMessage> Chain, string Source, string Detail)
{
    public static readonly QQReplyResolution None = new([], "none", string.Empty);
}

/// <summary>
/// Remembers recent messages so a quote can be expanded into its full reply chain.
/// QQ gives no way to fetch a message by id, so resolution is layered: reference index (REFIDX_*, plus aliases
/// learned for TMP_* placeholders) → unique content match against the cache → whatever <c>msg_elements</c> describes.
/// </summary>
internal sealed class QQReplyTracker
{
    private const int MaxCachedMessages = 4096;
    private const int MaxChainLength = 32;

    private readonly object _gate = new();
    private readonly Dictionary<MessageKey, Entry> _byId = [];
    private readonly Dictionary<IndexKey, Entry> _byIndex = [];
    private readonly Queue<MessageKey> _order = new();

    private readonly record struct MessageKey(ChannelType Type, string ChannelId, string MessageId);
    private readonly record struct IndexKey(ChannelType Type, string ChannelId, string Index);

    private sealed record Entry(MessageEvent Event, IReadOnlyList<QQQuotedMessage> Chain)
    {
        public HashSet<string> Indexes { get; } = new(StringComparer.Ordinal);
    }

    public void Remember(MessageEvent message, string? referenceIndex, IReadOnlyList<QQQuotedMessage>? chain = null)
    {
        var entry = new Entry(message, chain ?? []);
        var channel = message.Channel;
        var key = new MessageKey(channel.Type, channel.Id, message.MessageId);
        lock (_gate)
        {
            if (_byId.TryGetValue(key, out var previous)) Detach(key, previous);
            else _order.Enqueue(key);
            _byId[key] = entry;
            if (!string.IsNullOrWhiteSpace(referenceIndex)) Attach(channel, referenceIndex, entry);
            while (_order.Count > MaxCachedMessages)
            {
                var oldest = _order.Dequeue();
                if (_byId.Remove(oldest, out var evicted)) Detach(oldest, evicted);
            }
        }
    }

    public MessageEvent? Find(Channel channel, string messageId)
    {
        lock (_gate)
            return _byId.TryGetValue(new MessageKey(channel.Type, channel.Id, messageId), out var entry) ? entry.Event : null;
    }

    public QQQuotedMessage[]? ChainOf(Channel channel, string messageId)
    {
        lock (_gate)
            return _byId.TryGetValue(new MessageKey(channel.Type, channel.Id, messageId), out var entry)
                ? BuildChain(entry)
                : null;
    }

    /// <summary>The REFIDX_* value to put in <c>message_reference</c> when quoting this message.</summary>
    public string? ReferenceIndexOf(Channel channel, string messageId)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(new MessageKey(channel.Type, channel.Id, messageId), out var entry)) return null;
            return entry.Indexes.FirstOrDefault(index => index.StartsWith("REFIDX_", StringComparison.Ordinal))
                ?? entry.Indexes.FirstOrDefault(index => !IsTemporary(index));
        }
    }

    public QQReplyResolution Resolve(MessageEvent message)
    {
        var quote = message.GetQuote();
        if (quote is null) return QQReplyResolution.None;
        var wire = message.Raw is JsonElement { ValueKind: JsonValueKind.Object } raw
            ? raw.Deserialize<QQIncomingMessage>(QQJson.Options)
            : null;
        var blocks = wire is null ? [] : QQQuoteElementParser.Parse(wire);
        var elementIndex = wire?.MessageElements?.FirstOrDefault()?.MessageIndex;
        var keys = new[] { elementIndex, quote.MessageId }
            .Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray();

        lock (_gate)
        {
            foreach (var key in keys)
                if (_byIndex.TryGetValue(new IndexKey(message.Channel.Type, message.Channel.Id, key!), out var hit)
                    && hit.Event.MessageId != message.MessageId)
                    return new QQReplyResolution(BuildChain(hit), "index", Describe(keys, blocks));

            if (blocks.Count > 0 && FindUnique(message, blocks) is { } matched)
            {
                foreach (var key in keys) Attach(message.Channel, key!, matched);
                return new QQReplyResolution(BuildChain(matched), "content-match", Describe(keys, blocks));
            }

            if (blocks.Count == 0) return new QQReplyResolution([], "none", Describe(keys, blocks));
            return new QQReplyResolution(ChainFromBlocks(message, blocks, elementIndex ?? quote.MessageId),
                "msg_elements", Describe(keys, blocks));
        }
    }

    // ---- resolution helpers (call with _gate held) ----

    /// <summary>Builds a chain from msg_elements; blocks that match a cached message adopt its real sender and ancestry.</summary>
    private QQQuotedMessage[] ChainFromBlocks(MessageEvent message, IReadOnlyList<QQQuoteBlock> blocks, string headId)
    {
        var result = new List<QQQuotedMessage>(blocks.Count);
        for (var i = 0; i < blocks.Count && result.Count < MaxChainLength; i++)
        {
            var cached = i == 0 ? null : FindUnique(message, blocks.Skip(i).ToArray());
            if (cached is not null)
            {
                foreach (var item in BuildChain(cached))
                    if (result.All(existing => existing.MessageId != item.MessageId)) result.Add(item);
                break;
            }
            result.Add(new QQQuotedMessage(i == 0 ? headId : $"{headId}#{i}",
                new User("unknown") { Name = "未知用户" }, blocks[i].Segments, DateTimeOffset.MinValue));
        }
        return result.ToArray();
    }

    private Entry? FindUnique(MessageEvent current, IReadOnlyList<QQQuoteBlock> blocks)
    {
        var head = blocks[0];
        var candidates = _byId.Values
            .Where(entry => entry.Event.Channel.Type == current.Channel.Type
                && entry.Event.Channel.Id == current.Channel.Id
                && entry.Event.MessageId != current.MessageId
                && entry.Event.Timestamp <= current.Timestamp
                && SameContent(entry.Event.Segments, head.Segments))
            .ToArray();
        if (candidates.Length > 1 && blocks.Count > 1)
        {
            // Same text sent more than once: the ancestor the quote carries tells them apart.
            candidates = candidates.Where(entry => entry.Chain.Count > 0
                && SameContent(entry.Chain[0].Segments, blocks[1].Segments)).ToArray();
        }
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool SameContent(IEnumerable<MessageSegment> cached, IEnumerable<MessageSegment> quoted) =>
        NormalizedText(cached) == NormalizedText(quoted) && SameResources(cached, quoted);

    // Mentions are skipped on both sides: QQ renders them in quotes as plain "@name" while the event carries ids.
    private static string NormalizedText(IEnumerable<MessageSegment> segments) =>
        string.Join(' ', string.Concat(segments.OfType<TextSegment>().Select(segment => segment.Text))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // URLs differ between the push and the quote, so compare the kind of attachment, plus its file id when both have one.
    private static string[] ResourceKeys(IEnumerable<MessageSegment> segments) =>
        segments.OfType<ResourceSegment>().Select(segment => segment.GetType().Name).ToArray();

    private static string? FileId(string uri)
    {
        const string marker = "fileid=";
        var start = uri.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = uri.IndexOf('&', start);
        return end < 0 ? uri[start..] : uri[start..end];
    }

    private static bool SameResources(IEnumerable<MessageSegment> cached, IEnumerable<MessageSegment> quoted)
    {
        var left = cached.OfType<ResourceSegment>().ToArray();
        var right = quoted.OfType<ResourceSegment>().ToArray();
        if (!ResourceKeys(left).SequenceEqual(ResourceKeys(right))) return false;
        return left.Zip(right).All(pair => FileId(pair.First.Uri) is not { } a || FileId(pair.Second.Uri) is not { } b || a == b);
    }

    private static QQQuotedMessage[] BuildChain(Entry parent)
    {
        var result = new List<QQQuotedMessage>(MaxChainLength) { ToQuoted(parent.Event) };
        foreach (var ancestor in parent.Chain)
        {
            if (result.Count >= MaxChainLength) break;
            if (result.Any(item => item.MessageId == ancestor.MessageId)) continue;
            result.Add(ancestor);
        }
        return result.ToArray();
    }

    private static QQQuotedMessage ToQuoted(MessageEvent message) =>
        new(message.MessageId, message.Sender,
            message.Segments.Where(segment => segment is not QuoteSegment).ToArray(), message.Timestamp);

    private void Attach(Channel channel, string index, Entry entry)
    {
        _byIndex[new IndexKey(channel.Type, channel.Id, index)] = entry;
        entry.Indexes.Add(index);
    }

    private void Detach(MessageKey key, Entry entry)
    {
        foreach (var index in entry.Indexes)
        {
            var indexKey = new IndexKey(key.Type, key.ChannelId, index);
            if (_byIndex.TryGetValue(indexKey, out var indexed) && ReferenceEquals(indexed, entry))
                _byIndex.Remove(indexKey);
        }
    }

    private static bool IsTemporary(string index) => index.StartsWith("TMP_", StringComparison.Ordinal);

    // ---- diagnostics ----

    private static string Describe(IEnumerable<string?> keys, IReadOnlyList<QQQuoteBlock> blocks) =>
        $"keys=[{string.Join(',', keys.Select(Fingerprint))}], blocks=[{string.Join(" <- ", blocks.Select(block => Preview(block.Text)))}]";

    /// <summary>Short display text for a quoted message: its text, or the URL of its media.</summary>
    public static string? Preview(QQQuotedMessage quoted)
    {
        var parts = quoted.Segments.Select(segment => segment switch
        {
            TextSegment text => text.Text,
            ImageSegment image => $"[图片: {image.Uri}]",
            VideoSegment video => $"[视频: {video.Uri}]",
            AudioSegment audio => $"[语音: {audio.Uri}]",
            FileSegment file => $"[文件: {file.FileName ?? file.Uri}]",
            MentionSegment mention => $"@{mention.DisplayName ?? mention.UserId}",
            MentionAllSegment => "@全体成员",
            EmojiSegment emoji => $"[表情: {emoji.Name ?? emoji.Id}]",
            _ => null
        });
        var text = string.Concat(parts).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string Preview(string text)
    {
        var flat = text.Replace("\r", "").Replace('\n', ' ');
        return flat.Length <= 24 ? flat : flat[..24] + "…";
    }

    internal static string Fingerprint(string? value) => string.IsNullOrWhiteSpace(value)
        ? "<none>"
        : (IsTemporary(value) ? "TMP:" : "") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10];

    /// <summary>The message's own <c>msg_idx</c>, which later messages use to quote it.</summary>
    public static string? OwnIndexOf(MessageEvent message)
    {
        if (message.Raw is not JsonElement { ValueKind: JsonValueKind.Object } raw
            || !raw.TryGetProperty("message_scene", out var scene)
            || scene.ValueKind != JsonValueKind.Object
            || !scene.TryGetProperty("ext", out var extensions)
            || extensions.ValueKind != JsonValueKind.Array) return null;
        foreach (var extension in extensions.EnumerateArray())
        {
            if (extension.ValueKind != JsonValueKind.String || extension.GetString() is not { } value) continue;
            var prefix = value.StartsWith("msg_idx=", StringComparison.Ordinal) ? "msg_idx="
                : value.StartsWith("msgIdx:", StringComparison.Ordinal) ? "msgIdx:" : null;
            if (prefix is not null && value.Length > prefix.Length) return value[prefix.Length..].Trim();
        }
        return null;
    }

    public string DescribeRecent(Channel channel, string currentMessageId)
    {
        lock (_gate)
            return string.Join(" | ", _order.Reverse()
                .Where(key => key.Type == channel.Type && key.ChannelId == channel.Id && key.MessageId != currentMessageId)
                .Take(8)
                .Select(key => _byId.TryGetValue(key, out var entry)
                    ? $"{entry.Event.Sender.Name ?? entry.Event.Sender.Id}:idx=[{string.Join(',', entry.Indexes.Select(Fingerprint))}]," +
                      $"text={Preview(NormalizedText(entry.Event.Segments))}"
                    : null)
                .Where(text => text is not null));
    }
}
