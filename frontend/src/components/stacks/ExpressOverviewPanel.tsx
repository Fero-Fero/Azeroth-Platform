import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Download, Loader2, Play, Rocket, X } from 'lucide-react'
import { useExpressProvision } from '@/hooks/useExpressProvision'
import { useLauncherBuildStatus } from '@/hooks/useLauncher'
import { stackKeys } from '@/hooks/useStacks'
import { apiErrorMessage } from '@/lib/utils'
import { launcherApi, stackApi } from '@/services/api'
import { expressPhaseLabel } from '@/setup/steps/express/expressPhases'
import type { StackDetailsDto } from '@/types/stack.types'
import { ServerType, StackStatus } from '@/types/stack.types'

interface ExpressOverviewPanelProps {
  stack: StackDetailsDto
  canStart: boolean
  isStarting: boolean
  onStartStack: () => void
}

export default function ExpressOverviewPanel({
  stack,
  canStart,
  isStarting,
  onStartStack,
}: ExpressOverviewPanelProps) {
  const queryClient = useQueryClient()
  const status = stack.expressProvisionStatus
  const completed = status === 'Completed'
  const { data: launcher } = useLauncherBuildStatus(completed)
  const downloadReady = launcher?.downloadAvailable === true
  const { start: startProvision, retry: retryProvision } = useExpressProvision(stack.stackId)

  const dismissReady = useMutation({
    mutationFn: () => stackApi.dismissExpressReadyNotice(stack.stackId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: stackKeys.detail(stack.stackId) })
    },
  })

  if (stack.configuration.serverType !== ServerType.Express) {
    return null
  }

  const busy = startProvision.isPending || retryProvision.isPending
  const error =
    startProvision.isError
      ? apiErrorMessage(startProvision.error, 'Could not start Express Setup.')
      : retryProvision.isError
        ? apiErrorMessage(retryProvision.error, 'Could not retry Express Setup.')
        : null

  if (status === 'Pending' && stack.hasCompletedBuild && stack.status !== StackStatus.Building) {
    return (
      <div className="mb-8 overflow-hidden rounded-xl border-2 border-emerald-400 bg-gradient-to-r from-emerald-50 to-white shadow-md">
        <div className="px-6 py-5">
          <h2 className="text-xl font-bold text-emerald-950">Express Setup</h2>
          <p className="mt-1 text-sm text-emerald-900">
            {stack.expressProvisionMessage ||
              'The first build is done. Setup and Launch will disable playerbots, boot the server, configure SOAP, then turn bots back on, build the launcher, and ask for a client.'}
          </p>
          {error && <p className="mt-2 text-sm text-red-700">{error}</p>}
          <button
            type="button"
            disabled={busy}
            onClick={() => startProvision.mutate()}
            className="mt-4 inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-6 py-3 text-base font-semibold text-white shadow hover:bg-emerald-700 disabled:opacity-50"
          >
            {busy ? <Loader2 className="h-5 w-5 animate-spin" /> : <Rocket className="h-5 w-5" />}
            Setup and Launch!
          </button>
        </div>
      </div>
    )
  }

  if (status === 'Failed') {
    return (
      <div className="mb-8 rounded-xl border-2 border-red-300 bg-red-50 px-6 py-5">
        <h2 className="text-lg font-bold text-red-950">Express Setup failed</h2>
        <p className="mt-1 text-sm text-red-800">
          Stopped at {expressPhaseLabel(stack.expressProvisionPhase)}.
          {stack.expressProvisionMessage ? ` ${stack.expressProvisionMessage}` : ''}
        </p>
        {error && <p className="mt-2 text-sm text-red-700">{error}</p>}
        <button
          type="button"
          disabled={busy}
          onClick={() => retryProvision.mutate()}
          className="mt-4 inline-flex items-center gap-2 rounded-lg bg-red-600 px-5 py-2.5 text-sm font-semibold text-white hover:bg-red-700 disabled:opacity-50"
        >
          {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
          Retry from {expressPhaseLabel(stack.expressProvisionPhase)}
        </button>
      </div>
    )
  }

  if (completed) {
    return (
      <div className="mb-8 space-y-3">
        {stack.expressReadyNoticePending && (
          <div className="relative overflow-hidden rounded-xl border-2 border-amber-400 bg-amber-50 px-6 py-5 shadow-md">
            <button
              type="button"
              className="absolute right-3 top-3 rounded p-1 text-amber-800 hover:bg-amber-100"
              aria-label="Dismiss"
              onClick={() => dismissReady.mutate()}
            >
              <X className="h-4 w-4" />
            </button>
            <h2 className="text-xl font-bold text-amber-950">All ready — press Start!</h2>
            <p className="mt-1 text-sm text-amber-900">
              Express Setup finished. Game account admin / admin (GM 3). Start the stack when you want to play.
            </p>
            <button
              type="button"
              disabled={!canStart || isStarting}
              onClick={() => {
                onStartStack()
                dismissReady.mutate()
              }}
              className="mt-4 inline-flex items-center gap-2 rounded-lg bg-green-600 px-6 py-3 text-base font-semibold text-white shadow hover:bg-green-700 disabled:opacity-50"
            >
              {isStarting ? <Loader2 className="h-5 w-5 animate-spin" /> : <Play className="h-5 w-5" />}
              Start
            </button>
          </div>
        )}
        <div className="rounded-xl border border-gray-200 bg-white px-6 py-4 shadow-sm">
          <p className="text-sm text-gray-700">Download the launcher for this Express server.</p>
          <a
            href={launcherApi.downloadUrl()}
            className={`mt-3 inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-semibold ${
              downloadReady
                ? 'bg-purple-600 text-white hover:bg-purple-700'
                : 'pointer-events-none bg-gray-200 text-gray-500'
            }`}
          >
            <Download className="h-4 w-4" />
            {downloadReady ? 'Download launcher' : 'Launcher not ready yet'}
          </a>
        </div>
      </div>
    )
  }

  return null
}
