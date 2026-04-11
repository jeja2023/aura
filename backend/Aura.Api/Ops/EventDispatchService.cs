using Aura.Api.Hubs;
using Aura.Api.Models;
using Microsoft.AspNetCore.SignalR;

namespace Aura.Api.Ops;

internal sealed class EventDispatchService
{
    private readonly IAlertNotifier _alertNotifier;
    private readonly IHubContext<EventHub> _hubContext;

    public EventDispatchService(IAlertNotifier alertNotifier, IHubContext<EventHub> hubContext)
    {
        _alertNotifier = alertNotifier;
        _hubContext = hubContext;
    }

    public Task BroadcastRoleEventAsync(string eventName, object payload)
    {
        return _hubContext.Clients.Groups("role:building_admin", "role:super_admin").SendAsync(eventName, payload);
    }

    public Task NotifyAlertAsync(string alertType, string detail, string source)
    {
        return _alertNotifier.NotifyAsync(new AlertNotifyMessage(alertType, detail, source, DateTimeOffset.Now));
    }
}

