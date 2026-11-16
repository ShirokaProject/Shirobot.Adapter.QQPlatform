using ShiroBot.Adapter.QQPlatform.Protocol;
using ShiroBot.Adapter.QQPlatform.Protocol.Support;
using ShiroBot.SDK.Plugin;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl.Dispatch;

/// <summary>Caches group names used to enrich events and outgoing logs.</summary>
internal sealed class QQGroupNameCache(QQOpenApiClient api, IConsoleLogger logger, bool traceEvents)
{
    private readonly QQBoundedCache<string?> _names = new(4096);

    public string? GetCached(string groupOpenId) =>
        _names.TryGet(groupOpenId, out var cached) ? cached : null;

    public async Task<string?> GetAsync(string groupOpenId, CancellationToken cancellationToken = default)
    {
        if (_names.TryGet(groupOpenId, out var cached)) return cached;
        string? name = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            name = await api.GetGroupNameAsync(groupOpenId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            if (traceEvents) logger.Warning($"QQ group name lookup failed: {ex.Message}");
        }
        _names.Set(groupOpenId, name, name is null ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1));
        return name;
    }

    public void Clear() => _names.Clear();
}
