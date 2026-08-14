import { useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Bot, Loader2, Play, RotateCcw, TrendingUp } from 'lucide-react'
import IndividualProgressionSyncHint from '@/components/modules/IndividualProgressionSyncHint'
import { usePatchOverview, useProgressionSyncStatus } from '@/hooks/usePatches'
import { useServerConfig, useServerConfigs } from '@/hooks/useServerFiles'
import { useStackJob } from '@/hooks/useStackJob'
import { stackKeys } from '@/hooks/useStacks'
import { getConfValue, setConfValue } from '@/lib/conf-file'
import { serverConfigApi, stackApi } from '@/services/api'
import { StackStatus } from '@/types/stack.types'

const PLAYERBOTS_ENABLED_KEY = 'AiPlayerbot.Enabled'

const setupCompleteKey = (stackId: string) => `azp_ip_playerbots_setup_${stackId}`
const phaseKey = (stackId: string) => `azp_ip_playerbots_phase_${stackId}`

type IpPlayerbotsPhase = 'awaiting-start' | 'awaiting-reenable'

export function isIpPlayerbotsSetupComplete(stackId: string): boolean {
  try {
    return localStorage.getItem(setupCompleteKey(stackId)) === '1'
  } catch {
    return false
  }
}

function getIpPlayerbotsPhase(stackId: string): IpPlayerbotsPhase | null {
  try {
    const value = localStorage.getItem(phaseKey(stackId))
    return value === 'awaiting-start' || value === 'awaiting-reenable' ? value : null
  } catch {
    return null
  }
}

function markIpPlayerbotsSetupComplete(stackId: string) {
  try {
    localStorage.setItem(setupCompleteKey(stackId), '1')
    localStorage.removeItem(phaseKey(stackId))
  } catch {
    /* ignore storage errors */
  }
}

function setIpPlayerbotsPhase(stackId: string, phase: IpPlayerbotsPhase | null) {
  try {
    if (phase) {
      localStorage.setItem(phaseKey(stackId), phase)
    } else {
      localStorage.removeItem(phaseKey(stackId))
    }
  } catch {
    /* ignore storage errors */
  }
}

interface IndividualProgressionPlayerbotsSetupHintProps {
  stackId: string
  stackStatus: StackStatus
  patchesHref?: string
  className?: string
}

