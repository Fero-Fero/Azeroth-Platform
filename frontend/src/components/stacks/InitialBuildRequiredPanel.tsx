import { useQuery } from '@tanstack/react-query'
import { AlertCircle, Hammer, Loader2, Trash2 } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import { buildApi } from '@/services/api'
import { BuildPhase, StackStatus, type StackDetailsDto } from '@/types/stack.types'

interface InitialBuildRequiredPanelProps {
  stack: StackDetailsDto
  stackId: string
  onRetryBuild: () => void
  isRetrying: boolean
  onDelete: () => void
  isDeleting: boolean
}

export default function InitialBuildRequiredPanel({
  stack,
  stackId,
  onRetryBuild,
  isRetrying,
  onDelete,
  isDeleting,
}: InitialBuildRequiredPanelProps) {
  const navigate = useNavigate()

  const buildStatusQuery = useQuery({
    queryKey: ['build-status', stackId],
    queryFn: async () => (await buildApi.status(stackId)).data,
    enabled: stack.status !== StackStatus.Building,
    retry: false,
  })

  const buildStatus = buildStatusQuery.data
  const buildFailed =
    buildStatus?.currentPhase === BuildPhase.Failed ||
    (buildStatus?.errorMessage?.length ?? 0) > 0
  const errorMessage =
    buildStatus?.errorMessage ??
    (buildStatusQuery.isError ? 'No build record found — the initial build may not have started.' : null)

  if (stack.status === StackStatus.Building) {
    return (
      <div className="mx-auto max-w-2xl mt-12">
        <div className="rounded-xl border border-blue-200 bg-blue-50 p-8 text-center">
          <Loader2 className="mx-auto h-10 w-10 animate-spin text-blue-600" />
          <h2 className="mt-4 text-xl font-semibold text-gray-900">Initial build in progress</h2>
          <p className="mt-2 text-sm text-gray-600">
            Your stack is being compiled for the first time. This usually takes 15–30 minutes.
          </p>
          <button
            type="button"
            onClick={() => navigate(`/stacks/${stackId}/build`)}
            className="mt-6 inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
          >
            View build progress
          </button>
        </div>
      </div>
    )
  }

  return (
    <div className="mx-auto max-w-2xl mt-12">
      <button
        type="button"
        onClick={() => navigate('/stacks')}
        className="mb-4 text-sm text-gray-600 hover:text-gray-800"
      >
        ← Back to Stacks
      </button>

      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 bg-gray-50 px-6 py-5">
          <h1 className="text-2xl font-bold text-gray-900">{stack.stackName}</h1>
          <p className="mt-1 text-sm text-gray-500">
            Created {new Date(stack.createdAt).toLocaleDateString()} • {stack.serverType}
          </p>
        </div>

        <div className="space-y-5 px-6 py-6">
          <div
            className={`rounded-lg border p-4 ${
              buildFailed ? 'border-red-200 bg-red-50' : 'border-amber-200 bg-amber-50'
            }`}
          >
            <div className="flex gap-3">
              <AlertCircle
                className={`h-5 w-5 shrink-0 ${buildFailed ? 'text-red-600' : 'text-amber-600'}`}
              />
              <div>
                <h2 className={`font-semibold ${buildFailed ? 'text-red-900' : 'text-amber-900'}`}>
                  {buildFailed ? 'Initial build failed' : 'Setup not complete'}
                </h2>
                <p className={`mt-1 text-sm ${buildFailed ? 'text-red-800' : 'text-amber-800'}`}>
                  {buildFailed
                    ? 'The first build did not finish successfully. Retry the build before using this stack.'
                    : 'This stack has not completed its first build yet. Start or retry the build to finish setup.'}
                </p>
                {errorMessage && (
                  <p className="mt-2 rounded-md bg-white/70 px-3 py-2 font-mono text-xs text-gray-800">
                    {errorMessage}
                  </p>
                )}
              </div>
            </div>
          </div>

          {buildStatus?.recentLogs && buildStatus.recentLogs.length > 0 && (
            <div>
              <h3 className="mb-2 text-sm font-medium text-gray-700">Recent build logs</h3>
              <div className="max-h-48 overflow-y-auto rounded-lg bg-gray-900 p-3 font-mono text-xs text-green-400">
                {buildStatus.recentLogs.slice(-12).map((line, index) => (
                  <div key={index}>{line}</div>
                ))}
              </div>
            </div>
          )}

          <div className="flex flex-wrap gap-3">
            <button
              type="button"
              onClick={onRetryBuild}
              disabled={isRetrying || isDeleting}
              className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
            >
              {isRetrying ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Starting build…
                </>
              ) : (
                <>
                  <Hammer className="h-4 w-4" />
                  {buildFailed ? 'Retry build' : 'Start build'}
                </>
              )}
            </button>
            <button
              type="button"
              onClick={onDelete}
              disabled={isRetrying || isDeleting}
              className="inline-flex items-center gap-2 rounded-lg border border-red-300 px-4 py-2 text-sm font-medium text-red-700 hover:bg-red-50 disabled:opacity-50"
            >
              {isDeleting ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Deleting…
                </>
              ) : (
                <>
                  <Trash2 className="h-4 w-4" />
                  Delete stack
                </>
              )}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
