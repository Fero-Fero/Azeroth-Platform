using AzerothPlatform.Api.Hubs;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace AzerothPlatform.Api.Services;

/// <summary>
/// Publishes stack lifecycle background-job status to SignalR clients grouped by stack id.
/// </summary>
public class SignalRStackEventPublisher : IStackEventPublisher
{
    private readonly IHubContext<StackProgressHub> _hubContext;

    public SignalRStackEventPublisher(IHubContext<StackProgressHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishStatusAsync(StackJobStatusDto status)
    {
        return _hubContext.Clients.Group(status.StackId).SendAsync("StackJobUpdated", status);
    }
}
