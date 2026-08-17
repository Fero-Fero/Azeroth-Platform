import { Info, X } from 'lucide-react'
import { MODULE_IDS, STEP_IDS } from '@/setup/constants'
import {
  hasIndividualProgressionModule,
  isIpPipelineComplete,
} from '@/setup/steps/modules/mod-individual-progression/ipStatus'
import type { SetupStep, SetupStepContext } from '@/setup/types'

function SyncHintDetails(ctx: SetupStepContext) {
  return (
    <div className="flex items-start gap-3">
      <Info className="mt-0.5 h-5 w-5 shrink-0 text-violet-600" aria-hidden="true" />
      <p className="text-sm text-violet-900">
        Open the{' '}
        <button
          type="button"
          onClick={() => ctx.onSelectTab('patches')}
          className="font-medium text-violet-700 underline hover:text-violet-900"
        >
          Patches tab
        </button>{' '}
        to apply remaining patches in order after progression is prepared.
      </p>
    </div>
  )
}

function SyncHintAction(ctx: SetupStepContext) {
  return (
    <button
      type="button"
      onClick={() => ctx.status.progress.dismiss(STEP_IDS.ipSyncHint)}
      className="shrink-0 rounded p-1 text-violet-500 hover:bg-violet-100 hover:text-violet-800"
      aria-label="Dismiss"
    >
      <X className="h-4 w-4" />
    </button>
  )
}

export function ipSyncHintStep(): SetupStep {
  return {
    id: STEP_IDS.ipSyncHint,
    moduleId: MODULE_IDS.individualProgression,
    level: 'warning',
    title: 'Individual Progression — patches',
    applies: (ctx) =>
      hasIndividualProgressionModule(ctx.stack) &&
      !ctx.status.progress.isSkipped(STEP_IDS.prepareProgression) &&
      isIpPipelineComplete(ctx) &&
      !ctx.status.progress.isDismissed(STEP_IDS.ipSyncHint),
    isComplete: (ctx) => ctx.status.progress.isDismissed(STEP_IDS.ipSyncHint),
    summary: () => 'Apply patches in order on the Patches tab.',
    Component: (ctx) => <SyncHintDetails {...ctx} />,
    Action: (ctx) => <SyncHintAction {...ctx} />,
  }
}
