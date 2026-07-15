using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AzerothPlatform.Api.Hubs;

/// <summary>
/// SignalR hub for stack lifecycle background-job progress (start/stop/restart/start-database). Clients
/// join a per-stack group so they receive status updates for the operation running on that stack (and
/// can reattach after navigating away or refreshing).
/// </summary>
[Authorize]
public class StackProgressHub : Hub
{
    public Task SubscribeToStack(string stackId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, stackId);
    }

    public Task UnsubscribeFromStack(string stackId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, stackId);
    }

    public Task SubscribeToDockerCleanup()
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, DockerCleanupJobGroups.SignalR);
    }

    public Task UnsubscribeFromDockerCleanup()
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, DockerCleanupJobGroups.SignalR);
    }
}
