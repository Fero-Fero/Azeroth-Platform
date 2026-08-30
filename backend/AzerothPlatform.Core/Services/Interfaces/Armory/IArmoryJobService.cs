using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Runs armory lifecycle operations (start/stop/restart/rebuild) as detached background jobs so they
/// survive the HTTP request that triggered them (e.g. a browser refresh). Callers enqueue an action and
/// get back the initial status; progress is tracked in-memory per stack and streamed over SignalR.
/// </summary>
public interface IArmoryJobService
{
    /// <summary>
    /// Enqueues an armory operation for the stack and starts it in the background. If a job is already
    /// running for the stack, that running job is returned unchanged (operations are serialized per stack).
    /// </summary>
    ArmoryJobStatusDto Enqueue(string stackId, ArmoryJobAction action);

    /// <summary>Returns the latest job status for the stack, or null if none has run this process lifetime.</summary>
    ArmoryJobStatusDto? GetStatus(string stackId);
}
