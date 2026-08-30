export type DbcBaselineStoreDto = {
  ready: boolean
  inProgress: boolean
  tag: string | null
  publishedAt: string | null
  syncedAt: string | null
  tableCount: number
  error: string | null
  message: string | null
  recentLogs: string[]
}

export type ModuleInstallChoiceKind = 'Exclusive' | 'Independent'

export type ModuleInstallChoice = {
  id: string
  label: string
  description?: string | null
  defaultSelected?: boolean
}

export type ModuleInstallChoiceGroup = {
  id: string
  title: string
  description: string
  kind: ModuleInstallChoiceKind
  allowNone: boolean
  choices: ModuleInstallChoice[]
}

export type ModuleInstallChoicesDto = {
  moduleId: string
  groups: ModuleInstallChoiceGroup[]
}

export type IpContentMode = 'Unset' | 'Standard' | 'ServerWideProgression'

export type ModuleInstallSelections = {
  groups: Record<string, string[]>
}

export type ApplyModuleExtraDataRequest = {
  ipContentMode?: IpContentMode
  selectionsByModuleId: Record<string, ModuleInstallSelections>
}

export type ModuleExtraDataStackStatusDto = {
  ipContentMode: IpContentMode
  prepared: boolean
  deposited: boolean
  hasPendingDeposit: boolean
  hasExtras: boolean
}

export type StackModuleInstallChoicesDto = {
  modules: ModuleInstallChoicesDto[]
  saved?: ApplyModuleExtraDataRequest
  status?: ModuleExtraDataStackStatusDto
}

export type ModuleInstallJobPhase = 'Idle' | 'Running' | 'Completed' | 'Failed'

export type ModuleInstallJobStatusDto = {
  stackId?: string | null
  jobId: string
  phase: ModuleInstallJobPhase
  message: string
  error?: string | null
  success: boolean
  isRunning: boolean
  startedAt?: string | null
  finishedAt?: string | null
  recentLogs: string[]
}
