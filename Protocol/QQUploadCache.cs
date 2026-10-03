using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

/// <summary>Reuses <c>file_info</c> values until QQ says they expire, so the same media is not uploaded twice.</summary>
internal sealed class QQUploadCache
{
    private const int MaxEntries = 500;
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(10);
    // ttl 0 means "valid for a long time"; stay conservative.
    private static readonly TimeSpan LongLived = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, (string FileInfo, DateTimeOffset ExpiresAt)> _entries = [];
    private readonly Queue<string> _order = new();

    public static string Key(Channel channel, int fileType, string source) =>
        $"{(int)channel.Type}|{channel.Id}|{fileType}|{source}";

    public string? Get(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            if (entry.ExpiresAt > DateTimeOffset.UtcNow) return entry.FileInfo;
            _entries.Remove(key);
            return null;
        }
    }

    public void Set(string key, string fileInfo, int? ttlSeconds)
    {
        var lifetime = ttlSeconds is > 0 ? TimeSpan.FromSeconds(ttlSeconds.Value) - Margin : LongLived;
        if (lifetime <= TimeSpan.Zero) return;
        lock (_gate)
        {
            if (!_entries.ContainsKey(key)) _order.Enqueue(key);
            _entries[key] = (fileInfo, DateTimeOffset.UtcNow + lifetime);
            while (_order.Count > MaxEntries) _entries.Remove(_order.Dequeue());
        }
    }
}
