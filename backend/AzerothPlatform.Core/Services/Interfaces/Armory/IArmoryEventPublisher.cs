using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Pushes armory background-job status changes to subscribed clients (real-time updates).
/// </summary>
public interface IArmoryEventPublisher
{
    Task PublishStatusAsync(ArmoryJobStatusDto status);
}
