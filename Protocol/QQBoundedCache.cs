namespace ShiroBot.Adapter.QQPlatform.Protocol;

/// <summary>A bounded, expiring cache. Its eviction list contains exactly one node per live key.</summary>
internal sealed class QQBoundedCache<T>(int capacity, TimeProvider? clock = null)
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();
    private DateTimeOffset _nextCleanup;
    private sealed record Entry(string Key, T Value, DateTimeOffset ExpiresAt);

    internal int Count { get { lock (_gate) return _entries.Count; } }

    public bool TryGet(string key, out T value)
    {
        lock (_gate)
        {
            PruneExpired();
            if (_entries.TryGetValue(key, out var node))
            {
                if (node.Value.ExpiresAt > _clock.GetUtcNow())
                {
                    value = node.Value.Value;
                    return true;
                }
                Remove(node);
            }
            value = default!;
            return false;
        }
    }

    public void Set(string key, T value, TimeSpan lifetime)
    {
        lock (_gate)
        {
            PruneExpired();
            if (_entries.TryGetValue(key, out var previous)) Remove(previous);
            if (lifetime <= TimeSpan.Zero) return;
            var node = _order.AddLast(new Entry(key, value, _clock.GetUtcNow() + lifetime));
            _entries.Add(key, node);
            while (_entries.Count > _capacity) Remove(_order.First!);
        }
    }

    public T? Take(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node)) return default;
            Remove(node);
            return node.Value.ExpiresAt > _clock.GetUtcNow() ? node.Value.Value : default;
        }
    }

    public void Clear()
    {
        lock (_gate) { _entries.Clear(); _order.Clear(); }
    }

    private void PruneExpired()
    {
        var now = _clock.GetUtcNow();
        if (now < _nextCleanup) return;
        _nextCleanup = now.AddMinutes(1);
        var node = _order.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.ExpiresAt <= now) Remove(node);
            node = next;
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _entries.Remove(node.Value.Key);
        _order.Remove(node);
    }
}
