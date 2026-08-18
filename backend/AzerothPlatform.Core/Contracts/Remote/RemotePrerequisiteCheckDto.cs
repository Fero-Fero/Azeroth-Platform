namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Result of a single prerequisite probe on a remote Docker host.
/// </summary>
public class RemotePrerequisiteCheckDto
{
    /// <summary>Short label, e.g. "SSH", "Docker Engine", "Docker Compose".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this prerequisite passed.</summary>
    public bool Passed { get; set; }

    /// <summary>Human-readable detail or error message.</summary>
    public string Message { get; set; } = string.Empty;
}
