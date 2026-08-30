namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// A module entry from the AzerothCore community catalogue (GitHub topic metadata).
/// </summary>
public sealed class CommunityModuleDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = "master";

    public int Stars { get; set; }

    public int Forks { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>True when this module is already in the platform catalog (built-in or custom).</summary>
    public bool InPlatformCatalog { get; set; }

    /// <summary>True when the platform already ships this module as a built-in curated entry.</summary>
    public bool IsBuiltIn { get; set; }
}

public sealed class CommunityModuleListResult
{
    public IReadOnlyList<CommunityModuleDto> Items { get; set; } = [];

    public int Total { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }
}

public sealed class ImportCommunityModuleRequest
{
    /// <summary>GitHub repository URL (https://github.com/owner/repo).</summary>
    public string Repository { get; set; } = string.Empty;
}
