using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace Aura.Api.Hubs;

[Authorize(Policy = "楼栋管理员")]
public sealed class EventHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var role = Context.User?.FindFirstValue(ClaimTypes.Role);
        if (!string.IsNullOrWhiteSpace(role))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"role:{role}");
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var role = Context.User?.FindFirstValue(ClaimTypes.Role);
        if (!string.IsNullOrWhiteSpace(role))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"role:{role}");
        }
        await base.OnDisconnectedAsync(exception);
    }
}
