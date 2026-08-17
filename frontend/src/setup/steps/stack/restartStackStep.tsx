import { STEP_IDS } from '@/setup/constants'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { StackStatus } from '@/types/stack.types'

type RestartStackOptions = {
  when?: (ctx: SetupStepContext) => boolean
}

export function restartStackStep(options: RestartStackOptions = {}): SetupStep {
  return {
    id: STEP_IDS.restartStack,
    level: 'warning',
    title: 'Restart stack',
    applies: (ctx) => options.when?.(ctx) ?? ctx.stack.status === StackStatus.Running,
    isComplete: () => false,
    summary: () => 'Restart the stack to apply configuration changes.',
    Component: () => <p>Restart the stack from the overview to apply the latest configuration.</p>,
  }
}
