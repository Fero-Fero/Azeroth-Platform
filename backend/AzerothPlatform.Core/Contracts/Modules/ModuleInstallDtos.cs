namespace AzerothPlatform.Core.Contracts;

public enum ModuleInstallChoiceKind
{
    Exclusive = 0,
    Independent = 1
}

public sealed class ModuleInstallChoice
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public bool DefaultSelected { get; init; }
}

public sealed class ModuleInstallChoiceGroup
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public required ModuleInstallChoiceKind Kind { get; init; }
    public bool AllowNone { get; init; }
    public required IReadOnlyList<ModuleInstallChoice> Choices { get; init; }
}

public sealed class ModuleInstallChoicesDto
{
    public required string ModuleId { get; init; }
    public IReadOnlyList<ModuleInstallChoiceGroup> Groups { get; init; } = [];
}

public sealed class StackModuleInstallChoicesDto
{
    public IReadOnlyList<ModuleInstallChoicesDto> Modules { get; init; } = [];
    public ApplyModuleExtraDataRequest Saved { get; set; } = new();
    public ModuleExtraDataStackStatusDto Status { get; set; } = new();
}

/// <summary>Operator selections keyed by choice group id.</summary>
public sealed class ModuleInstallSelections
{
    public Dictionary<string, List<string>> Groups { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Exclusive(string groupId)
    {
        if (!Groups.TryGetValue(groupId, out var values) || values.Count == 0)
        {
            return null;
        }

        return values[0];
    }

    public bool IndependentContains(string groupId, string choiceId)
    {
        if (!Groups.TryGetValue(groupId, out var values))
        {
            return false;
        }

        return values.Any(value => string.Equals(value, choiceId, StringComparison.OrdinalIgnoreCase));
    }
}

public enum IpContentMode
{
    Unset = 0,
    Standard = 1,
    ServerWideProgression = 2
}

public sealed class ApplyModuleExtraDataRequest
{
    public IpContentMode IpContentMode { get; set; }

    public Dictionary<string, ModuleInstallSelections> SelectionsByModuleId { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModuleExtraDataStackStatusDto
{
    public IpContentMode IpContentMode { get; set; }
    public bool Prepared { get; set; }
    public bool Deposited { get; set; }
    public bool HasPendingDeposit { get; set; }
    public bool HasExtras { get; set; }
}

public enum ModuleInstallJobPhase
{
    Idle = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}

public sealed class ModuleInstallJobStatusDto
{
    public string? StackId { get; set; }
    public string JobId { get; set; } = string.Empty;
    public ModuleInstallJobPhase Phase { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Error { get; set; }
    public bool Success { get; set; }
    public bool IsRunning => Phase == ModuleInstallJobPhase.Running;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public List<string> RecentLogs { get; set; } = [];
}

public enum ModuleInstallArtifactKind
{
    DbcCsv = 0,
    DbcBase = 1,
    Mpq = 2,
    SqlWorld = 3,
    SqlAuth = 4,
    SqlCharacters = 5,
    Addon = 6,
    Lua = 7,
    Maps = 8
}

public sealed class ModuleInstallArtifact
{
    public required ModuleInstallArtifactKind Kind { get; init; }
    public required string SourcePath { get; init; }
    public string? DestHint { get; init; }
}

public sealed class WorldserverConfHint
{
    public required string Key { get; init; }
    public required string Value { get; init; }
}

public sealed class ModuleInstallContribution
{
    public List<ModuleInstallArtifact> Artifacts { get; init; } = [];
    public List<WorldserverConfHint> ConfHints { get; init; } = [];
}

public sealed class SessionBaseDbc
{
    public required string TableName { get; init; }
    public required string ModuleId { get; init; }
    public required string BinaryPath { get; init; }
}

/// <summary>Thrown when two modules contribute different rows for the same DBC id.</summary>
public sealed class ModuleDbcConflictException : InvalidOperationException
{
    public ModuleDbcConflictException(string moduleA, string moduleB, string table, string entryId)
        : base($"{moduleA} and {moduleB} are incompatible because they both modify entry {entryId} in {table}.dbc.")
    {
        ModuleA = moduleA;
        ModuleB = moduleB;
        Table = table;
        EntryId = entryId;
    }

    public string ModuleA { get; }
    public string ModuleB { get; }
    public string Table { get; }
    public string EntryId { get; }
}
