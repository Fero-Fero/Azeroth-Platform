import { Loader2 } from 'lucide-react'
import { STEP_IDS } from '@/setup/constants'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { ServerType } from '@/types/stack.types'

function isExpress(ctx: SetupStepContext) {
  return ctx.stack.configuration.serverType === ServerType.Express
}

function provisionActive(ctx: SetupStepContext) {
  const status = ctx.stack.expressProvisionStatus
  return status === 'Pending' || status === 'Running' || status === 'Failed'
}

export function expressProvisionCompleted(stack: { expressProvisionStatus?: string }) {
  return stack.expressProvisionStatus === 'Completed'
}

export function expressProvisionStep(): SetupStep {
  return {
    id: STEP_IDS.expressProvision,
    level: 'loading',
    title: 'Express Setup',
    defaultExpanded: true,
    applies: (ctx) => isExpress(ctx) && provisionActive(ctx),
    isComplete: (ctx) => expressProvisionCompleted(ctx.stack),
    summary: (ctx) => ctx.stack.expressProvisionMessage || 'Preparing Express Setup…',
    Component: (ctx) => (
      <div className="space-y-2 text-sm text-gray-700">
        {ctx.stack.expressProvisionStatus === 'Failed' ? (
          <p className="text-red-700">{ctx.stack.expressProvisionMessage || 'Express Setup failed.'}</p>
        ) : (
          <p>
            Express Setup downloads the client, applies the first patch, then starts the server with
            playerbots enabled. This runs automatically after the first build.
          </p>
        )}
      </div>
    ),
    Action: (ctx) =>
      ctx.stack.expressProvisionStatus === 'Running' || ctx.stack.expressProvisionStatus === 'Pending' ? (
        <span className="inline-flex items-center gap-2 text-sm text-blue-700">
          <Loader2 className="h-4 w-4 animate-spin" />
          {ctx.stack.expressProvisionMessage || 'Working…'}
        </span>
      ) : null,
  }
}
