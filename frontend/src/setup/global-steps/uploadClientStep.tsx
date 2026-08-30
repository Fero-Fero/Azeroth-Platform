import { STEP_IDS } from '@/setup/constants'
import { clientUploadApplies, isClientUploadComplete } from '@/setup/global-steps/uploadStatus'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { setupActionButton, setupSkipButton } from '@/setup/ui'

function ClientDetails() {
  return (
    <p>
      Upload a base client archive on the Client tab. Each stack keeps its own client files, so this must be
      done per stack. You can skip this if you are not serving a launcher client yet.
    </p>
  )
}

function ClientAction(ctx: SetupStepContext) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      {setupSkipButton(() => ctx.status.progress.skip(STEP_IDS.uploadClient))}
      {setupActionButton('Upload client', () => ctx.onSelectTab('client'))}
    </div>
  )
}

export function uploadClientStep(): SetupStep {
  return {
    id: STEP_IDS.uploadClient,
    skippable: true,
    level: 'warning',
    title: 'Base client not uploaded',
    applies: (ctx) => clientUploadApplies(ctx.status) && !ctx.status.progress.isSkipped(STEP_IDS.uploadClient),
    isComplete: (ctx) => isClientUploadComplete(ctx.status, ctx.status.progress),
    summary: () => 'The client container has no base WoW client to serve to the launcher.',
    Component: () => <ClientDetails />,
    Action: (ctx) => <ClientAction {...ctx} />,
  }
}
