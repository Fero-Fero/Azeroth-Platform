import { TrendingUp } from 'lucide-react'
import { MODULE_IDS, STEP_IDS } from '@/setup/constants'
import { isStepDoneOrSkipped } from '@/setup/progress/setupProgressStore'
import {
  hasIndividualProgressionModule,
  isIpProgressionReady,
} from '@/setup/steps/modules/mod-individual-progression/ipStatus'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { setupActionButton, setupSkipButton } from '@/setup/ui'

function PrepareDetails() {
  return (
    <p className="text-sm">
      Optional. Open the Patches tab to <strong>prepare server-wide progression</strong> (bootstrap), then
      run <strong>Sync with mod-individual-progression</strong> and apply patches in order. You can skip
      this if you want a standard realm.
    </p>
  )
}

function PrepareAction(ctx: SetupStepContext) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      {setupSkipButton(() => {
        ctx.status.progress.skip(STEP_IDS.prepareProgression)
        ctx.status.progress.dismiss(STEP_IDS.ipSyncHint)
      })}
      {setupActionButton('Open Patches tab', () => ctx.onSelectTab('patches'), {
        icon: <TrendingUp className="h-4 w-4" />,
        tone: 'blue',
      })}
    </div>
  )
}

export function prepareProgressionStep(): SetupStep {
  return {
    id: STEP_IDS.prepareProgression,
    moduleId: MODULE_IDS.individualProgression,
    skippable: true,
    level: 'warning',
    title: 'Prepare server-wide progression',
    defaultExpanded: true,
    applies: (ctx) =>
      hasIndividualProgressionModule(ctx.stack) &&
      ctx.status.moduleExtraData.ipContentMode !== 'Standard' &&
      !ctx.status.progress.isSkipped(STEP_IDS.prepareProgression),
    isComplete: (ctx) =>
      isStepDoneOrSkipped(STEP_IDS.prepareProgression, isIpProgressionReady(ctx.status), ctx.status.progress),
    summary: () =>
      'Optional - bootstrap Individual Progression and sync patches, or skip for a standard realm.',
    Component: () => <PrepareDetails />,
    Action: (ctx) => <PrepareAction {...ctx} />,
  }
}
