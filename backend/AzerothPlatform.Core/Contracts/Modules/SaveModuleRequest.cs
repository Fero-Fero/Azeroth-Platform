namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Payload for creating or updating a custom catalog module.
/// </summary>
public class SaveModuleRequest
{
    /// <summary>
    /// Module identifier (used as the clone folder name). Required on create; ignored on update
    /// (the route id is authoritative). Allowed characters: letters, digits, '.', '_', '-'.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Git repository URL (http/https).</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>Git branch to clone. Defaults to "master" when empty.</summary>
    public string Branch { get; set; } = string.Empty;
}
