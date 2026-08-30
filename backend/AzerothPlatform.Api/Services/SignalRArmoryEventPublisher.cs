using AzerothPlatform.Api.Hubs;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace AzerothPlatform.Api.Services;

/// <summary>
/// Publishes armory background-job status to SignalR clients grouped by stack id.
/// </summary>
public class SignalRArmoryEventPublisher : IArmoryEventPublisher
{
    private readonly IHubContext<ArmoryProgressHub> _hubContext;

    public SignalRArmoryEventPublisher(IHubContext<ArmoryProgressHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishStatusAsync(ArmoryJobStatusDto status)
    {
        return _hubContext.Clients.Group(status.StackId).SendAsync("ArmoryJobUpdated", status);
    }
}
