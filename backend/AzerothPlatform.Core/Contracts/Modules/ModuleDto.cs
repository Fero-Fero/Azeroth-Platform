namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Where a catalog module's source comes from.
/// </summary>
public static class ModuleSource
{
    /// <summary>Cloned from a git repository at build time.</summary>
    public const string Git = "git";

    /// <summary>An uploaded package (.zip) stored by the manager and copied in at build time.</summary>
    public const string Package = "package";
}

/// <summary>
/// Module information for AzerothCore
/// </summary>
public class ModuleDto
{
    /// <summary>
    /// Source of the module: <see cref="ModuleSource.Git"/> or <see cref="ModuleSource.Package"/>.
    /// </summary>
    public string SourceType { get; set; } = ModuleSource.Git;

    /// <summary>
    /// Unique module identifier
    /// </summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>
    /// Display name
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Module description
    /// </summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// Git repository URL
    /// </summary>
    public string Repository { get; set; } = string.Empty;
    
    /// <summary>
    /// Git branch to clone
    /// </summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>
    /// True for modules defined in code (cannot be edited or deleted); false for custom
    /// modules added through the catalog admin.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// Whether this module is recommended for most stacks. Recommended modules are highlighted and sorted first.
    /// </summary>
    public bool Recommended { get; set; }

    /// <summary>
    /// Other module ids that must be selected when this module is selected.
    /// </summary>
    public List<string> RequiredModuleIds { get; set; } = new();
}
