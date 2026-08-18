namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Action the build pipeline should perform after a successful (re)build completes. Used to make
/// the "Update" action snapshot, reapply patch SQL, and reboot, while plain rebuilds do nothing.
/// </summary>
public enum PostBuildAction
{
    /// <summary>No post-build action; leave the stack stopped (initial build / manual rebuild).</summary>
    None,

    /// <summary>Snapshot before building, then reapply all patch SQL and start the stack (Update).</summary>
    SnapshotReapplyStart
}
