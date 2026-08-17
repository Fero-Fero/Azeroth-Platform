import { STEP_IDS } from '@/setup/constants'
import { armoryDbcUploadApplies, isArmoryDbcUploadComplete } from '@/setup/global-steps/uploadStatus'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { setupActionButton, setupSkipButton } from '@/setup/ui'

function ArmoryDetails() {
  return (
    <p>
      Upload the armory static assets on the Armory tab so character previews and the model viewer work
      correctly. You can skip this if you are not using the armory viewer yet.
    </p>
  )
}

function ArmoryAction(ctx: SetupStepContext) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      {setupSkipButton(() => ctx.status.progress.skip(STEP_IDS.uploadArmoryDbc))}
      {setupActionButton('Upload armory data', () => ctx.onSelectTab('armory'))}
    </div>
  )
}

export function uploadArmoryDbcStep(): SetupStep {
  return {
    id: STEP_IDS.uploadArmoryDbc,
    skippable: true,
    level: 'warning',
    title: 'Armory data not uploaded',
    applies: (ctx) =>
      armoryDbcUploadApplies(ctx.status) && !ctx.status.progress.isSkipped(STEP_IDS.uploadArmoryDbc),
    isComplete: (ctx) => isArmoryDbcUploadComplete(ctx.status, ctx.status.progress),
    summary: () => 'The 3D model-viewer dataset is missing, so the armory viewer is disabled.',
    Component: () => <ArmoryDetails />,
    Action: (ctx) => <ArmoryAction {...ctx} />,
  }
}
