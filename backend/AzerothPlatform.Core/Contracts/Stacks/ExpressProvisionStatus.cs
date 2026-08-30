namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Background auto-provisioner that runs once after the first successful Express stack build.
/// </summary>
public enum ExpressProvisionStatus
{
    None,
    Pending,
    Running,
    Completed,
    Failed,
    /// <summary>Phase 1 finished; waiting for the operator to upload or download a base client.</summary>
    WaitingForClient
}
