using AzerothPlatform.Api.Hubs;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace AzerothPlatform.Api.Services;

public sealed class SignalRClientEventPublisher : IClientEventPublisher
{
    private readonly IHubContext<StackProgressHub> _hubContext;

    public SignalRClientEventPublisher(IHubContext<StackProgressHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishStatusAsync(ClientJobStatusDto status) =>
        _hubContext.Clients.Group(status.StackId).SendAsync("ClientJobUpdated", status);
}
