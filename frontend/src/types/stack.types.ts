// Enums
export enum ServerType {
  Standard = 'Standard',
  Playerbots = 'Playerbots',
  IndividualProgression = 'IndividualProgression',
  NpcBots = 'NpcBots',
  Custom = 'Custom',
}

/** Wizard-facing server-type description sourced from the backend server-type catalog. */
export interface ServerTypeInfoDto {
  id: ServerType
  displayName: string
  description: string
  /** Icon key mapped to a lucide icon in the UI (e.g. server, bot, trending-up, users). */
  icon: string
  coreRepositoryUrl: string
  coreBranch: string
  /** When true, the wizard prompts for a repository URL + branch to build from. */
  allowCustomRepository: boolean
  /** Modules auto-selected and locked for this server type. */
  requiredModuleIds?: string[]
}

/** User-supplied AzerothCore fork used when the selected server type allows a custom repository. */
export interface CustomForkConfigDto {
  repositoryUrl: string
  branch: string
}

export enum BuildPhase {
  Cloning = 'Cloning',
  PreparingModules = 'PreparingModules',
  Building = 'Building',
  CreatingImages = 'CreatingImages',
  Completed = 'Completed',
  Failed = 'Failed',
}

export enum StackStatus {
  Building = 'Building',
  Stopped = 'Stopped',
  Initializing = 'Initializing',
  Starting = 'Starting',
  Degraded = 'Degraded',
  Running = 'Running',
  Failed = 'Failed',
  SetupIncomplete = 'SetupIncomplete',
}

// Configuration DTOs
export interface DatabaseConfigDto {
  rootPassword: string
  port: number
}

export interface PortConfigDto {
  authServer: number
  worldServer: number
  soapPort: number
}

export interface AdvancedConfigDto {
  maxPlayers: number
  realmName: string
  realmlistHost?: string
  /** Legacy flat env vars (mirrors the worldserver bucket). Prefer serviceEnvVars. */
  customEnvVars?: Record<string, string>
  /** Per-service env vars: serviceId -> (envVarName -> value). */
  serviceEnvVars?: Record<string, Record<string, string>>
}

export type SmtpSecurityMode = 'none' | 'starttls' | 'tls'

export interface ArmoryEmailConfigDto {
  smtpHost: string
  smtpPort: number
  smtpSecurity: SmtpSecurityMode
  smtpUsername: string
  /** Blank on read means unchanged on update. */
  smtpPassword: string
  fromAddress: string
  fromName: string
  verificationSubject: string
  verificationBodyHtml: string
}

export interface ArmoryAccountsConfigDto {
  useEmailConfirmation: boolean
  emailConfigured: boolean
  email?: ArmoryEmailConfigDto | null
}

export interface ArmoryAccountsStatusDto {
  pendingRegistrationCount: number
  pendingTableAvailable: boolean
}

export interface ArmoryTestEmailResultDto {
  success: boolean
  message: string
}

export enum DeploymentTarget {
  Local = 'Local',
  External = 'External',
}

export interface DeploymentConfigDto {
  target: DeploymentTarget
  externalHost: string
  externalSshPort: number
  externalSshUser: string
  externalSshPrivateKey: string
  savedSshKeyId?: string
  saveSshKeyToVault?: boolean
  saveSshKeyLabel?: string
  cloudConnectionId?: string
  cloudInstanceId?: string
  cloudRegion?: string
  cloudProvider?: string
  cloudInstanceType?: string
}

export interface CloudSshKeyDto {
  id: string
  label: string
  fingerprint: string
  defaultSshUser: string
  createdAtUtc: string
}

export interface CloudSshKeyExportDto {
  id: string
  label: string
  fingerprint: string
  defaultSshUser: string
  privateKey: string
}

export interface CreateCloudSshKeyRequestDto {
  label: string
  privateKey: string
  defaultSshUser: string
}

export enum CloudProvider {
  DigitalOcean = 'DigitalOcean',
  Aws = 'Aws',
  Gcp = 'Gcp',
  Azure = 'Azure',
  Hetzner = 'Hetzner',
  Vultr = 'Vultr',
}

export enum CloudAuthMethod {
  Manual = 'Manual',
  OAuth = 'OAuth',
  AssumedRole = 'AssumedRole',
}

export enum CloudLoginMode {
  OAuth = 'OAuth',
  DeviceCode = 'DeviceCode',
  GuidedToken = 'GuidedToken',
  ManualOnly = 'ManualOnly',
  AssumedRole = 'AssumedRole',
}

