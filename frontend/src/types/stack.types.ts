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
}

export interface NetworkInfoDto {
  addresses: string[]
  suggestedRealmlistHost: string
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

export interface RemoteConnectionTestResultDto {
  success: boolean
  message: string
  serverVersion?: string
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
  /** False until the first worldserver build completes successfully. */
  hasCompletedBuild?: boolean
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

export type StackJobAction = 'Start' | 'StartDatabase' | 'Stop' | 'Restart'
export type StackJobPhase =
  | 'Starting'
  | 'StartingDatabase'
  | 'Stopping'
  | 'Restarting'
  | 'Completed'
  | 'Failed'

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
