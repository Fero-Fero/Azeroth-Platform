namespace AzerothPlatform.Infrastructure.Data.Entities;

/// <summary>
/// A user-added module in the module catalog. Built-in modules are defined in code and are not
/// stored here; only custom modules added through the admin UI are persisted.
/// </summary>
public class CatalogModuleEntity
{
    /// <summary>Unique module identifier (e.g. <c>mod-foo</c>). Used as the clone folder name.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Source of the module: "git" (cloned) or "package" (uploaded .zip).</summary>
    public string SourceType { get; set; } = "git";

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Git repository URL cloned into azerothcore-wotlk/modules at build time.</summary>
    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = "master";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
