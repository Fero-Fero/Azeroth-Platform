namespace AzerothPlatform.Core.Contracts;

/// <summary>Phase of the launcher (desktop client) compile pipeline.</summary>
public enum LauncherBuildPhase
{
    Idle = 0,
    Preparing = 1,
    Publishing = 2,
    Packaging = 3,
    Completed = 4,
    Failed = 5
}

/// <summary>
/// Which segment of the launcher's four-part <c>Release.Update.Minor.Patch</c> version to increment
/// on the next build. Bumping a segment resets all less-significant segments to zero.
/// </summary>
public enum LauncherVersionPart
{
    Release = 0,
    Update = 1,
    Minor = 2,
    Patch = 3
}

/// <summary>Request body for <c>POST /api/launcher-build</c>.</summary>
public sealed class LauncherBuildRequestDto
{
    /// <summary>
    /// Which version segment to bump: <c>release</c>, <c>update</c>, <c>minor</c> or <c>patch</c>.
    /// Case-insensitive; defaults to <c>patch</c> when missing or unrecognized.
    /// </summary>
    public string? Part { get; set; }
}

/// <summary>Live status of the launcher compile, polled by the website.</summary>
public sealed class LauncherBuildStatusDto
{
    public LauncherBuildPhase Phase { get; set; } = LauncherBuildPhase.Idle;

    public string Message { get; set; } = string.Empty;

    public bool IsBuilding { get; set; }

    public string? Error { get; set; }

    /// <summary>Recent log lines from the compile (rolling tail).</summary>
    public List<string> Log { get; set; } = new();

    /// <summary>Four-part <c>Release.Update.Minor.Patch</c> version of the available launcher, or null.</summary>
    public string? AvailableVersion { get; set; }

    /// <summary>When the currently available launcher was built (UTC), or null.</summary>
    public DateTime? AvailableBuiltAt { get; set; }

    /// <summary>Size of the available launcher exe in bytes, or 0.</summary>
    public long AvailableSizeBytes { get; set; }

    /// <summary>
    /// Lowercase hex SHA-256 of the available launcher exe, or null. The desktop launcher fetches this
    /// over the trusted (TLS) manager channel and verifies the downloaded self-update artifact against it
    /// before replacing the running exe, so a tampered download cannot be installed.
    /// </summary>
    public string? AvailableSha256 { get; set; }

    /// <summary>True when a built launcher exe is available for download.</summary>
    public bool DownloadAvailable { get; set; }
}

/// <summary>
/// The launcher version a single stack currently serves for download, compared against the version the
/// manager most recently built. Used by the launcher admin page to verify the built launcher actually
/// propagated to every stack, and to offer a re-send for any stack that is stale or unreachable.
/// </summary>
public sealed class LauncherStackVersionDto
{
    public string StackId { get; set; } = string.Empty;

    public string StackName { get; set; } = string.Empty;

    /// <summary>The stack's own client-container base URL (<c>http://host:clientPort</c>), or null.</summary>
    public string? PortalUrl { get; set; }

    /// <summary>The launcher version this stack currently serves (its <c>/launcher/latest</c>), or null
    /// when the stack is unreachable or has no launcher yet.</summary>
    public string? DeployedVersion { get; set; }

    /// <summary>True when the stack's <c>/launcher/latest</c> responded (regardless of version).</summary>
    public bool Reachable { get; set; }

    /// <summary>True when the stack serves the same version the manager most recently built.</summary>
    public bool UpToDate { get; set; }

    /// <summary>Whether this stack is a selectable launcher profile (informational).</summary>
    public bool LauncherVisible { get; set; }
}

/// <summary>
/// Snapshot of launcher-build propagation across all client-enabled stacks: the version the manager
/// last built plus each stack's currently-served version.
/// </summary>
public sealed class LauncherPropagationDto
{
    /// <summary>The version the manager most recently built, or null when nothing has been built yet.</summary>
    public string? BuiltVersion { get; set; }

    public List<LauncherStackVersionDto> Stacks { get; set; } = new();
}
