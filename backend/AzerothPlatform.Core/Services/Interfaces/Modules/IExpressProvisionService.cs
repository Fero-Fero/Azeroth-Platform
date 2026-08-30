namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Runs Express Setup after the operator clicks Setup and Launch on a local stack.
/// </summary>
public interface IExpressProvisionService
{
    /// <summary>Starts Express Setup from the beginning when status is Pending.</summary>
    void Start(string stackId);

    /// <summary>
    /// Continues after the operator uploaded or downloaded a base client
    /// (<see cref="AzerothPlatform.Core.Contracts.ExpressProvisionStatus.WaitingForClient"/>).
    /// </summary>
    void ContinueAfterClient(string stackId);

    /// <summary>
    /// Re-runs Express Setup from the checkpoint that failed
    /// (<see cref="AzerothPlatform.Core.Contracts.ExpressProvisionStatus.Failed"/>).
    /// </summary>
    void Retry(string stackId);

    /// <summary>Clears the one-time "all ready, press Start" Overview notice.</summary>
    void DismissReadyNotice(string stackId);

    /// <summary>
    /// Re-enqueues stacks left in <see cref="AzerothPlatform.Core.Contracts.ExpressProvisionStatus.Running"/>
    /// after a manager restart so Express Setup is not tied to the browser session.
    /// </summary>
    void ResumeInterrupted();
}
