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
    Failed
}
