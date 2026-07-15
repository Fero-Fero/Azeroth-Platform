using AzerothPlatform.Api.Hubs;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace AzerothPlatform.Api.Services;

/// <summary>
/// Publishes build events to SignalR clients
/// </summary>
public class SignalRBuildEventPublisher : IBuildEventPublisher
{
    private readonly IHubContext<BuildProgressHub> _hubContext;

    public SignalRBuildEventPublisher(IHubContext<BuildProgressHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishPhaseChangedAsync(string stackId, string phase)
    {
        return _hubContext.Clients.Group(stackId).SendAsync("BuildPhaseChanged", stackId, phase);
    }

    public Task PublishProgressUpdatedAsync(string stackId, int progressPercent, string currentStep)
    {
        return _hubContext.Clients.Group(stackId).SendAsync("BuildProgressUpdated", stackId, progressPercent, currentStep);
    }

    public Task PublishLogReceivedAsync(string stackId, string logLine)
    {
        return _hubContext.Clients.Group(stackId).SendAsync("BuildLogReceived", stackId, logLine);
    }

    public Task PublishBuildCompletedAsync(string stackId, bool success)
    {
        return _hubContext.Clients.Group(stackId).SendAsync("BuildCompleted", stackId, success);
    }

    public Task PublishBuildFailedAsync(string stackId, string errorMessage)
    {
        return _hubContext.Clients.Group(stackId).SendAsync("BuildFailed", stackId, errorMessage);
    }
}
