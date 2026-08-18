using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Data.Entities;

/// <summary>
/// Persisted representation of a managed AzerothCore stack.
/// </summary>
public class ManagedStackEntity
{
    public string Id { get; set; } = string.Empty;

    public string StackName { get; set; } = string.Empty;

    public string NormalizedStackName { get; set; } = string.Empty;

    public ServerType ServerType { get; set; }

    public StackStatus Status { get; set; }

    public string ModuleIdsJson { get; set; } = "[]";

    public string DatabaseRootPassword { get; set; } = string.Empty;

    public int DatabasePort { get; set; }

    public int AuthServerPort { get; set; }

    public int WorldServerPort { get; set; }

    public int SoapPort { get; set; }

    /// <summary>Host port the per-stack armory (frontend-armory) container is published on.</summary>
    public int ArmoryPort { get; set; }

    /// <summary>Whether the per-stack armory container is currently intended to be running.</summary>
    public bool ArmoryEnabled { get; set; }

    /// <summary>
    /// Host port the per-stack client-server (azeroth-platform-client) container is published on. The
    /// launcher fetches this stack's manifest + files from here.
    /// </summary>
    public int ClientPort { get; set; }

    /// <summary>Whether this stack runs a client-server container that serves client files to launchers.</summary>
    public bool ClientEnabled { get; set; }

    /// <summary>
    /// Host interface the player-facing HTTP services (armory + client file server) are published on.
    /// Blank inherits the manager default (<c>Docker:PublishBindAddress</c>, loopback for local stacks;
    /// all-interfaces for external). Set to <c>0.0.0.0</c> (all interfaces) or a specific IP to expose the
    /// armory beyond localhost - e.g. so a LAN/VPC/remote machine can reach it - without hand-editing the
    /// generated <c>.env</c>. Applied on the next armory/client recreate.
    /// </summary>
    public string PublishBindAddress { get; set; } = string.Empty;

