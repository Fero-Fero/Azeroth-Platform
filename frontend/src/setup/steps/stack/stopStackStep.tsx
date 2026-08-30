import { STEP_IDS } from '@/setup/constants'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { StackStatus } from '@/types/stack.types'

type StopStackOptions = {
  when?: (ctx: SetupStepContext) => boolean
}

export function stopStackStep(options: StopStackOptions = {}): SetupStep {
  return {
    id: STEP_IDS.stopStack,
    level: 'warning',
    title: 'Stop stack',
    applies: (ctx) => options.when?.(ctx) ?? ctx.stack.status === StackStatus.Running,
    isComplete: (ctx) => ctx.stack.status === StackStatus.Stopped,
    summary: () => 'Stop the stack before continuing.',
    Component: () => <p>Stop the stack from the overview when you are ready to continue.</p>,
  }
}
