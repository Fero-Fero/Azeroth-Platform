import { Loader2 } from 'lucide-react'
import { STEP_IDS } from '@/setup/constants'
import { isDbImportInProgress } from '@/setup/stackServices'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { useStackJob } from '@/hooks/useStackJob'

function WaitDbImportDetails(ctx: SetupStepContext) {
  const { job } = useStackJob(ctx.stack.stackId)
  const message = job?.message?.trim()

  return (
    <div className="space-y-2 text-sm">
      <p>
        Database import is still running. Wait for it to finish before setting up Individual Progression or
        applying module content.
      </p>
      {message && <p className="text-gray-600">{message}</p>}
    </div>
  )
}

export function waitDbImportStep(): SetupStep {
  return {
    id: STEP_IDS.waitDbImport,
    level: 'loading',
    title: 'Waiting for DBimport',
    defaultExpanded: true,
    applies: (ctx) => ctx.status.soapInitialized && isDbImportInProgress(ctx.stack),
    isComplete: (ctx) => ctx.status.soapInitialized && !isDbImportInProgress(ctx.stack),
    summary: () => 'DBimport must finish before you can continue setup.',
    Component: (ctx) => <WaitDbImportDetails {...ctx} />,
    Action: () => (
      <span className="inline-flex items-center gap-1.5 text-sm text-gray-600">
        <Loader2 className="h-4 w-4 animate-spin" />
        Importing
      </span>
    ),
  }
}
