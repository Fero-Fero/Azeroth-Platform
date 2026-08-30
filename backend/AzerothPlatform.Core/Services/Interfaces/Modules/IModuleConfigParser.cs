namespace AzerothPlatform.Core.Services.Interfaces;

using AzerothPlatform.Core.Contracts;

/// <summary>
/// Parses module-specific configuration files and extracts configurable options.
/// Each module (playerbots, transmog, autobalance, etc.) has unique conf structure,
/// so module-specific parsers are needed rather than a generic parser.
/// </summary>
public interface IModuleConfigParser
{
    /// <summary>
    /// Gets the module name this parser handles (e.g., "playerbots", "transmog")
    /// </summary>
    string ModuleName { get; }

    /// <summary>
    /// Parses the module's configuration file content and extracts all configurable options.
    /// Returns options grouped by section for organization in UI.
    /// </summary>
    /// <param name="confFileContent">Raw content of the .conf.dist or .conf file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of configuration sections with their options</returns>
    Task<IEnumerable<ModuleConfigSectionDto>> ParseAsync(string confFileContent, CancellationToken cancellationToken = default);
}

/// <summary>
/// A section (tab) in the module configuration modal.
/// Example: "Summon Options", "Mount Settings", "Combat Settings"
/// </summary>
public class ModuleConfigSectionDto
{
    /// <summary>
    /// Section name for display (e.g., "Summon Options")
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// All configuration options in this section
    /// </summary>
    public required IEnumerable<ModuleConfigOption> Options { get; set; }
}
