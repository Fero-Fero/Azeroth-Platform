import { isStepDoneOrSkipped } from '@/setup/progress/setupProgressStore'
import { STEP_IDS } from '@/setup/constants'
import type { SetupStep, SetupStepContext, SetupStepStatus } from '@/setup/types'
import { setupActionButton, setupSkipButton } from '@/setup/ui'
import { useSyncDbcStore } from '@/hooks/useModuleExtraData'

function DbcBaselineDetails() {
  return (
    <p>
      The manager downloads the latest wowgaming/client-data release and keeps only DBC tables as CSVs. Module
      extra-data (Individual Progression Spell.dbc, and so on) trims against this store. You can create a stack
      first, but extra-data apply waits until the store is ready.
    </p>
  )
}

function DbcBaselineAction(ctx: SetupStepContext) {
  const sync = useSyncDbcStore()
  return (
    <div className="flex flex-wrap items-center gap-2">
      {setupSkipButton(() => ctx.status.progress.skip(STEP_IDS.dbcBaseline))}
      {setupActionButton('Sync DBC baseline', () => sync.mutate(false), {
        pending: sync.isPending || ctx.status.dbcStore.inProgress,
        disabled: ctx.status.dbcStore.loading,
      })}
    </div>
  )
}

export function dbcBaselineApplies(status: SetupStepStatus): boolean {
  return !status.dbcStore.loading && !status.dbcStore.ready
}

export function isDbcBaselineComplete(status: SetupStepStatus): boolean {
  return isStepDoneOrSkipped(STEP_IDS.dbcBaseline, status.dbcStore.ready, status.progress)
}

export function dbcBaselineStep(): SetupStep {
  return {
    id: STEP_IDS.dbcBaseline,
    skippable: true,
    level: 'warning',
    title: 'DBC baseline not ready',
    applies: (ctx) => dbcBaselineApplies(ctx.status) && !ctx.status.progress.isSkipped(STEP_IDS.dbcBaseline),
    isComplete: (ctx) => isDbcBaselineComplete(ctx.status),
    summary: () => 'Download the vanilla WotLK DBC CSV store used to trim module extra data.',
    Component: () => <DbcBaselineDetails />,
    Action: (ctx) => <DbcBaselineAction {...ctx} />,
  }
}
