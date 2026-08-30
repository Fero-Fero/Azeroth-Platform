import { Bot } from 'lucide-react'
import { MODULE_IDS, STEP_IDS } from '@/setup/constants'
import {
  hasPlayerbotsModule,
  isPlayerbotsDisableComplete,
} from '@/setup/steps/modules/mod-playerbots/playerbotsStatus'
import { usePlayerbotsConf } from '@/setup/steps/modules/mod-playerbots/usePlayerbotsConf'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { setupActionButton } from '@/setup/ui'

function DisableDetails(ctx: SetupStepContext) {
  const playerbots = usePlayerbotsConf(ctx.stack.stackId)

  return (
    <div className="space-y-2 text-sm">
      <p>
        Individual Progression stacks should configure patches and other content before bots start spawning.
        Disable playerbots now, start the stack, then sync progression on the Patches tab and re-enable
        playerbots when you are ready.
      </p>
      {!playerbots.path && (
        <p>
          Complete the initial stack build first -{' '}
          <code className="rounded bg-amber-100 px-1 text-xs">playerbots.conf</code> becomes available after
          module configs are seeded.
        </p>
      )}
      {playerbots.toggleError && (
        <p className="text-red-700">
          {(playerbots.toggleError as Error)?.message ?? 'Failed to update playerbots.conf.'}
        </p>
      )}
    </div>
  )
}

function DisableAction(ctx: SetupStepContext) {
  const playerbots = usePlayerbotsConf(ctx.stack.stackId)
  return setupActionButton(
    'Disable playerbots',
    () => {
      ctx.status.progress.setPlayerbotsPhase('awaiting-start')
      playerbots.toggle(false, {
        onError: () => {
          if (ctx.status.progress.getPlayerbotsPhase() === 'awaiting-start') {
            ctx.status.progress.setPlayerbotsPhase(null)
          }
        },
      })
    },
    {
      disabled: playerbots.isToggling || !playerbots.path,
      pending: playerbots.isToggling,
      icon: <Bot className="h-4 w-4" />,
    },
  )
}

export function disablePlayerbotsStep(): SetupStep {
  return {
    id: STEP_IDS.disablePlayerbots,
    moduleId: MODULE_IDS.playerbots,
    level: 'warning',
    title: 'Disable playerbots before your first launch',
    defaultExpanded: true,
    applies: (ctx) => hasPlayerbotsModule(ctx.stack),
    isComplete: (ctx) => isPlayerbotsDisableComplete(ctx.status),
    summary: () =>
      'Disable playerbots, start the stack, prepare progression on Patches, then re-enable playerbots.',
    Component: (ctx) => <DisableDetails {...ctx} />,
    Action: (ctx) => <DisableAction {...ctx} />,
  }
}
