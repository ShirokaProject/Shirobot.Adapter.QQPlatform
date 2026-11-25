using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Messaging;

/// <summary>
///     Tracks QQ passive-reply msg_seq values and reply windows. Since 2026-11-17 a group message can be
///     replied to within sixty minutes, five times, or fifty times when the bot's developer is the group's
///     owner or an admin; direct messages allow four replies within sixty minutes. A group message replied to
///     in a private chat shares the group message's window and count, so limits follow where the source
///     message arrived, not where the reply goes.
/// </summary>
internal sealed class QQPassiveReplyWindow(int groupMaxReplies = QQPassiveReplyWindow.DefaultGroupMaxReplies)
{
    public const int DefaultGroupMaxReplies = 5;
    public const int ElevatedGroupMaxReplies = 50;
    public static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan DirectWindow = TimeSpan.FromMinutes(60);
    public const int DirectMaxReplies = 4;

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

    /// <param name="sourceType">Where the source message arrived; defaults to the target channel's type.</param>
    public bool CanReply(Channel channel, string messageId, DateTimeOffset? arrivedAt, ChannelType? sourceType = null)
    {
        var (window, maxReplies) = Limits(sourceType ?? channel.Type);
        if (arrivedAt is { } at && DateTimeOffset.UtcNow - at >= window) return false;
        lock (_gate)
            return !_sequences.TryGetValue(messageId, out var used) || used < maxReplies;
    }

    public (TimeSpan Window, int MaxReplies) Limits(ChannelType sourceType) =>
        sourceType == ChannelType.Group ? (GroupWindow, groupMaxReplies) : (DirectWindow, DirectMaxReplies);

    public void Clear()
    {
        lock (_gate) { _sequences.Clear(); _sequenceOrder.Clear(); }
    }
}
