import { useEffect, useState, useRef } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { CheckCircle2, XCircle, Loader2, Terminal, AlertCircle } from 'lucide-react'
import { useBuildProgress } from '@/hooks/useBuildProgress'
import { buildApi } from '@/services/api'
import { BuildPhase } from '@/types/stack.types'

const PHASE_LABELS: Record<BuildPhase, string> = {
  [BuildPhase.Cloning]: 'Cloning Repository',
  [BuildPhase.PreparingModules]: 'Preparing Modules',
  [BuildPhase.Building]: 'Building',
  [BuildPhase.CreatingImages]: 'Creating Images',
  [BuildPhase.Completed]: 'Completed',
  [BuildPhase.Failed]: 'Failed',
}

export default function BuildProgressPage() {
  const { stackId } = useParams<{ stackId: string }>()
  const navigate = useNavigate()
  const { progress, isComplete, error } = useBuildProgress(stackId ?? null)
  const [autoScrollLogs, setAutoScrollLogs] = useState(true)
  const logsEndRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (autoScrollLogs && logsEndRef.current) {
      logsEndRef.current.scrollIntoView({ behavior: 'smooth' })
    }
  }, [progress.logs, autoScrollLogs])

  const handleCancel = async () => {
    if (!stackId) return

    try {
      await buildApi.cancel(stackId)
    } catch (err) {
      console.error('Failed to cancel build:', err)
    }
  }

  const handleViewStack = () => {
    if (stackId) {
      navigate(`/stacks/${stackId}`)
    }
  }

  const handleBackToList = () => {
    navigate('/stacks')
  }

  if (!stackId) {
    return (
      <div className="text-center">
        <p className="text-gray-500">No stack ID provided</p>
      </div>
    )
  }

  const isFailed = error !== null || progress.phase === BuildPhase.Failed
  const isSuccess = isComplete && !isFailed

  return (
    <div className="mx-auto max-w-4xl">
      <div className="mb-6">
        <h1 className="text-3xl font-bold text-gray-900">Build Progress</h1>
        <p className="mt-1 text-gray-500">
          Building your AzerothCore stack...
        </p>
      </div>

      <div className="space-y-6">
        {/* Status Card */}
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 bg-gray-50 px-6 py-4">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-3">
                {!isComplete && (
                  <Loader2 className="h-6 w-6 animate-spin text-blue-600" />
                )}
                {isSuccess && (
                  <CheckCircle2 className="h-6 w-6 text-green-600" />
                )}
                {isFailed && (
                  <XCircle className="h-6 w-6 text-red-600" />
                )}
                <div>
                  <h2 className="text-lg font-semibold text-gray-900">
                    {PHASE_LABELS[progress.phase]}
                  </h2>
                  <p className="text-sm text-gray-500">{progress.step}</p>
                </div>
              </div>
              <div className="text-right">
                <div className="text-2xl font-bold text-gray-900">
                  {progress.percent}%
                </div>
              </div>
            </div>
          </div>

          <div className="px-6 py-4">
            {/* Progress Bar */}
            <div className="mb-4">
              <div className="h-2 w-full overflow-hidden rounded-full bg-gray-200">
                <div
                  className={`h-full transition-all duration-300 ${
                    isFailed ? 'bg-red-600' : isSuccess ? 'bg-green-600' : 'bg-blue-600'
                  }`}
                  style={{ width: `${progress.percent}%` }}
                />
              </div>
            </div>

            {/* Error Message */}
            {error && (
              <div className="mb-4 rounded-lg border border-red-200 bg-red-50 p-4">
                <div className="flex gap-3">
                  <AlertCircle className="h-5 w-5 shrink-0 text-red-600" />
                  <div>
                    <h3 className="font-semibold text-red-900">Build Failed</h3>
                    <p className="mt-1 text-sm text-red-700">{error}</p>
                  </div>
                </div>
              </div>
            )}

            {/* Success Message */}
            {isSuccess && (
              <div className="mb-4 rounded-lg border border-green-200 bg-green-50 p-4">
                <div className="flex gap-3">
                  <CheckCircle2 className="h-5 w-5 shrink-0 text-green-600" />
                  <div>
                    <h3 className="font-semibold text-green-900">Build Completed</h3>
                    <p className="mt-1 text-sm text-green-700">
                      Your AzerothCore stack has been built successfully and is ready to start.
                    </p>
                  </div>
                </div>
              </div>
            )}

            {/* Actions */}
            <div className="flex flex-wrap gap-3">
              {!isComplete && (
                <button
                  type="button"
                  onClick={handleCancel}
                  className="rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  Cancel Build
                </button>
              )}
              {isFailed && (
                <button
                  type="button"
                  onClick={() => navigate(`/stacks/${stackId}`)}
                  className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  Back to Stack
                </button>
              )}
              {isSuccess && (
                <button
                  type="button"
                  onClick={handleViewStack}
                  className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  View Stack
                </button>
              )}
              {isComplete && (
                <button
                  type="button"
                  onClick={handleBackToList}
                  className="rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  Back to Stacks
                </button>
              )}
            </div>
          </div>
        </div>

        {/* Build Logs */}
        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 bg-gray-50 px-6 py-4">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-3">
                <Terminal className="h-5 w-5 text-gray-600" />
                <h2 className="text-lg font-semibold text-gray-900">Build Logs</h2>
              </div>
              <label className="flex items-center gap-2 text-sm text-gray-600">
                <input
                  type="checkbox"
                  checked={autoScrollLogs}
                  onChange={(e) => setAutoScrollLogs(e.target.checked)}
                  className="rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                />
                Auto-scroll
              </label>
            </div>
          </div>

          <div className="bg-gray-900 p-6">
            <div className="h-96 overflow-y-auto font-mono text-sm">
              {progress.logs.length === 0 ? (
                <p className="text-gray-500">Waiting for build logs...</p>
              ) : (
                <div className="space-y-1">
                  {progress.logs.map((log, index) => (
                    <div key={index} className="text-green-400">
                      {log}
                    </div>
                  ))}
                  <div ref={logsEndRef} />
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
