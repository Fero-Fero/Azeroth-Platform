export type PatchStatus = 'Applied' | 'Next' | 'Locked'

export interface PatchFileDto {
  category: string
  name: string
  size: number
  description?: string | null
}

export interface PatchSummaryDto {
  key: string
  index: string
  level: number
  name: string
  status: PatchStatus
  sqlCount: number
  dbcCount: number
  mapCount: number
  mpqCount: number
  description: string
  appliedAt?: string | null
  progressionState?: number | null
  progressionSlug?: string | null
  progressionTitle?: string | null
  incrementsProgression?: boolean | null
}

import type { PatchProgressionMetadata } from '@/types/individual-progression.types'

export interface PatchDetailsDto {
  key: string
  index: string
  level: number
  name: string
  status: PatchStatus
  appliedAt?: string | null
  description: string
  descriptionFile?: string | null
  files: PatchFileDto[]
  mpqRemovals: string[]
  progression?: PatchProgressionMetadata | null
  configOverrides?: PatchConfigOverrideDto[]
}

export interface PatchConfigOverrideDto {
  sourceJson: string
  targetConf: string
  key: string
  value: string
}

export interface PublishedMpqDto {
  name: string
  size: number
  isReserved: boolean
}

export interface MigrationOverviewDto {
  stackId: string
  currentLevel: number
  currentIndex: string
  baselineInitialized: boolean
  isApplying: boolean
  applyingPatchKey?: string | null
  patches: PatchSummaryDto[]
  hasIndividualProgressionModule: boolean
  individualProgressionBootstrapped: boolean
  individualProgressionValidationRequired: boolean
  individualProgressionValidationCurrent: boolean
  individualProgressionValidationPassedAt?: string | null
  individualProgressionPatchCount: number
  individualProgressionExpectedPatchCount: number
}

export interface ApplyPatchResultDto {
  success: boolean
  patchKey: string
  level: number
  log: string[]
  error?: string | null
}

export interface ApplyStatusDto {
  isApplying: boolean
  patchKey?: string | null
  runId?: string | null
  phase?: string | null
  correlationId?: string | null
  startedAt?: string | null
  completedAt?: string | null
  success?: boolean | null
  error?: string | null
  log: string[]
  logAvailable: boolean
}

export type Expansion = 'classic' | 'tbc' | 'wotlk' | 'custom'

export type PatchKind = 'expansion' | 'patch' | 'hotfix'

export interface CreatePatchRequest {
  expansion: Expansion
  kind?: PatchKind
  name?: string
  parentIndex?: string
}

export type ImportPatchCollectionMode = 'append' | 'override' | 'merge'

export interface ImportedPatchDto {
  expansion: Expansion
  sourceKey: string
  targetKey: string
}

export interface ImportPatchCollectionResultDto {
  mode: ImportPatchCollectionMode
  importedCount: number
  importedPatches: ImportedPatchDto[]
}

export interface DbcContentDto {
  fileName: string
  content: string
}
