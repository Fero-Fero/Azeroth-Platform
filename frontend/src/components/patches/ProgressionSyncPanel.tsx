import { useState } from 'react'
import { Loader2, RefreshCw, Eye, CheckCircle2, AlertTriangle, FileQuestion } from 'lucide-react'
import {
  useProgressionSyncStatus,
  useRunProgressionSync,
  useResolveProgressionOptionalFiles,
} from '@/hooks/usePatches'
import type { ProgressionSyncPendingFile } from '@/types/individual-progression.types'
import IgnoredFilesDialog from './IgnoredFilesDialog'

interface ProgressionSyncPanelProps {
  stackId: string
}

export default function ProgressionSyncPanel({ stackId }: ProgressionSyncPanelProps) {
  const { data: syncStatus, isLoading } = useProgressionSyncStatus(stackId)
  const syncMutation = useRunProgressionSync(stackId)
  const resolveMutation = useResolveProgressionOptionalFiles(stackId)

  const [showIgnored, setShowIgnored] = useState(false)
  const [pendingFiles, setPendingFiles] = useState<ProgressionSyncPendingFile[]>([])
  const [pendingDecisions, setPendingDecisions] = useState<Record<string, boolean>>({})
  const [syncLog, setSyncLog] = useState<string[]>([])
  const [syncError, setSyncError] = useState<string | null>(null)
  const [syncSuccess, setSyncSuccess] = useState<string | null>(null)

  const handleSync = async () => {
    setSyncError(null)
    setSyncSuccess(null)
    setPendingFiles([])
    setSyncLog([])

    try {
      const res = await syncMutation.mutateAsync()
      setSyncLog(res.data.log)

      if (!res.data.success) {
        setSyncError(res.data.error ?? 'Sync failed.')
        return
      }

      if (res.data.pendingOptionalFiles.length > 0) {
        setPendingFiles(res.data.pendingOptionalFiles)
        const defaults: Record<string, boolean> = {}
        for (const file of res.data.pendingOptionalFiles) {
          defaults[file.source] = false
        }
        setPendingDecisions(defaults)
      } else {
        setSyncSuccess(
          `Synced successfully: ${res.data.copiedFiles} file(s) copied, ${res.data.skippedOptional} optional file(s) skipped.`
        )
      }
    } catch (err) {
      setSyncError(err instanceof Error ? err.message : 'Sync failed.')
    }
  }

  const handleResolveOptional = async () => {
    setSyncError(null)
    try {
      const res = await resolveMutation.mutateAsync(pendingDecisions)
      setPendingFiles([])
      setPendingDecisions({})
      setSyncLog(res.data.log)
      if (res.data.success) {
        setSyncSuccess(
          `Sync complete: ${res.data.copiedFiles} file(s) copied, ${res.data.skippedOptional} optional file(s) skipped.`
        )
      } else {
        setSyncError(res.data.error ?? 'Failed to resolve optional files.')
      }
    } catch (err) {
      setSyncError(err instanceof Error ? err.message : 'Failed to resolve optional files.')
    }
  }

  const toggleDecision = (source: string) => {
    setPendingDecisions((prev) => ({ ...prev, [source]: !prev[source] }))
  }

  if (isLoading) {
    return (
      <div className="flex items-center gap-2 py-4 text-sm text-gray-500">
        <Loader2 className="h-4 w-4 animate-spin" /> Loading sync status…
      </div>
    )
  }

  return (
    <section className="rounded-lg border border-indigo-200 bg-indigo-50/30 px-5 py-4 shadow-sm space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="text-sm font-semibold text-indigo-900">
            Sync with mod-individual-progression
          </p>
          <p className="mt-1 max-w-2xl text-sm text-indigo-800">
            Automatically import patch files from the local{' '}
            <code className="rounded bg-indigo-100 px-1 text-xs">mod-individual-progression</code>{' '}
            module and the{' '}
            <code className="rounded bg-indigo-100 px-1 text-xs">Azeroth-Platform-Progression</code>{' '}
            repository using the configured mapping rules.
          </p>
          {syncStatus?.lastSyncAt && (
            <p className="mt-1 text-xs text-indigo-700">
              Last synced: {new Date(syncStatus.lastSyncAt).toLocaleString()}
            </p>
          )}
        </div>
        <div className="flex shrink-0 flex-wrap items-center gap-2">
          {syncStatus?.hasOptionalFilesLog && (syncStatus.ignoredFilesCount ?? 0) > 0 && (
            <button
              type="button"
              onClick={() => setShowIgnored(true)}
              className="inline-flex items-center gap-1.5 rounded-md border border-indigo-300 bg-white px-3 py-2 text-sm font-medium text-indigo-800 hover:bg-indigo-50"
            >
              <Eye className="h-4 w-4" />
              View Ignored Files
              <span className="rounded-full bg-indigo-100 px-1.5 py-0.5 text-xs font-semibold text-indigo-700">
                {syncStatus.ignoredFilesCount}
              </span>
            </button>
          )}
          <button
            type="button"
            onClick={handleSync}
            disabled={syncMutation.isPending || syncStatus?.isRunning}
            className="inline-flex items-center gap-2 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
          >
            {syncMutation.isPending || syncStatus?.isRunning ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <RefreshCw className="h-4 w-4" />
            )}
            {syncStatus?.lastSyncAt ? 'Update & Re-sync' : 'Sync Now'}
          </button>
        </div>
      </div>

      {syncError && (
        <div className="rounded-md border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-700 flex items-center gap-2">
          <AlertTriangle className="h-4 w-4 shrink-0" />
          {syncError}
        </div>
      )}

      {syncSuccess && (
        <div className="rounded-md border border-green-200 bg-green-50 px-4 py-2 text-sm text-green-700 flex items-center gap-2">
          <CheckCircle2 className="h-4 w-4 shrink-0" />
          {syncSuccess}
        </div>
      )}

      {syncLog.length > 0 && (
        <pre className="rounded-md border border-gray-200 bg-gray-50/80 p-3 text-xs font-mono text-gray-700 max-h-40 overflow-auto whitespace-pre-wrap">
          {syncLog.join('\n')}
        </pre>
      )}

      {pendingFiles.length > 0 && (
        <div className="rounded-md border border-amber-200 bg-amber-50 p-4 space-y-3">
          <div className="flex items-start gap-2">
            <FileQuestion className="h-5 w-5 text-amber-600 shrink-0 mt-0.5" />
            <div>
              <p className="text-sm font-semibold text-amber-900">
                Optional files need your decision
              </p>
              <p className="text-sm text-amber-800 mt-1">
                The following optional files are not yet present in your patch directories. Choose
                which ones to include.
              </p>
            </div>
          </div>

          <ul className="space-y-1.5">
            {pendingFiles.map((file) => (
              <li
                key={file.source}
                className={`flex items-center justify-between rounded-md border px-3 py-2 text-sm ${
                  pendingDecisions[file.source]
                    ? 'border-green-200 bg-green-50'
                    : 'border-gray-200 bg-white'
                }`}
              >
                <label className="flex min-w-0 items-center gap-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={pendingDecisions[file.source] ?? false}
                    onChange={() => toggleDecision(file.source)}
                    className="h-4 w-4 rounded border-gray-300 text-green-600 focus:ring-green-500"
                  />
                  <span className="truncate font-mono text-xs">{file.fileName}</span>
                </label>
                <span className="shrink-0 text-xs text-gray-500 ml-2">
                  → {file.destination}
                </span>
              </li>
            ))}
          </ul>

          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={handleResolveOptional}
              disabled={resolveMutation.isPending}
              className="inline-flex items-center gap-2 rounded-md bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-50"
            >
              {resolveMutation.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
              Confirm Choices
            </button>
            <span className="text-xs text-gray-500">
              {Object.values(pendingDecisions).filter(Boolean).length} of{' '}
              {pendingFiles.length} selected for inclusion
            </span>
          </div>
        </div>
      )}

      {showIgnored && (
        <IgnoredFilesDialog
          stackId={stackId}
          onClose={() => setShowIgnored(false)}
        />
      )}
    </section>
  )
}
