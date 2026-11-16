using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;

/// <summary>
///     Tracks QQ passive-reply msg_seq values and reply windows: group messages allow five replies within
///     five minutes, direct messages four within sixty minutes.
/// </summary>
internal sealed class QQPassiveReplyWindow
{
    private readonly Dictionary<string, int> _sequences = new(StringComparer.Ordinal);
    private readonly Queue<string> _sequenceOrder = new();
    private readonly object _gate = new();

    public int NextSequence(string key, int? explicitSequence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var hasPrevious = _sequences.TryGetValue(key, out var previous);
            var sequence = explicitSequence ?? (hasPrevious ? checked(previous + 1) : 1);
            if (hasPrevious && sequence <= previous)
                throw new ArgumentException("QQ reply sequence must increase for each source message.",
                    nameof(explicitSequence));
            if (!hasPrevious) _sequenceOrder.Enqueue(key);
            _sequences[key] = sequence;
            while (_sequenceOrder.Count > 4096) _sequences.Remove(_sequenceOrder.Dequeue());
            return sequence;
        }
    }

    public bool CanReply(Channel channel, string messageId, DateTimeOffset? arrivedAt)
    {
        var (window, maxReplies) = Limits(channel);
        if (arrivedAt is { } at && DateTimeOffset.UtcNow - at >= window) return false;
        lock (_gate)
            return !_sequences.TryGetValue(messageId, out var used) || used < maxReplies;
    }

    public static (TimeSpan Window, int MaxReplies) Limits(Channel channel) =>
        channel.Type == ChannelType.Group ? (TimeSpan.FromMinutes(5), 5) : (TimeSpan.FromMinutes(60), 4);

    public void Clear()
    {
        lock (_gate) { _sequences.Clear(); _sequenceOrder.Clear(); }
    }
}
