import { useState } from 'react'
import { AlertTriangle, Loader2, RotateCcw, TrendingUp } from 'lucide-react'
import { MODULE_IDS, STEP_IDS } from '@/setup/constants'
import {
  hasPlayerbotsModule,
  isPlayerbotsDisabled,
  isPlayerbotsSetupComplete,
} from '@/setup/steps/modules/mod-playerbots/playerbotsStatus'
import { usePlayerbotsConf } from '@/setup/steps/modules/mod-playerbots/usePlayerbotsConf'
import { isIpProgressionReady } from '@/setup/steps/modules/mod-individual-progression/ipStatus'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { setupActionButton } from '@/setup/ui'

function ReenableConfirmDialog({
  onCancel,
  onConfirm,
  pending,
}: {
  onCancel: () => void
  onConfirm: () => void
  pending: boolean
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-md rounded-lg bg-white shadow-xl">
        <div className="border-b border-gray-200 px-6 py-4">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-full bg-amber-100">
              <AlertTriangle className="h-5 w-5 text-amber-600" />
            </div>
            <h2 className="text-lg font-semibold text-gray-900">Re-enable playerbots?</h2>
          </div>
        </div>
        <div className="px-6 py-4">
          <p className="text-sm text-gray-700">
            Server-wide progression is not fully set up yet. Enabling playerbots before you prepare
            progression and sync patches on the Patches tab may cause issues with bot behaviour and
            progression state.
          </p>
          <p className="mt-3 text-sm text-gray-600">
            Open the Patches tab first if you have not run <strong>Prepare progression</strong> and{' '}
            <strong>Sync with mod-individual-progression</strong>.
          </p>
        </div>
        <div className="flex justify-end gap-3 border-t border-gray-200 px-6 py-4">
          <button
            type="button"
            onClick={onCancel}
            disabled={pending}
            className="rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-200 disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onConfirm}
            disabled={pending}
            className="inline-flex items-center gap-2 rounded-md bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-50"
          >
            {pending ? <Loader2 className="h-4 w-4 animate-spin" /> : <RotateCcw className="h-4 w-4" />}
            Re-enable anyway
          </button>
        </div>
      </div>
    </div>
  )
}

function ReenableDetails(ctx: SetupStepContext) {
  const playerbots = usePlayerbotsConf(ctx.stack.stackId)
  const progressionReady = isIpProgressionReady(ctx.status)

  return (
    <div className="space-y-3 text-sm">
      {progressionReady ? (
        <p>
          Configure any remaining patches, then re-enable playerbots before inviting players. Restart the
          worldserver after changing this setting.
        </p>
      ) : (
        <p>
          The stack is running with playerbots off. Prepare server-wide progression on the Patches tab —
          click <strong>Prepare progression</strong>, then{' '}
          <strong>Sync with mod-individual-progression</strong> — before re-enabling playerbots.
        </p>
      )}
      {playerbots.toggleError && (
        <p className="text-red-700">
          {(playerbots.toggleError as Error)?.message ?? 'Failed to update playerbots.conf.'}
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        <a
          href={ctx.patchesHref}
          className="inline-flex items-center gap-2 rounded-md border border-violet-300 bg-white px-4 py-2 text-sm font-medium text-violet-800 hover:bg-violet-50"
        >
          <TrendingUp className="h-4 w-4" />
          Open Patches tab
        </a>
      </div>
    </div>
  )
}

function ReenableAction(ctx: SetupStepContext) {
  const playerbots = usePlayerbotsConf(ctx.stack.stackId)
  const [showConfirm, setShowConfirm] = useState(false)
  const progressionReady = isIpProgressionReady(ctx.status)

  const reenable = () => {
    playerbots.toggle(true, {
      onSuccess: () => ctx.status.progress.markPlayerbotsSetupComplete(),
      onSettled: () => setShowConfirm(false),
    })
  }

  return (
    <>
      {setupActionButton(
        'Re-enable playerbots',
        () => {
          if (!progressionReady) {
            setShowConfirm(true)
            return
          }
          reenable()
        },
        {
          disabled: playerbots.isToggling || !playerbots.path,
          pending: playerbots.isToggling,
          icon: <RotateCcw className="h-4 w-4" />,
          tone: 'blue',
        },
      )}
      {showConfirm && (
        <ReenableConfirmDialog
          onCancel={() => setShowConfirm(false)}
          onConfirm={reenable}
          pending={playerbots.isToggling}
        />
      )}
    </>
  )
}

export function reenablePlayerbotsStep(): SetupStep {
  return {
    id: STEP_IDS.reenablePlayerbots,
    moduleId: MODULE_IDS.playerbots,
    level: 'warning',
    title: 'Re-enable playerbots',
    defaultExpanded: true,
    applies: (ctx) => hasPlayerbotsModule(ctx.stack) && isPlayerbotsDisabled(ctx.status),
    isComplete: (ctx) => isPlayerbotsSetupComplete(ctx.status),
    summary: () => 'Re-enable playerbots after progression is prepared on the Patches tab.',
    Component: (ctx) => <ReenableDetails {...ctx} />,
    Action: (ctx) => <ReenableAction {...ctx} />,
  }
}