export interface CloudProviderConnectionDto {
  id: string
  provider: CloudProvider
  label: string
  defaultRegion?: string
  defaultProjectId?: string
  createdAtUtc: string
  authMethod?: CloudAuthMethod
  accountHint?: string
  tokenExpiresAtUtc?: string
  needsReauth?: boolean
}

export interface CloudConnectionVerifyResultDto {
  ok: boolean
  message: string
  connection: CloudProviderConnectionDto
}

export interface CloudAuthProviderStatusDto {
  provider: CloudProvider
  loginMode: CloudLoginMode
  isConfigured: boolean
  isImplemented: boolean
  supportsPkce: boolean
  signInLabel: string
  unavailableReason: string
}

export interface CloudAuthStartRequestDto {
  returnUrl?: string
  reconnectConnectionId?: string
  label?: string
  policyTier?: string
  externalId?: string
  useDeviceCode?: boolean
}

export interface CloudAuthAwsTemplateDto {
  policyTier: string
  label: string
  description: string
  cloudFormationYaml: string
}

export interface CloudAuthStartResultDto {
  authorizationUrl?: string
  state?: string
  deviceCode?: string
  verificationUri?: string
  userCode?: string
  intervalSeconds?: number
  requiresManualCredentials?: boolean
  message?: string
  externalId?: string
  cloudFormationConsoleUrl?: string
  awsTemplates?: CloudAuthAwsTemplateDto[]
}

export interface CloudAuthCompleteRequestDto {
  roleArn?: string
  externalId?: string
  label?: string
  reconnectConnectionId?: string
  defaultRegion?: string
  defaultProjectId?: string
  deviceCode?: string
  accessToken?: string
}

export interface CloudInstanceSetupDialogDto {
  connectionId: string
  provider: CloudProvider
  label: string
  authMethod: CloudAuthMethod
  accountHint?: string
  canList: boolean
  canCreate: boolean
  canBootstrapExisting: boolean
  canSyncFirewall: boolean
  autoFirewallDefault: boolean
  suggestedAdminCidr?: string
  launchDefaults?: CloudLaunchDefaultsDto
  defaultProjectId?: string
  projects?: CloudLaunchCatalogOptionDto[]
}

export interface CreateCloudProviderConnectionRequestDto {
  provider: CloudProvider
  label: string
  accessToken?: string
  accessKeyId?: string
  secretAccessKey?: string
  serviceAccountJson?: string
  azureTenantId?: string
  azureClientId?: string
  azureClientSecret?: string
  azureSubscriptionId?: string
  defaultRegion?: string
}

export interface CloudInstanceDto {
  id: string
  provider: CloudProvider
  name: string
  region: string
  state: string
  publicHost: string
  suggestedSshUser: string
  image: string
  instanceType?: string
}

export enum CloudLaunchMode {
  Create = 'Create',
  BootstrapExisting = 'BootstrapExisting',
}

export interface CloudLaunchRequestDto {
  mode: CloudLaunchMode
  name: string
  sshUser: string
  region?: string
  instanceId?: string
  size?: string
  image?: string
  savedSshKeyId?: string
  generateSshKey?: boolean
  applyNetworkProfile?: boolean
  adminSourceCidr?: string
}

export interface CloudLaunchResultDto {
  instance: CloudInstanceDto
  savedSshKeyId?: string
  privateKeyPem?: string
  message: string
  bootstrapCommandId?: string
}

export interface CloudFirewallProbeResultDto {
  success: boolean
  message: string
  checks: RemotePrerequisiteCheckDto[]
}

export interface CloudLaunchDefaultsDto {
  provider: CloudProvider
  region: string
  size: string
  image: string
  sshUser: string
  supportsCreate: boolean
  supportsBootstrapExisting: boolean
}

export interface CloudLaunchCatalogOptionDto {
  value: string
  label: string
  description?: string
}

export interface CloudLaunchCatalogDto {
  provider: CloudProvider
  regions: CloudLaunchCatalogOptionDto[]
  sizes: CloudLaunchCatalogOptionDto[]
  images: CloudLaunchCatalogOptionDto[]
}

export interface CloudAuditLogDto {
  id: string
  occurredAtUtc: string
  actor: string
  eventType: string
  resourceType: string
  resourceId?: string
  summary: string
  metadataJson?: string
}

export interface SyncCloudSecurityGroupRequestDto {
  connectionId: string
  adminSourceCidr: string
  instanceId?: string
  region?: string
}

