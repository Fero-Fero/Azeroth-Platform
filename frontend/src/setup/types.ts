import type { StackDetailsDto } from '@/types/stack.types'
import type { SetupProgressStore } from '@/setup/progress/setupProgressStore'

export type SetupTabId = 'addons' | 'patches' | 'client' | 'armory'

export type SetupStepContext = {
  stack: StackDetailsDto
  patchesHref: string
  onSelectTab: (tab: SetupTabId) => void
  status: SetupStepStatus
}

export type SetupStepStatus = {
  soapInitialized: boolean
  dbcStore: {
    ready: boolean
    inProgress: boolean
    loading: boolean
    tag: string | null
  }
  moduleExtraData: {
    modules: import('@/types/module-extra-data.types').ModuleInstallChoicesDto[]
    loading: boolean
    jobPhase: import('@/types/module-extra-data.types').ModuleInstallJobPhase | null
    ipContentMode: import('@/types/module-extra-data.types').IpContentMode
    prepared: boolean
    deposited: boolean
    hasPendingDeposit: boolean
  }
  client: {
    dataUploaded: boolean
    containerRunning: boolean
    loading: boolean
  }
  armory: {
    dbcUploaded: boolean
    containerRunning: boolean
    loading: boolean
  }
  playerbots: {
    confPath: string | null
    enabled: boolean | null
    loading: boolean
  }
  individualProgression: {
    bootstrapped: boolean
    syncCompleted: boolean
    loading: boolean
  }
  progress: SetupProgressStore
}

export type SetupStepLevel = 'error' | 'warning' | 'success' | 'loading'

export type SetupStep = {
  id: string
  moduleId?: string
  skippable?: boolean
  sequenced?: boolean
  dependsOn?: string[]
  level: SetupStepLevel
  title: string
  summary: (ctx: SetupStepContext) => string
  applies: (ctx: SetupStepContext) => boolean
  isComplete: (ctx: SetupStepContext) => boolean
  /**
   * Broader than `applies` for progress totals. Use when a sequenced step is
   * phase-gated in the UI but should still count as remaining (e.g. start-stack
   * after playerbots is disabled). Defaults to `applies`.
   */
  progressApplies?: (ctx: SetupStepContext) => boolean
  showWhenComplete?: (ctx: SetupStepContext) => boolean
  defaultExpanded?: boolean
  Component: React.FC<SetupStepContext>
  Action?: React.FC<SetupStepContext>
}

export type SetupWorkflowBuilder = (
  ctx: SetupStepContext,
  moduleSteps: SetupStep[],
) => SetupStep[]

export type SetupPipeline = {
  id: string
  steps: SetupStep[]
}

export type { SetupProgressStore }
