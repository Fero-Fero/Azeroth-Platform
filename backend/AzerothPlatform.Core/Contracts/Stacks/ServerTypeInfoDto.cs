namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Wizard-facing description of a selectable server type. Sourced from the operator-editable
/// server-type catalog so the UI reflects configuration changes (enabling/disabling variants,
/// renaming, swapping repositories) without a code change.
/// </summary>
public sealed class ServerTypeInfoDto
{
    /// <summary>The <see cref="ServerType"/> value (serialized as its enum name, e.g. "Playerbots").</summary>
    public ServerType Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Icon key the frontend maps to an icon component.</summary>
    public string Icon { get; set; } = "server";

    /// <summary>Core repository this type is built from (informational, shown in the UI).</summary>
    public string CoreRepositoryUrl { get; set; } = string.Empty;

    /// <summary>Core branch this type is built from (informational).</summary>
    public string CoreBranch { get; set; } = string.Empty;

    /// <summary>When true, the wizard prompts for a repository URL + branch instead of using a fixed catalog repo.</summary>
    public bool AllowCustomRepository { get; set; }

    /// <summary>
    /// Modules that must be selected for this server type (auto-selected and locked in the wizard).
    /// </summary>
    public List<string> RequiredModuleIds { get; set; } = new();
}