export interface CloudFirewallApplyResultDto {
  success: boolean
  message: string
  provider: CloudProvider
  rulesApplied: number
  rulesSkipped: number
  securityGroupIds: string[]
}

export interface StackConfigurationDto {
  stackName: string
  serverType: ServerType
  moduleIds: string[]
  database: DatabaseConfigDto
  ports: PortConfigDto
  advanced: AdvancedConfigDto
  deployment?: DeploymentConfigDto
  customFork?: CustomForkConfigDto
  armoryAccounts?: ArmoryAccountsConfigDto
  /** Completes this unfinished VPC draft instead of creating a new stack. */
  draftStackId?: string
}

export interface NetworkInfoDto {
  addresses: string[]
  suggestedRealmlistHost: string
  /** Client IP as CIDR for cloud SSH rules, e.g. 203.0.113.10/32 */
  suggestedAdminSourceCidr?: string
}

export interface ArmoryNetworkConfig {
  armoryPort: number
  clientPort: number
  /** Bind interface override: '' = inherit default, '0.0.0.0' = all interfaces, or a specific IP. */
  bindAddress: string
  /** Read-only: the interface actually used after policy is applied. */
  effectiveBindAddress: string
  /** Read-only: local vs external deployment. */
  isLocalDeployment: boolean
  /** Read-only: whether the armory container is currently running. */
  armoryRunning: boolean
}

export interface RemotePrerequisiteCheckDto {
  name: string
  passed: boolean
  message: string
}

export enum RemoteConnectionTestPhase {
  Full = 0,
  SshOnly = 1,
  PrerequisitesOnly = 2,
}

export interface RemoteConnectionTestRequestDto {
  deployment: DeploymentConfigDto
  phase?: RemoteConnectionTestPhase
}

export interface RemoteConnectionTestResultDto {
  success: boolean
  message: string
  serverVersion?: string
  prerequisites?: RemotePrerequisiteCheckDto[]
}

export interface RemoteSetupResultDto {
  success: boolean
  message: string
  serverVersion?: string
  steps?: RemotePrerequisiteCheckDto[]
}

export interface RemoteBootstrapResultDto {
  success: boolean
  message: string
  output?: string
  dockerVersion?: string
}

export enum RemoteHostOs {
  Linux = 0,
  Windows = 1,
}

export interface RemoteSetupOptionsDto {
  remoteOs: RemoteHostOs
  enableHostFirewall: boolean
  enableUnattendedUpgrades: boolean
  authServerPort: number
  worldServerPort: number
  armoryPort: number
  clientPort: number
  sshPort: number
}

export interface RemoteProvisionRequestDto {
  deployment: DeploymentConfigDto
  options: RemoteSetupOptionsDto
}

export interface VpcSecurityRoleDto {
  id: string
  name: string
  description: string
  exposure: string
  hostFirewall: boolean
  cloudSecurityGroup: boolean
  dockerHandlesBind: boolean
  adminSettingsLocation: string
  defaultPorts: number[]
}

export interface VpcSecurityCatalogDto {
  roles: VpcSecurityRoleDto[]
}

export interface VpcLaunchUserDataDto {
  sshUser: string
  script: string
  instructions: string
}

export interface VpcSecurityRuleDto {
  roleId: string
  port: number
  protocol: string
  action: string
  source: string
  description: string
}

export interface VpcSecurityProfileDto {
  host: string
  hostFirewallRules: VpcSecurityRuleDto[]
  cloudSecurityGroupRules: VpcSecurityRuleDto[]
  deniedPorts: VpcSecurityRuleDto[]
  notes: string
}

export type VpcSecurityCheckStatus = 'ok' | 'warning' | 'error' | 'unknown' | 'not-applicable'

export interface VpcSecurityCheckDto {
  category: string
  name: string
  roleId: string
  port?: number
  status: VpcSecurityCheckStatus
  message: string
}

export interface VpcFirewallStatusDto {
  overallHealthy: boolean
  message: string
  ufwInstalled: boolean
  ufwActive: boolean
  ufwStatusSummary?: string
  checks: VpcSecurityCheckDto[]
}

export type VpcSshLogEventType = 'accepted' | 'failed' | 'invalid-user' | 'closed'

export interface VpcSshLogEntryDto {
  timestamp?: string
  eventType: VpcSshLogEventType
  username?: string
  sourceIp?: string
  rawLine: string
}

