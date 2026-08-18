namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Complete details about a deployed stack
/// </summary>
public class StackDetailsDto
{
    /// <summary>
    /// Unique stack identifier
    /// </summary>
    public string StackId { get; set; } = string.Empty;
    
    /// <summary>
    /// Stack name
    /// </summary>
    public string StackName { get; set; } = string.Empty;

    /// <summary>
    /// Name shown in lists. Unfinished VPC drafts without a server name use "Unnamed instance".
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    
    /// <summary>
    /// Server type (Standard or Playerbots)
    /// </summary>
    public ServerType ServerType { get; set; }
    
    /// <summary>
    /// Current operational status
    /// </summary>
    public StackStatus Status { get; set; }
    
    /// <summary>
    /// Status of all containers in the stack
    /// </summary>
    public List<ContainerStatusDto> Containers { get; set; } = new();

    /// <summary>
    /// The stack's manageable services (database, auth/world servers, armory, plus any init/utility
    /// containers that exist), each with its current runtime state. Canonical services are always
    /// present even when stopped/absent so the UI can offer per-service controls.
    /// </summary>
    public List<StackServiceDto> Services { get; set; } = new();
    
    /// <summary>
    /// Stack configuration
    /// </summary>
    public StackConfigurationDto Configuration { get; set; } = new();
    
    /// <summary>
    /// Timestamp when stack was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// Update status for this stack (if available)
    /// </summary>
    public StackUpdateStatusDto? UpdateStatus { get; set; }
    
    /// <summary>
    /// Whether the SOAP admin account has been initialized
    /// </summary>
    public bool IsAdminAccountInitialized { get; set; }
    
    /// <summary>
    /// When the admin account was initialized
    /// </summary>
    public DateTime? AdminAccountInitializedAt { get; set; }

    /// <summary>
    /// Host port the per-stack armory (frontend-armory) container is published on.
    /// </summary>
    public int ArmoryPort { get; set; }

    /// <summary>
    /// Whether the armory container is currently running (derived from container state).
    /// </summary>
    public bool ArmoryRunning { get; set; }

    /// <summary>
    /// Module IDs saved on the stack but not yet present in the last worldserver build checkout.
    /// The operator must recompile before these modules (and their SQL/config) take effect.
    /// </summary>
    public List<string> ModulesPendingRebuild { get; set; } = new();

    /// <summary>
    /// True when this external stack's encrypted SSH key cannot be decrypted (e.g. secret-protection.key
    /// was lost after a data-volume prune). The operator must reconnect with a fresh private key.
    /// </summary>
    public bool NeedsExternalReconnect { get; set; }

    /// <summary>Explanation shown when <see cref="NeedsExternalReconnect"/> is true.</summary>
    public string? ExternalReconnectReason { get; set; }

    /// <summary>
    /// Whether the stack's Docker engine responded to a probe. Null when runtime was not probed
    /// (e.g. stack list). False when the daemon is stopped or unreachable.
    /// </summary>
    public bool? DockerEngineAvailable { get; set; }

    /// <summary>Explanation shown when <see cref="DockerEngineAvailable"/> is false.</summary>
    public string? DockerEngineUnavailableReason { get; set; }

    /// <summary>
    /// True after at least one worldserver build has completed successfully. When false, the stack
    /// detail UI stays on a setup/retry screen until the initial build succeeds. Failed later
    /// rebuilds do not clear this flag.
    /// </summary>
    public bool HasCompletedBuild { get; set; }

    /// <summary>Wizard step to resume when <see cref="Status"/> is <see cref="StackStatus.SetupIncomplete"/>.</summary>
    public string? WizardStepId { get; set; }

    /// <summary>When SSH hardening last succeeded (root / image-default users locked out of internet SSH).</summary>
    public DateTime? SshHardeningCompletedAt { get; set; }
}
