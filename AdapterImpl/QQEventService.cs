using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.Adapter.QQPlatform.AdapterImpl;

internal sealed class QQEventService : IEventService
{
    public event Func<BotEvent, Task>? EventReceived;

    public async Task PublishAsync(BotEvent value)
    {
        var listeners = EventReceived;
        if (listeners is null) return;
        foreach (Func<BotEvent, Task> listener in listeners.GetInvocationList())
            await listener(value).ConfigureAwait(false);
    }
}
