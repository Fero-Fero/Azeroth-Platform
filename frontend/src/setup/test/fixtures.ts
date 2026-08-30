import '@/setup/test/localStorageMock'
import { createSetupProgressStore, type SetupProgressStore } from '@/setup/progress/setupProgressStore'
import type { SetupStepContext, SetupStepStatus } from '@/setup/types'
import { ServerType, StackStatus, type StackDetailsDto } from '@/types/stack.types'

export function createMockProgress(): SetupProgressStore {
  return createSetupProgressStore(`test-${Math.random().toString(36).slice(2)}`)
}

export function createMockStatus(overrides: Partial<SetupStepStatus> = {}): SetupStepStatus {
  const progress = overrides.progress ?? createMockProgress()
  return {
    soapInitialized: false,
    dbcStore: { ready: true, inProgress: false, loading: false, tag: 'v20.0' },
    moduleExtraData: { modules: [], loading: false, jobPhase: null, ipContentMode: 'Unset', prepared: false, deposited: false, hasPendingDeposit: false },
    client: { dataUploaded: false, containerRunning: false, loading: false },
    armory: { dbcUploaded: false, containerRunning: false, loading: false },
    playerbots: { confPath: null, enabled: null, chatterDisabled: null, loading: false },
    individualProgression: { bootstrapped: false, syncCompleted: false, loading: false },
    progress,
    ...overrides,
  }
}

export function createMockStack(overrides: Partial<StackDetailsDto> = {}): StackDetailsDto {
  const { configuration, ...rest } = overrides
  return {
    stackId: 'stack-1',
    status: StackStatus.Stopped,
    isAdminAccountInitialized: false,
    armoryRunning: false,
    services: [],
    ...rest,
    configuration: {
      serverType: ServerType.Standard,
      moduleIds: [],
      ...configuration,
    },
  } as StackDetailsDto
}

export function createMockContext(overrides: {
  stack?: Partial<StackDetailsDto>
  status?: Partial<SetupStepStatus>
} = {}): SetupStepContext {
  const stack = createMockStack(overrides.stack)
  return {
    stack,
    patchesHref: `/stacks/${stack.stackId}?tab=patches`,
    onSelectTab: () => {},
    status: createMockStatus(overrides.status),
  }
}