export interface VpcSshLogsDto {
  success: boolean
  message: string
  logSource?: string
  entries: VpcSshLogEntryDto[]
}

// Build DTOs
export interface BuildStatusDto {
  buildId: string
  currentPhase: BuildPhase
  progressPercent: number
  currentStep: string
  recentLogs: string[]
  startedAt: string
  estimatedCompletion?: string
  errorMessage?: string
}

// Stack DTOs
export interface ContainerStatusDto {
  name: string
  service?: string
  status: string
  health: string
  startedAt: string
}

/** A manageable compose service of a stack with its current runtime state. */
export interface StackServiceDto {
  /** Compose service name used for lifecycle commands, e.g. "ac-worldserver". */
  service: string
  /** Friendly label, e.g. "World Server". */
  displayName: string
  /** Actual container name when it exists, else empty. */
  containerName: string
  /** "running" | "exited" | "created" | "restarting" | "dead" | "absent" | ... */
  state: string
  health: string
  startedAt?: string | null
  /** "core" | "armory" | "init" | "utility" */
  category: string
}

/** Per-service lifecycle actions exposed by the API. */
export type StackServiceAction = 'start' | 'stop' | 'restart' | 'recreate'

export interface ModuleVersionStatusDto {
  moduleId: string
  moduleName: string
  isOutdated: boolean
  currentCommitSha?: string
  latestCommitSha?: string
}

export interface DiscoveredStackDto {
  stackId: string
  suggestedName: string
  inferredServerType: ServerType
  currentStatus: StackStatus
  databasePort: number
  authServerPort: number
  worldServerPort: number
  soapPort: number
  isOrphaned: boolean
  containerNames: string[]
  coreRepositoryUrl?: string
  coreBranch?: string
  coreCommitSha?: string
  discoveredAt: string
  discoveredModules?: string[]
  discoveredDatabasePassword?: string
  discoveredSoapUsername?: string
  discoveredSoapPassword?: string
  discoveredEnvVars?: Record<string, string>
}

export interface ImportStackRequestDto {
  stackName: string
  databaseRootPassword?: string
  soapUsername?: string
  soapPassword?: string
}

export interface CiCheckDto {
  name: string
  status: string
  conclusion?: string
  htmlUrl?: string
}

export interface CiBuildStatusDto {
  status: string // "success", "failure", "pending", "unknown"
  criticalChecks: CiCheckDto[]
  checkedAt: string
  totalChecks: number
  passedChecks: number
  failedChecks: number
}

export interface StackUpdateStatusDto {
  stackId: string
  hasUpdates: boolean
  isCoreOutdated: boolean
  outdatedModuleCount: number
  currentCoreSha?: string
  latestCoreSha?: string
  outdatedModules: ModuleVersionStatusDto[]
  lastCheckedAt?: string
  latestCoreBuildStatus?: CiBuildStatusDto
  // True when the generated runtime config (.env / docker-compose.override.yml) predates the current
  // manager template and the stack should be re-applied (restart/re-apply) to pick up current fixes.
  isRuntimeConfigOutdated: boolean
  runtimeArtifactVersion: number
  requiredRuntimeArtifactVersion: number
}

export interface StackDetailsDto {
  stackId: string
  stackName: string
  displayName?: string
  serverType: ServerType
  status: StackStatus
  containers: ContainerStatusDto[]
  services: StackServiceDto[]
  configuration: StackConfigurationDto
  createdAt: string
  updateStatus?: StackUpdateStatusDto
  isAdminAccountInitialized: boolean
  adminAccountInitializedAt?: string
  armoryPort: number
  armoryRunning: boolean
  /** Module IDs saved on the stack but not yet compiled into the worldserver build. */
  modulesPendingRebuild?: string[]
  needsExternalReconnect?: boolean
  externalReconnectReason?: string | null
  /** Null when runtime was not probed; false when the stack's Docker engine is down or unreachable. */
  dockerEngineAvailable?: boolean | null
  dockerEngineUnavailableReason?: string | null
  /** False until the first worldserver build completes successfully. */
  hasCompletedBuild?: boolean
  /** Wizard step to resume when status is SetupIncomplete. */
  wizardStepId?: string | null
  /** When SSH hardening last succeeded (root / image-default users locked out of internet SSH). */
  sshHardeningCompletedAt?: string | null
}

export interface StackSetupDraftRequestDto {
  stackId?: string
  wizardStepId: string
  wizardDraftJson: string
  stackName?: string
  deployment: DeploymentConfigDto
}