function ReenablePlayerbotsConfirmDialog({
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

/**
 * Guides operators through temporarily disabling playerbots before the first launch on
 * Individual Progression stacks, then returns to the standard patch-sync hint.
 */
export default function IndividualProgressionPlayerbotsSetupHint({
  stackId,
  stackStatus,
  patchesHref,
  className = '',
}: IndividualProgressionPlayerbotsSetupHintProps) {
  const queryClient = useQueryClient()
  const setupComplete = isIpPlayerbotsSetupComplete(stackId)
  const [phase, setPhase] = useState<IpPlayerbotsPhase | null>(() => getIpPlayerbotsPhase(stackId))
  const [showReenableConfirm, setShowReenableConfirm] = useState(false)

  const { job: stackJob, isStackBusy, applyStatus } = useStackJob(stackId)

  const configsQuery = useServerConfigs(stackId)
  const playerbotsPath = useMemo(
    () =>
      configsQuery.data?.files.find((file) =>
        file.path.replace(/\\/g, '/').toLowerCase().endsWith('modules/playerbots.conf'),
      )?.path ?? null,
    [configsQuery.data?.files],
  )

  const configQuery = useServerConfig(stackId, playerbotsPath)
  const enabledValue = configQuery.data?.content
    ? getConfValue(configQuery.data.content, PLAYERBOTS_ENABLED_KEY)
    : null
  const playerbotsDisabled = enabledValue === '0'

  const stackReadyForProgressionPrep =
    stackStatus === StackStatus.Running ||
    stackStatus === StackStatus.Degraded ||
    phase === 'awaiting-reenable'
  const { data: patchOverview } = usePatchOverview(
    stackReadyForProgressionPrep && playerbotsDisabled ? stackId : '',
  )
  const { data: progressionSyncStatus } = useProgressionSyncStatus(
    playerbotsDisabled ? stackId : '',
  )
  const hasCompletedProgressionSync =
    progressionSyncStatus?.hasCompletedInitialSync === true ||
    !!progressionSyncStatus?.lastSyncAt
  const showProgressionPrepHint =
    playerbotsDisabled &&
    stackReadyForProgressionPrep &&
    !(patchOverview?.individualProgressionBootstrapped ?? false)
  const progressionNotReady =
    stackReadyForProgressionPrep &&
    (!(patchOverview?.individualProgressionBootstrapped ?? false) || !hasCompletedProgressionSync)

  const updatePhase = (next: IpPlayerbotsPhase | null) => {
    setIpPlayerbotsPhase(stackId, next)
    setPhase(next)
  }

  const toggleMutation = useMutation({
    mutationFn: async (enabled: boolean) => {
      if (!playerbotsPath) {
        throw new Error('playerbots.conf is not available yet. Finish the stack build first.')
      }

      const current = configQuery.data?.content
        ?? (await serverConfigApi.read(stackId, playerbotsPath)).data.content

      const nextContent = setConfValue(current, PLAYERBOTS_ENABLED_KEY, enabled ? '1' : '0')
      await serverConfigApi.save(stackId, playerbotsPath, nextContent)
    },
    onSuccess: (_data, enabled) => {
      if (enabled) {
        markIpPlayerbotsSetupComplete(stackId)
        updatePhase(null)
      } else {
        updatePhase('awaiting-start')
      }
      queryClient.invalidateQueries({ queryKey: ['server-config', stackId] })
    },
  })

  const requestReenablePlayerbots = () => {
    if (progressionNotReady) {
      setShowReenableConfirm(true)
      return
    }
    toggleMutation.mutate(true)
  }

  const confirmReenablePlayerbots = () => {
    toggleMutation.mutate(true, {
      onSettled: () => setShowReenableConfirm(false),
    })
  }

  const startStackMutation = useMutation({
    mutationFn: async () => {
      const canStart =
        stackStatus === StackStatus.Stopped ||
        stackStatus === StackStatus.Failed ||
        stackStatus === StackStatus.Degraded
      const canRestart =
        stackStatus === StackStatus.Running || stackStatus === StackStatus.Degraded

      if (canStart) {
        return stackApi.start(stackId)
      }
      if (canRestart) {
        return stackApi.restart(stackId)
      }
      throw new Error('The stack cannot be started right now. Wait for the current operation to finish.')
    },
    onSuccess: (res) => {
      applyStatus(res.data)
      updatePhase('awaiting-reenable')
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    },
  })

  const isBuilding = stackStatus === StackStatus.Building
  const isRunning = stackStatus === StackStatus.Running
  const isTransitioning =
    stackStatus === StackStatus.Starting || stackStatus === StackStatus.Initializing

  const canStartStack =
    stackStatus === StackStatus.Stopped ||
    stackStatus === StackStatus.Failed ||
    stackStatus === StackStatus.Degraded ||
    isRunning

  const isStartingStack =
    startStackMutation.isPending || isStackBusy || isTransitioning

  const startButtonLabel = useMemo(() => {
    if (isStartingStack) {
      return 'Starting stack…'
    }
    if (isRunning) {
      return 'Restart stack'
    }
    return 'Start stack'
  }, [isStartingStack, isRunning])

  if (setupComplete) {
    return (
      <IndividualProgressionSyncHint
        stackId={stackId}
        patchesHref={patchesHref}
        className={className}
      />
    )
  }

  if (configsQuery.isLoading || (playerbotsPath && configQuery.isLoading)) {
    return (
      <div className={`rounded-lg border border-violet-200 bg-violet-50 px-5 py-4 text-sm text-violet-800 ${className}`.trim()}>
        <Loader2 className="inline h-4 w-4 animate-spin mr-2" />
        Loading playerbots configuration…
      </div>
    )
  }

  if (playerbotsDisabled && phase === 'awaiting-start') {
    return (
      <div className={`rounded-xl border-2 border-green-400 bg-green-50 px-6 py-5 shadow-sm ${className}`.trim()}>
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div className="flex min-w-0 items-start gap-3">
            <Play className="mt-0.5 h-6 w-6 shrink-0 text-green-600" aria-hidden="true" />
            <div>
              <p className="text-base font-semibold text-green-950">Playerbots disabled — start the stack</p>
              <p className="mt-2 text-sm leading-relaxed text-green-900">
                Start the entire stack now so the worldserver loads with playerbots turned off. After it is
                up, configure patches and other content before re-enabling playerbots.
              </p>
              {isBuilding && (
                <p className="mt-2 text-sm text-green-800">
                  Wait for the current build to finish before starting the stack.
                </p>
              )}
            </div>
          </div>
          <button
            type="button"
            onClick={() => startStackMutation.mutate()}
            disabled={!canStartStack || isStartingStack || isBuilding}
            className="inline-flex shrink-0 items-center gap-2 rounded-md bg-green-600 px-5 py-2.5 text-sm font-semibold text-white hover:bg-green-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isStartingStack ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <Play className="h-4 w-4" />
            )}
            {startButtonLabel}
          </button>
        </div>
        {startStackMutation.isError && (
          <p className="mt-3 text-sm text-red-700">
            {(startStackMutation.error as Error)?.message ?? 'Failed to start the stack.'}
          </p>
        )}
        {isStackBusy && stackJob?.message && (
          <p className="mt-3 text-sm text-green-800">{stackJob.message}</p>
        )}
      </div>
    )
  }

  if (playerbotsDisabled) {
    return (
      <>
        <div className={`rounded-xl border border-blue-200 bg-blue-50 px-6 py-5 shadow-sm ${className}`.trim()}>
          <div className="flex min-w-0 items-start gap-3">
            <Bot className="mt-0.5 h-5 w-5 shrink-0 text-blue-600" aria-hidden="true" />
            <div className="min-w-0 flex-1">
              <p className="text-sm font-semibold text-blue-900">Playerbots are disabled</p>
              {showProgressionPrepHint ? (
                <p className="mt-2 text-sm leading-relaxed text-blue-900">
                  The stack is running with playerbots off. Prepare server-wide progression on the
                  Patches tab — click <strong>Prepare progression</strong>, then{' '}
                  <strong>Sync with mod-individual-progression</strong> — before re-enabling
                  playerbots.
                </p>
              ) : (
                <p className="mt-1 text-sm text-blue-800">
                  Configure patches and other content, then re-enable playerbots before inviting
                  players. Restart the worldserver after changing this setting.
                </p>
              )}
            </div>
          </div>

          <div className="mt-4 flex flex-wrap justify-end gap-2">
            {patchesHref && (
              <a
                href={patchesHref}
                className="inline-flex items-center gap-2 rounded-md border border-violet-300 bg-white px-4 py-2 text-sm font-medium text-violet-800 hover:bg-violet-50"
              >
                <TrendingUp className="h-4 w-4" />
                Open Patches tab
              </a>
            )}
            <button
              type="button"
              onClick={requestReenablePlayerbots}
              disabled={toggleMutation.isPending || !playerbotsPath}
              className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {toggleMutation.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <RotateCcw className="h-4 w-4" />
              )}
              Re-enable playerbots
            </button>
          </div>

          {toggleMutation.isError && (
            <p className="mt-3 text-sm text-red-700">
              {(toggleMutation.error as Error)?.message ?? 'Failed to update playerbots.conf.'}
            </p>
          )}
        </div>

        {showReenableConfirm && (
          <ReenablePlayerbotsConfirmDialog
            onCancel={() => setShowReenableConfirm(false)}
            onConfirm={confirmReenablePlayerbots}
            pending={toggleMutation.isPending}
          />
        )}
      </>
    )
  }

  return (
    <div className={`rounded-xl border-2 border-amber-400 bg-amber-50 px-6 py-5 shadow-sm ${className}`.trim()}>
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="flex min-w-0 items-start gap-3">
          <AlertTriangle className="mt-1 h-6 w-6 shrink-0 text-amber-600" aria-hidden="true" />
          <div>
            <p className="text-base font-semibold text-amber-950">
              Disable playerbots before your first launch
            </p>
            <p className="mt-2 text-sm leading-relaxed text-amber-900">
              Individual Progression stacks should configure patches and other content before bots start
              spawning. Disable playerbots now, start the stack, then sync progression on the Patches tab
              and re-enable playerbots when you are ready.
            </p>
            {!playerbotsPath && (
              <p className="mt-2 text-sm text-amber-800">
                Complete the initial stack build first — <code className="rounded bg-amber-100 px-1 text-xs">playerbots.conf</code>{' '}
                becomes available after module configs are seeded.
              </p>
            )}
          </div>
        </div>
        <button
          type="button"
          onClick={() => toggleMutation.mutate(false)}
          disabled={toggleMutation.isPending || !playerbotsPath}
          className="inline-flex shrink-0 items-center gap-2 rounded-md bg-amber-600 px-5 py-2.5 text-sm font-semibold text-white hover:bg-amber-700 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {toggleMutation.isPending ? (
            <Loader2 className="h-4 w-4 animate-spin" />
          ) : (
            <Bot className="h-4 w-4" />
          )}
          Disable playerbots
        </button>
      </div>
      {toggleMutation.isError && (
        <p className="mt-3 text-sm text-red-700">
          {(toggleMutation.error as Error)?.message ?? 'Failed to update playerbots.conf.'}
        </p>
      )}
    </div>
  )
}
