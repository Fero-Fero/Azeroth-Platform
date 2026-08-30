using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AzerothPlatform.Api.Hubs;

/// <summary>
/// SignalR hub for armory background-job progress. Clients join a per-stack group so they receive
/// status updates for the armory operation running on that stack (and can reattach after a refresh).
/// </summary>
[Authorize]
public class ArmoryProgressHub : Hub
{
    public Task SubscribeToArmory(string stackId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, stackId);
    }

    public Task UnsubscribeFromArmory(string stackId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, stackId);
    }
}
