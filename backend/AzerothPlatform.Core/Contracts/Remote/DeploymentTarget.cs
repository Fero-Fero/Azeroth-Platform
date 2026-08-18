namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Where a stack's containers actually run. The platform itself always runs locally; External
/// stacks are built locally and shipped to a remote Docker host over an SSH docker context.
/// </summary>
public enum DeploymentTarget
{
    /// <summary>Containers run on the local Docker engine (default).</summary>
    Local,

    /// <summary>Containers run on a remote Docker host reached over SSH.</summary>
    External
}
