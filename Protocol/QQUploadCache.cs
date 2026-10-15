using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.Protocol;

/// <summary>Reuses <c>file_info</c> values until QQ says they expire, so the same media is not uploaded twice.</summary>
internal sealed class QQUploadCache
{
    private const int MaxEntries = 500;
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(10);
    // ttl 0 means "valid for a long time"; stay conservative.
    private static readonly TimeSpan LongLived = TimeSpan.FromHours(1);

    private readonly QQBoundedCache<string> _entries = new(MaxEntries);

    public static string Key(Channel channel, int fileType, string source) =>
        $"{(int)channel.Type}|{channel.Id}|{fileType}|{source}";

    public string? Get(string key) => _entries.TryGet(key, out var value) ? value : null;

    public void Set(string key, string fileInfo, int? ttlSeconds)
    {
        var lifetime = ttlSeconds is > 0 ? TimeSpan.FromSeconds(ttlSeconds.Value) - Margin : LongLived;
        if (lifetime > TimeSpan.Zero) _entries.Set(key, fileInfo, lifetime);
    }
}
