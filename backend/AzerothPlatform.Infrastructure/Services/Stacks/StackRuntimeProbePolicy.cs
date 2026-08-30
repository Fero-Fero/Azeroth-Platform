using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// When stack detail refreshes should skip a live <c>docker ps</c>. External lifecycle jobs already
/// occupy SSH, so extra probes stall the UI. Local probes stay on so first-time db-import/database
/// containers appear as running while Start is in progress.
/// </summary>
public static class StackRuntimeProbePolicy
{
    public static bool ShouldSkipLiveProbe(
        bool lifecycleJobRunning,
        DeploymentTarget deploymentTarget,
        StackStatus persistedStatus)
    {
        if (persistedStatus == StackStatus.SetupIncomplete)
        {
            return true;
        }

        return lifecycleJobRunning && deploymentTarget == DeploymentTarget.External;
    }
}
