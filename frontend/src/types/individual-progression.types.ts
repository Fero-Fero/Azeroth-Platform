export interface IndividualProgressionKeyMapping {
  startingProgression: string
  progressionLimit: string
  tbcRacesUnlockProgression: string
  tbcRacesStartingProgression: string
}

export interface IndividualProgressionSettings {
  bootstrapped: boolean
  moduleConfPath: string
  worldserverConfPath: string
  expansionKey: string
  keys: IndividualProgressionKeyMapping
  values: Record<string, string>
}

export interface IndividualProgressionBootstrapResult {
  templatesCreated: number
  configUpdated: boolean
  expansion: number
  keysDiscovered: boolean
  settings: IndividualProgressionSettings
}

export interface PatchProgressionMetadata {
  state: number
  slug: string
  expansion: string
  incrementsProgression: boolean
}

export interface IndividualProgressionKeyCheck {
  key: string
  configPath: string
  patchKey?: string | null
  configSource?: string | null
  exists: boolean
  canRead: boolean
  canUpdate: boolean
  value?: string | null
  error?: string | null
}

export interface IndividualProgressionValidationResult {
  passed: boolean
  isCurrent: boolean
  validatedAt?: string | null
  buildFingerprint?: string | null
  patchCount: number
  expectedPatchCount: number
  errors: string[]
  keyChecks: IndividualProgressionKeyCheck[]
}

export const INDIVIDUAL_PROGRESSION_MODULE_ID = 'mod-individual-progression'

// ===== Progression Sync =====

export interface ProgressionSyncMappingEntry {
  source: string
  destination: string
  optional: boolean
}

export interface ProgressionSyncMapping {
  mappings: ProgressionSyncMappingEntry[]
}

export interface ProgressionOptionalFileEntry {
  source: string
  destination: string
  fileName: string
  accepted: boolean
  decidedAt: string
}

export interface ProgressionOptionalFilesLog {
  entries: ProgressionOptionalFileEntry[]
  lastSyncAt: string
}

export interface ProgressionIgnoredFile {
  source: string
  destination: string
  fileName: string
  decidedAt: string
}

export interface ProgressionSyncPendingFile {
  source: string
  destination: string
  fileName: string
}

export interface ProgressionSyncResult {
  copiedFiles: number
  skippedOptional: number
  pendingOptionalFiles: ProgressionSyncPendingFile[]
  log: string[]
  success: boolean
  error?: string | null
}

export interface ProgressionSyncStatus {
  isRunning: boolean
  hasOptionalFilesLog: boolean
  ignoredFilesCount: number
  lastSyncAt?: string | null
  hasCompletedInitialSync?: boolean
  phase?: string | null
  progressPercent?: number
  message?: string | null
  startedAt?: string | null
  completedAt?: string | null
  error?: string | null
  log: string[]
}

export interface ResolveOptionalFilesRequest {
  decisions: Record<string, boolean>
}

// ===== MPQ Construction =====

export interface MpqManifest {
  add: string[]
  remove: string[]
  description: Record<string, string>
}

export interface MpqConstructionEntry {
  mpqName: string
  patchKey: string
  description?: string | null
  preBuilt: boolean
}

export interface MpqConstructionPlan {
  toBuild: MpqConstructionEntry[]
  skipped: string[]
}