    /// <summary>
    /// Random secret used to sign the armory's player session cookies (HS256 JWT). Generated once per
    /// stack and persisted so it survives restarts/override regeneration. Kept independent of the DB
    /// root password so that knowledge of the password alone cannot be used to forge session tokens.
    /// </summary>
    public string ArmorySessionSecret { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted password for the stack-scoped <c>acore_armory</c> MySQL user (least-privilege armory DB access).
    /// Generated on first armory deploy; blank until then.
    /// </summary>
    public string ArmoryDatabasePasswordProtected { get; set; } = string.Empty;

    public int MaxPlayers { get; set; }

    public string RealmName { get; set; } = string.Empty;

    /// <summary>
    /// Per-service environment variables (JSON <c>{serviceId:{key:value}}</c>, e.g. worldserver,
    /// authserver, armory, client). Environment variables are per-container, so each service owns its
    /// own bucket which the override generator injects into that service's <c>environment:</c> block.
    /// </summary>
    public string ServiceEnvVarsJson { get; set; } = "{}";
    
    public string SoapUsername { get; set; } = "admin";
    
    public string SoapPassword { get; set; } = "admin";

    public DateTime CreatedAt { get; set; }
    
    // ===== Version Tracking (captured at build time) =====
    public string CoreRepositoryUrl { get; set; } = string.Empty;
    
    public string CoreBranch { get; set; } = string.Empty;
    
    public string CoreCommitSha { get; set; } = string.Empty;
    
    public DateTime? LastBuiltAt { get; set; }
    
    public string ModuleVersionsJson { get; set; } = "[]";

    /// <summary>
    /// Version of the runtime-artifact template (.env / docker-compose.override.yml) the manager last
    /// generated for this stack. Compared against <see cref="Services.RuntimeArtifactTemplate.CurrentVersion"/>
    /// to detect "deployment drift": a lower value means the on-disk artifacts predate current fixes and
    /// the stack should be re-applied. Starts at 0 until runtime artifacts are generated.
    /// </summary>
    public int RuntimeArtifactVersion { get; set; }
    
    // ===== Update Status (cached by background service) =====
    public bool IsOutdated { get; set; }
    
    public bool IsCoreOutdated { get; set; }
    
    public int OutdatedModuleCount { get; set; }
    
    public string? LatestAvailableCoreSha { get; set; }
    
    public string OutdatedModulesJson { get; set; } = "[]";
    
    public DateTime? LastUpdateCheckAt { get; set; }
    
    // ===== CI/CD Build Status (cached with update checks) =====
    /// <summary>
    /// CI build status for the latest available core version: "success", "failure", "pending", "unknown"
    /// </summary>
    public string? LatestCoreBuildStatus { get; set; }
    
    /// <summary>
    /// JSON array of critical CI check results for latest core version
    /// </summary>
    public string? LatestCoreBuildChecksJson { get; set; }
    
    /// <summary>
    /// When the CI build status was last checked
    /// </summary>
    public DateTime? LatestCoreBuildStatusCheckedAt { get; set; }
    
    // ===== SOAP Admin Account =====
    /// <summary>
    /// Whether the SOAP admin account has been initialized in the database
    /// </summary>
    public bool IsAdminAccountInitialized { get; set; }
    
    /// <summary>
    /// When the admin account was initialized
    /// </summary>
    public DateTime? AdminAccountInitializedAt { get; set; }

    // ===== Migrations / Patches =====
    /// <summary>
    /// Highest applied patch level (numeric prefix of the patch folder). 0 means none applied.
    /// Patches must be applied incrementally from lowest to next-lowest.
    /// </summary>
    public int AppliedPatchLevel { get; set; }

    /// <summary>
    /// JSON array of applied patches: [{ "key": "1_classic", "level": 1, "appliedAt": "..." }]
    /// </summary>
    public string AppliedPatchesJson { get; set; } = "[]";

    // ===== Patch apply lock (cross-user / cross-machine) =====
    /// <summary>
    /// Key of the patch currently being applied ("*" for reapply-all), or null when idle. Acts as a
    /// DB-backed lock so a second operator on another machine cannot start a concurrent apply.
    /// </summary>
    public string? ApplyingPatchKey { get; set; }

    /// <summary>Identifier of the in-flight apply run, used to release the lock atomically.</summary>
    public string? ApplyRunId { get; set; }

    /// <summary>When the current apply started (UTC). Used for stale-lock recovery after a crash.</summary>
    public DateTime? ApplyStartedAt { get; set; }

    // ===== Build orchestration =====
    /// <summary>
    /// Action the build pipeline runs after the next successful build completes. Set to
    /// <see cref="PostBuildAction.SnapshotReapplyStart"/> by the Update action; cleared afterwards.
    /// </summary>
    public PostBuildAction PostBuildAction { get; set; } = PostBuildAction.None;

    /// <summary>
    /// How the next build should reconcile the operator's existing server .conf files with the freshly
    /// regenerated configs. Set by the Update/Rebuild actions and cleared after the build applies it.
    /// </summary>
    public ConfigMigrationMode ConfigMigrationMode { get; set; } = ConfigMigrationMode.Skip;

    // ===== Launcher profile (multi-profile client) =====
    /// <summary>Whether this stack appears as a selectable profile in the desktop launcher.</summary>
    public bool LauncherVisible { get; set; }

    /// <summary>Profile display name shown in the launcher dropdown (blank falls back to realm/stack name).</summary>
    public string LauncherDisplayName { get; set; } = string.Empty;

    /// <summary>Short description shown for the profile in the launcher.</summary>
    public string LauncherDescription { get; set; } = string.Empty;

    /// <summary>Sort order of the profile in the launcher dropdown (ascending).</summary>
    public int LauncherSortOrder { get; set; }

    /// <summary>
    /// Canonical realmlist host for this stack. Written into realmlist.wtf served to launchers AND
    /// into the acore_auth.realmlist DB row so the auth server redirects clients to the right world
    /// address. Blank falls back to the global default (Migrations:RealmlistHost). (Historically named
    /// "override"; now the single source of truth per stack.)
    /// </summary>
    public string RealmlistHostOverride { get; set; } = string.Empty;

    /// <summary>Per-stack launcher style template override; blank inherits the global template.</summary>
    public string LauncherTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Informational WoW client version label shown in the launcher for this stack (e.g.
    /// <c>3.3.5a (12340)</c>). Blank falls back to the global launcher client-version default.
    /// </summary>
    public string LauncherClientVersion { get; set; } = string.Empty;

    // ===== Deployment target (local vs external remote Docker host) =====
    /// <summary>Where this stack's containers run: local Docker engine or a remote host over SSH.</summary>
    public DeploymentTarget DeploymentTarget { get; set; } = DeploymentTarget.Local;

    /// <summary>OS family of the external Docker host. Local stacks ignore this (always the manager).</summary>
    public RemoteHostOs RemoteOs { get; set; } = RemoteHostOs.Linux;

    /// <summary>Remote host (IP/DNS) of the external Docker engine (External stacks only).</summary>
    public string ExternalHost { get; set; } = string.Empty;

    /// <summary>SSH port on the remote host (default 22).</summary>
    public int ExternalSshPort { get; set; } = 22;

    /// <summary>SSH username for the remote host.</summary>
    public string ExternalSshUser { get; set; } = string.Empty;

    /// <summary>PEM-encoded SSH private key for the remote host (stored at rest; encryption is a follow-up).</summary>
    public string ExternalSshPrivateKey { get; set; } = string.Empty;

    /// <summary>Linked cloud account used to launch or pick this stack's VM (External stacks only).</summary>
    public string CloudConnectionId { get; set; } = string.Empty;

    /// <summary>Provider instance id (EC2 i-..., droplet id, etc.) for terminate.</summary>
    public string CloudInstanceId { get; set; } = string.Empty;

    /// <summary>Provider region or zone of <see cref="CloudInstanceId"/>.</summary>
    public string CloudRegion { get; set; } = string.Empty;

    /// <summary>Cloud provider of the bound VM (Aws, DigitalOcean, …). Blank for local stacks.</summary>
    public string CloudProvider { get; set; } = string.Empty;

    /// <summary>Provider instance type / size (t3.micro, s-2vcpu-2gb, cx22, …).</summary>
    public string CloudInstanceType { get; set; } = string.Empty;

    /// <summary>JSON snapshot of the unfinished create-stack wizard (no SSH private key).</summary>
    public string WizardDraftJson { get; set; } = string.Empty;

    /// <summary>Wizard step id to resume (e.g. server-config). Blank when setup is complete.</summary>
    public string WizardStepId { get; set; } = string.Empty;

    /// <summary>When true, armory registration uses email verification before account activation.</summary>
    public bool ArmoryUseEmailConfirmation { get; set; }

    /// <summary>When <see cref="ArmoryUseEmailConfirmation"/> is true, whether SMTP settings are complete.</summary>
    public bool ArmoryEmailConfigured { get; set; }

    /// <summary>Non-secret armory email settings (JSON). Blank when email confirmation is off.</summary>
    public string ArmoryEmailConfigJson { get; set; } = string.Empty;

    /// <summary>Encrypted SMTP password for armory outbound mail. Blank until configured.</summary>
    public string ArmoryEmailSmtpPasswordProtected { get; set; } = string.Empty;

    /// <summary>
    /// When SSH hardening last succeeded (root / image-default users locked out of internet SSH).
    /// </summary>
    public DateTime? SshHardeningCompletedAt { get; set; }
}
