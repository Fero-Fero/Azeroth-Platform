import { isStepDoneOrSkipped } from '@/setup/progress/setupProgressStore'
import { STEP_IDS } from '@/setup/constants'
import type { SetupStep, SetupStepStatus } from '@/setup/types'

export function dbcBaselineApplies(_status: SetupStepStatus): boolean {
  return false
}

export function isDbcBaselineComplete(status: SetupStepStatus): boolean {
  return isStepDoneOrSkipped(STEP_IDS.dbcBaseline, true, status.progress)
}

export function dbcBaselineStep(): SetupStep {
  return {
    id: STEP_IDS.dbcBaseline,
    skippable: true,
    level: 'warning',
    title: 'DBC baseline not ready',
    applies: () => false,
    isComplete: (ctx) => isDbcBaselineComplete(ctx.status),
    summary: () =>
      'DBC baselines convert on demand from the stack data directory when a patch or module needs a table.',
    Component: () => null,
  }
}
