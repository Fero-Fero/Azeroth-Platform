using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Runs stack lifecycle actions (start/stop/restart/start-database) as detached background jobs so they
/// survive the request that triggered them and can be reattached to (after navigating or a page refresh)
/// via <see cref="GetStatus"/> or the SignalR stream. Enqueuing while a job is already running for the
/// stack returns the in-flight job instead of racing docker.
/// </summary>
public interface IStackJobService
{
    /// <summary>Current lifecycle job for the stack, or null when none has ever run.</summary>
    StackJobStatusDto? GetStatus(string stackId);

    /// <summary>Starts a detached lifecycle job for the stack. If one is already running, returns it unchanged
    /// (so a second click can't launch a duplicate docker operation) unless <paramref name="supersedeRunning"/>
    /// is true (used by force-stop to interrupt a stuck start).
    /// </summary>
    StackJobStatusDto Enqueue(
        string stackId,
        StackJobAction action,
        PublicHostApplyPlanDto? publicHostPlan = null,
        bool supersedeRunning = false);

    /// <summary>Updates the message on an in-flight lifecycle job (best-effort).</summary>
    void ReportProgress(string stackId, string message);
}

/// <summary>Publishes stack lifecycle job status to connected clients (SignalR).</summary>
public interface IStackEventPublisher
{
    Task PublishStatusAsync(StackJobStatusDto status);
}
