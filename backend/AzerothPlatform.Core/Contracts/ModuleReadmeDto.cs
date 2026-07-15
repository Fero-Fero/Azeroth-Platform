namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// A module's README, rendered as Markdown on the client.
/// </summary>
public sealed class ModuleReadmeDto
{
    public string ModuleId { get; set; } = string.Empty;

    /// <summary>True when a README was found.</summary>
    public bool Found { get; set; }

    /// <summary>Raw Markdown content (empty when not found).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Base URL used to resolve relative links/images in the README (git modules only);
    /// null for uploaded packages.
    /// </summary>
    public string? BaseUrl { get; set; }
}
