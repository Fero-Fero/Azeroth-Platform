import type { SetupWorkflowBuilder } from '@/setup/types'

export const defaultSetup: SetupWorkflowBuilder = (_ctx, moduleSteps) => [...moduleSteps]
