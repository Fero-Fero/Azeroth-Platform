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

export interface IndividualProgressionRecreatePatchesResult {
  templatesCreated: number
  missingBefore: number
}

export interface PatchProgressionMetadata {
  state: number
  slug: string
  expansion: string
  incrementsProgression: boolean
}

export interface MergePatchImportResult {
  targetPatchKey: string
  sqlFiles: number
  mpqFiles: number
  dbcFiles: number
  mapFiles: number
}

export interface IndividualProgressionKeyCheck {
  key: string
  configPath: string
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

export const INDIVIDUAL_PROGRESSION_EXPECTED_PATCH_COUNT = 18

export const INDIVIDUAL_PROGRESSION_MODULE_ID = 'mod-individual-progression'
