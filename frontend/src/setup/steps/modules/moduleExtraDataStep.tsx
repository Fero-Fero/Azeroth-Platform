import { useEffect } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { STEP_IDS } from '@/setup/constants'
import { hasIndividualProgressionModule, isIpSyncComplete } from '@/setup/steps/modules/mod-individual-progression/ipStatus'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { setupActionButton, setupSkipButton } from '@/setup/ui'
import {
  moduleExtraDataKeys,
  useDepositModuleExtraData,
  useModuleExtraDataJob,
} from '@/hooks/useModuleExtraData'

function ExtraDataDetails(ctx: SetupStepContext) {
  const queryClient = useQueryClient()
  const deposit = useDepositModuleExtraData(ctx.stack.stackId)
  const job = useModuleExtraDataJob(ctx.stack.stackId, true)
  const running = job.data?.phase === 'Running' || deposit.isPending
  const failed = job.data?.phase === 'Failed'

  useEffect(() => {
    if (job.data?.phase !== 'Completed' && job.data?.phase !== 'Failed') {
      return
    }
    void queryClient.invalidateQueries({ queryKey: moduleExtraDataKeys.stackStatus(ctx.stack.stackId) })
  }, [ctx.stack.stackId, job.data?.phase, queryClient])

  return (
    <div className="space-y-3 text-sm text-amber-900">
      <p>
        Apply extra client and server content that was prepared into InstalledModules (DBC into patch-D,
        overlay MPQs, optional SQL, addons, Lua scripts). Choices were already saved before the first build.
      </p>
      {failed && (job.data?.error || job.data?.message) && (
        <p className="text-red-700">{job.data.error ?? job.data.message}</p>
      )}
      {running && job.data?.message && <p className="text-gray-700">{job.data.message}</p>}
      {setupActionButton('Setup module content', () => deposit.mutate(), {
        pending: running,
        disabled: running,
      })}
    </div>
  )
}

function ExtraDataAction(ctx: SetupStepContext) {
  return setupSkipButton(() => ctx.status.progress.skip(STEP_IDS.moduleExtraData))
}

export function moduleExtraDataStep(): SetupStep {
  return {
    id: STEP_IDS.moduleExtraData,
    skippable: true,
    level: 'warning',
    title: 'Setup module content',
    dependsOn: [STEP_IDS.soapAdmin],
    applies: (ctx) => {
      if (
        !ctx.status.soapInitialized ||
        ctx.status.moduleExtraData.loading ||
        !ctx.status.moduleExtraData.hasPendingDeposit ||
        ctx.status.progress.isSkipped(STEP_IDS.moduleExtraData)
      ) {
        return false
      }
      if (
        hasIndividualProgressionModule(ctx.stack) &&
        ctx.status.moduleExtraData.ipContentMode !== 'Standard' &&
        !ctx.status.progress.isSkipped(STEP_IDS.prepareProgression) &&
        !isIpSyncComplete(ctx.status)
      ) {
        return false
      }
      return true
    },
    isComplete: (ctx) =>
      ctx.status.moduleExtraData.deposited
      || ctx.status.progress.isSkipped(STEP_IDS.moduleExtraData),
    summary: () => 'Apply prepared module extras (DBC, MPQ, SQL, addons, Lua) onto the running stack.',
    Component: (ctx) => <ExtraDataDetails {...ctx} />,
    Action: (ctx) => <ExtraDataAction {...ctx} />,
  }
}
