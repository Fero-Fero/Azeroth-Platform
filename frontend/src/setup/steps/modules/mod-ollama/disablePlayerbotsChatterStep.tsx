import { MessageSquareOff } from 'lucide-react'
import { OLLAMA_PLAYERBOTS_CHATTER_DISABLE, STEP_IDS } from '@/setup/constants'
import { usePlayerbotsConf } from '@/setup/steps/modules/mod-playerbots/usePlayerbotsConf'
import {
  hasOllamaModule,
  isExpressStack,
  isOllamaPlayerbotsChatterComplete,
  ollamaModuleLabel,
} from '@/setup/steps/modules/mod-ollama/ollamaChatterStatus'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { setupActionButton } from '@/setup/ui'

function ChatterDetails(ctx: SetupStepContext) {
  const playerbots = usePlayerbotsConf(ctx.stack.stackId)
  const label = ollamaModuleLabel(ctx.stack)

  return (
    <div className="space-y-3 text-sm">
      <p>
        {label} works best when Playerbots&apos; built-in talk is off, so loot broadcasts and random say
        do not compete with the LLM. Apply these keys in{' '}
        <code className="rounded bg-amber-100 px-1 text-xs">playerbots.conf</code>.
      </p>
      <ul className="list-disc space-y-1 pl-5 text-xs text-gray-600">
        {Object.entries(OLLAMA_PLAYERBOTS_CHATTER_DISABLE).map(([key, value]) => (
          <li key={key}>
            <code className="rounded bg-white px-1">{key} = {value}</code>
          </li>
        ))}
      </ul>
      {!playerbots.path && (
        <p>
          Complete the initial stack build first —{' '}
          <code className="rounded bg-amber-100 px-1 text-xs">playerbots.conf</code> becomes available after
          module configs are seeded.
        </p>
      )}
      {playerbots.chatterError && (
        <p className="text-red-700">
          {(playerbots.chatterError as Error)?.message ?? 'Failed to update playerbots.conf.'}
        </p>
      )}
      <p className="text-amber-800">If worldserver is already running, restart it so the keys take effect.</p>
    </div>
  )
}

function ChatterAction(ctx: SetupStepContext) {
  const playerbots = usePlayerbotsConf(ctx.stack.stackId)

  return (
    <div className="flex flex-wrap items-center gap-2">
      {setupActionButton(
        'Disable Playerbots chatter',
        () => {
          playerbots.applyChatterDisable()
        },
        {
          disabled: playerbots.isApplyingChatter || !playerbots.path,
          pending: playerbots.isApplyingChatter,
          icon: <MessageSquareOff className="h-4 w-4" />,
        },
      )}
      <button
        type="button"
        onClick={() => ctx.status.progress.dismiss(STEP_IDS.ollamaDisablePlayerbotsChatter)}
        className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50"
      >
        Dismiss
      </button>
    </div>
  )
}

export function disablePlayerbotsChatterStep(): SetupStep {
  return {
    id: STEP_IDS.ollamaDisablePlayerbotsChatter,
    level: 'warning',
    title: 'Disable Playerbots chatter for Ollama',
    defaultExpanded: true,
    applies: (ctx) =>
      !isExpressStack(ctx.stack)
      && hasOllamaModule(ctx.stack)
      && !isOllamaPlayerbotsChatterComplete(ctx),
    isComplete: (ctx) => isOllamaPlayerbotsChatterComplete(ctx),
    summary: (ctx) =>
      `Recommended: turn off Playerbots broadcasts and random talk so they do not overlap ${ollamaModuleLabel(ctx.stack)}.`,
    Component: (ctx) => <ChatterDetails {...ctx} />,
    Action: (ctx) => <ChatterAction {...ctx} />,
  }
}
