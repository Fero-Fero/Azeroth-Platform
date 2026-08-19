import { STEP_IDS } from '@/setup/constants'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { setupActionButton, setupSkipButton } from '@/setup/ui'
import { useDepositModuleExtraData, useModuleExtraDataJob } from '@/hooks/useModuleExtraData'

function ExtraDataDetails(ctx: SetupStepContext) {
  const deposit = useDepositModuleExtraData(ctx.stack.stackId)
  const job = useModuleExtraDataJob(ctx.stack.stackId, deposit.isPending || deposit.data?.isRunning === true)
  const running = job.data?.phase === 'Running' || deposit.isPending

  return (
    <div className="space-y-3 text-sm text-amber-900">
      <p>
        Apply extra client and server content that was prepared into InstalledModules (DBC into patch-D,
        overlay MPQs, optional SQL, addons, Lua scripts). Choices were already saved before the first build.
      </p>
      {job.data?.error && <p className="text-red-700">{job.data.error}</p>}
      {job.data?.message && <p className="text-gray-700">{job.data.message}</p>}
      {setupActionButton('Setup module content', () => deposit.mutate(), {
        pending: running,
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
    applies: (ctx) =>
      ctx.status.soapInitialized
      && !ctx.status.moduleExtraData.loading
      && ctx.status.moduleExtraData.hasPendingDeposit
      && !ctx.status.progress.isSkipped(STEP_IDS.moduleExtraData),
    isComplete: (ctx) =>
      ctx.status.moduleExtraData.deposited
      || ctx.status.progress.isSkipped(STEP_IDS.moduleExtraData),
    summary: () => 'Apply prepared module extras (DBC, MPQ, SQL, addons, Lua) onto the running stack.',
    Component: (ctx) => <ExtraDataDetails {...ctx} />,
    Action: (ctx) => <ExtraDataAction {...ctx} />,
  }
}
