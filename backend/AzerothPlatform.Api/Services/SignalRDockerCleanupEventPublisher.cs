using AzerothPlatform.Api.Hubs;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace AzerothPlatform.Api.Services;

/// <summary>
/// Publishes Docker disk-reclaim background-job status to SignalR clients in the global cleanup group.
/// </summary>
public sealed class SignalRDockerCleanupEventPublisher : IDockerCleanupEventPublisher
{
    private readonly IHubContext<StackProgressHub> _hubContext;

    public SignalRDockerCleanupEventPublisher(IHubContext<StackProgressHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishStatusAsync(DockerCleanupJobStatusDto status)
    {
        return _hubContext.Clients
            .Group(DockerCleanupJobGroups.SignalR)
            .SendAsync("DockerCleanupUpdated", status);
    }
}