export interface StackSetupDraftDto {
  stackId: string
  stackName: string
  wizardStepId: string
  wizardDraftJson: string
  externalSshPrivateKey: string
  deployment: DeploymentConfigDto
}

export interface SoapCredentialsDto {
  username: string
  password: string
}

export interface DatabaseCredentialsDto {
  username: string
  password: string
  port: number
}

export type ArmoryJobAction = 'Start' | 'Stop' | 'Restart' | 'Rebuild' | 'SyncDbc'
export type ArmoryJobPhase =
  | 'Starting'
  | 'Stopping'
  | 'Restarting'
  | 'Rebuilding'
  | 'SyncingDbc'
  | 'Completed'
  | 'Failed'

/** Status of the detached armory background job for a stack (survives page refreshes). */
export interface ArmoryJobStatus {
  stackId: string
  jobId: string
  action: ArmoryJobAction
  phase: ArmoryJobPhase
  message: string
  /** Timestamped progress log lines for the running/last operation (live + reattached after refresh). */
  recentLogs: string[]
  error?: string | null
  success?: boolean | null
  startedAt: string
  finishedAt?: string | null
  isRunning: boolean
}

export type ClientJobAction = 'Start' | 'Stop' | 'Restart' | 'Recreate'
export type ClientJobPhase =
  | 'Starting'
  | 'Stopping'
  | 'Restarting'
  | 'Recreating'
  | 'Completed'
  | 'Failed'

/** Status of the detached client file-server background job for a stack (survives page refreshes). */
export interface ClientJobStatus {
  stackId: string
  jobId: string
  action: ClientJobAction
  phase: ClientJobPhase
  message: string
  error?: string | null
  success?: boolean | null
  startedAt: string
  finishedAt?: string | null
  isRunning: boolean
}

export type StackJobAction = 'Start' | 'StartDatabase' | 'Stop' | 'Restart' | 'ApplyPublicHost'
export type StackJobPhase =
  | 'Starting'
  | 'StartingDatabase'
  | 'Stopping'
  | 'Restarting'
  | 'ApplyingPublicHost'
  | 'Completed'
  | 'Failed'

export type PublicHostApplyStepStatus = 'Pending' | 'Running' | 'Completed' | 'Skipped' | 'Failed'

export interface PublicHostApplyStep {
  id: string
  label: string
  status: PublicHostApplyStepStatus
  detail?: string | null
}

/** Status of the detached stack lifecycle background job (survives navigating away / refreshes). */
export interface StackJobStatus {
  stackId: string
  jobId: string
  action: StackJobAction
  phase: StackJobPhase
  message: string
  error?: string | null
  success?: boolean | null
  startedAt: string
  finishedAt?: string | null
  isRunning: boolean
  steps?: PublicHostApplyStep[]
}

export interface InitializeAdminResponseDto {
  success: boolean
  created: boolean
  message: string
  username?: string
  password?: string
}

// Module DTO
export interface ModuleDto {
  id: string
  name: string
  description: string
  repository: string
  branch: string
  // "git" (cloned from a repo) or "package" (uploaded .zip).
  sourceType?: string
  // True for modules defined in code (read-only); false for custom catalog modules.
  isBuiltIn?: boolean
  // Recommended modules are sorted first and highlighted in module pickers.
  recommended?: boolean
  // Other module ids that must be selected when this module is selected.
  requiredModuleIds?: string[]
}

export interface CommunityModuleDto {
  id: string
  name: string
  description: string
  repository: string
  branch: string
  stars: number
  forks: number
  updatedAt?: string | null
  inPlatformCatalog: boolean
  isBuiltIn: boolean
}

export interface CommunityModuleListResult {
  items: CommunityModuleDto[]
  total: number
  page: number
  pageSize: number
}

// Payload for creating/updating a git-sourced custom catalog module
export interface SaveModuleRequest {
  id: string
  name: string
  description: string
  repository: string
  branch: string
}

// A module's README (rendered as markdown)
export interface ModuleReadmeDto {
  moduleId: string
  found: boolean
  content: string
  baseUrl?: string | null
}

export interface ValidationResultDto {
  isValid: boolean
  errors: ValidationError[]
  suggestedPorts: SuggestedPorts
}

export interface ValidationError {
  field: string
  message: string
}

export type PortFieldPath =
  | 'database.port'
  | 'ports.authServer'
  | 'ports.worldServer'
  | 'ports.soapPort'

export type SuggestedPorts = Partial<Record<PortFieldPath, number>>
