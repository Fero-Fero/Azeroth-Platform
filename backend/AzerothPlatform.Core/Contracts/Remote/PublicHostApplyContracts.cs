using System.Text.Json.Serialization;

namespace AzerothPlatform.Core.Contracts;

public enum PublicHostApplyStepStatus
{
    Pending,
    Running,
    Completed,
    Skipped,
    Failed
}

/// <summary>A single step while applying the stack public host / realmlist address.</summary>
public sealed class PublicHostApplyStepDto
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public PublicHostApplyStepStatus Status { get; set; } = PublicHostApplyStepStatus.Pending;
    public string? Detail { get; set; }
}

/// <summary>Response when saving a stack's player-facing host / realmlist address.</summary>
public sealed class SetRealmAddressResponseDto
{
    public string Host { get; set; } = string.Empty;

    /// <summary>Background job that applies the change to live containers when the stack is stopped.</summary>
    public StackJobStatusDto Job { get; set; } = new();
}

/// <summary>Captured at enqueue time so the job UI only lists steps that will actually run.</summary>
public sealed class PublicHostApplyPlanDto
{
    public bool WasFullyStopped { get; set; }

    public bool ClientEnabled { get; set; }

    public bool ArmoryEnabled { get; set; }

    public bool DatabaseRunning { get; set; }

    public bool AuthRunning { get; set; }

    public bool WorldRunning { get; set; }

    public bool ClientRunning { get; set; }

    public bool ArmoryRunning { get; set; }

    public bool ArmoryAssetsRunning { get; set; }
}
