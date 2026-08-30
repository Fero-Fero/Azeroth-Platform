namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Request to clone or pull git modules for a stack without starting a full rebuild.
/// </summary>
public class SyncStackModulesRequestDto
{
    /// <summary>
    /// When set, only this catalog module is synced. When omitted, every git module selected on the stack is synced.
    /// </summary>
    public string? ModuleId { get; set; }
}

/// <summary>
/// Per-module result of a GitHub sync.
/// </summary>
public class SyncStackModuleItemDto
{
    public string ModuleId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Ok { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? CommitSha { get; set; }

    /// <summary>True when the module was cloned because it was missing from the build tree.</summary>
    public bool Cloned { get; set; }

    /// <summary>True when GitHub sync does not apply (uploaded package, missing catalog entry, etc.).</summary>
    public bool Skipped { get; set; }
}

/// <summary>
/// Result of syncing one or more stack modules from GitHub.
/// </summary>
public class SyncStackModulesResultDto
{
    public List<SyncStackModuleItemDto> Items { get; set; } = new();
}
