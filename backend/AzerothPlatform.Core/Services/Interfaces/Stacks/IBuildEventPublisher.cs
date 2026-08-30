using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Build event notifications for real-time updates
/// </summary>
public interface IBuildEventPublisher
{
    Task PublishPhaseChangedAsync(string stackId, string phase);
    Task PublishProgressUpdatedAsync(string stackId, int progressPercent, string currentStep);
    Task PublishLogReceivedAsync(string stackId, string logLine);
    Task PublishBuildCompletedAsync(string stackId, bool success);
    Task PublishBuildFailedAsync(string stackId, string errorMessage);
    Task PublishModuleCheckUpdatedAsync(string stackId, IReadOnlyList<ModuleCheckItemDto> items);
}
