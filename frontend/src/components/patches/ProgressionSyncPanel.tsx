import { useEffect, useState } from 'react'
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
  const syncMutation = useRunProgressionSync(stackId)
  const resolveMutation = useResolveProgressionOptionalFiles(stackId)

  const [showIgnored, setShowIgnored] = useState(false)
  const [pendingFiles, setPendingFiles] = useState<ProgressionSyncPendingFile[]>([])
  const [pendingDecisions, setPendingDecisions] = useState<Record<string, boolean>>({})
  const [syncLog, setSyncLog] = useState<string[]>([])
  const [syncError, setSyncError] = useState<string | null>(null)
  const [syncSuccess, setSyncSuccess] = useState<string | null>(null)
  const [showInitialSyncConfirm, setShowInitialSyncConfirm] = useState(false)
  const [pollSync, setPollSync] = useState(false)

  const { data: syncStatus, isLoading } = useProgressionSyncStatus(
    stackId,
    pollSync || syncMutation.isPending
  )

  useEffect(() => {
    if (syncStatus?.isRunning) {
      setPollSync(true)
    } else if (!syncMutation.isPending) {
      setPollSync(false)
    }
  }, [syncStatus?.isRunning, syncMutation.isPending])

  const isInitialSync = !syncStatus?.hasCompletedInitialSync && !syncStatus?.lastSyncAt
  const showProgressBar =
    syncMutation.isPending || pollSync || (syncStatus?.isRunning ?? false)
  const progressPercent = syncStatus?.progressPercent ?? (syncMutation.isPending ? 5 : 0)
  const progressMessage =
    syncStatus?.message ?? (syncMutation.isPending ? 'Starting progression sync…' : '')
  const liveLog =
    showProgressBar && (syncStatus?.log?.length ?? 0) > 0 ? syncStatus!.log : syncLog

  const runSync = async () => {
    setSyncError(null)
    setSyncSuccess(null)
    setPendingFiles([])
    setSyncLog([])
    setPollSync(true)

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

  const handleSyncClick = () => {
    setSyncError(null)
    setSyncSuccess(null)
    if (isInitialSync) {
      setShowInitialSyncConfirm(true)
      return
    }
    void runSync()
  }

  const handleConfirmInitialSync = () => {
    setShowInitialSyncConfirm(false)
    void runSync()
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
            Automatically import patch files from{' '}
            <code className="rounded bg-indigo-100 px-1 text-xs">Azeroth-Platform-Progression</code>{' '}
            and <code className="rounded bg-indigo-100 px-1 text-xs">mod-individual-progression</code>.
            Both repositories are updated first (<code className="rounded bg-indigo-100 px-1 text-xs">git pull</code>),
            patch folders are created from the progression repository layout, its files are copied in, and then
            optional module mappings are imported. Later syncs only modify managed progression patches; custom
            patches are left unchanged.
          </p>
          <p className="mt-2 max-w-2xl text-xs text-indigo-700 font-mono whitespace-pre-wrap">
            {`{BuildsPath}/{stackId}/
├── azeroth-platform-progression/   ← cloned on first sync, git pull after
├── migrations/                       ← patch folders
└── azerothcore-wotlk/
    └── modules/mod-individual-progression/`}
          </p>
          <p className="mt-2 max-w-2xl text-xs text-indigo-800">
            The progression repository lives on the stack only — nothing is cloned beside the platform or
            onto the host outside the stack data directory. Validation compares{' '}
            <code className="rounded bg-indigo-100 px-1">migrations/</code> against that on-stack checkout.
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
            onClick={handleSyncClick}
            disabled={showProgressBar}
            className="inline-flex items-center gap-2 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
          >
            {showProgressBar ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <RefreshCw className="h-4 w-4" />
            )}
            {syncStatus?.lastSyncAt ? 'Update & Re-sync' : 'Sync Now'}
          </button>
        </div>
      </div>

      {showInitialSyncConfirm && (
        <div className="rounded-md border border-amber-300 bg-amber-50 p-4 space-y-3">
          <div className="flex items-start gap-2">
            <AlertTriangle className="h-5 w-5 text-amber-600 shrink-0 mt-0.5" />
            <div>
              <p className="text-sm font-semibold text-amber-950">Overwrite existing patch content?</p>
              <p className="mt-1 text-sm text-amber-900">
                This is the first sync. It will create progression patch folders from
                Azeroth-Platform-Progression and copy in repository content, then import mapped files from
                mod-individual-progression. Any existing SQL, DBC, MPQ, or config files in those folders may
                be overwritten.
              </p>
              <p className="mt-2 text-sm text-amber-800">
                Later syncs only update managed progression patches and leave custom patches unchanged.
              </p>
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={handleConfirmInitialSync}
              disabled={syncMutation.isPending}
              className="inline-flex items-center gap-2 rounded-md bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-50"
            >
              {syncMutation.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
              Continue sync
            </button>
            <button
              type="button"
              onClick={() => setShowInitialSyncConfirm(false)}
              disabled={syncMutation.isPending}
              className="rounded-md border border-amber-300 bg-white px-4 py-2 text-sm font-medium text-amber-900 hover:bg-amber-100 disabled:opacity-50"
            >
              Cancel
            </button>
          </div>
        </div>
      )}

      {showProgressBar && (
        <div className="rounded-md border border-indigo-200 bg-white px-4 py-3 space-y-2">
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
            <div className="flex min-w-0 items-center gap-2 font-medium text-indigo-900">
              <Loader2 className="h-4 w-4 shrink-0 animate-spin" />
              <span className="truncate">{progressMessage || 'Syncing…'}</span>
            </div>
            <span className="shrink-0 tabular-nums text-indigo-700">{progressPercent}%</span>
          </div>
          <div
            className="h-2 w-full overflow-hidden rounded-full bg-indigo-100"
            role="progressbar"
            aria-valuenow={progressPercent}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-label="Progression sync progress"
          >
            <div
              className="h-full bg-indigo-600 transition-all duration-300"
              style={{ width: `${progressPercent}%` }}
            />
          </div>
          {syncStatus?.phase && (
            <p className="text-xs text-indigo-700">Phase: {syncStatus.phase}</p>
          )}
          {(liveLog?.length ?? 0) > 0 && (
            <pre className="max-h-32 overflow-auto whitespace-pre-wrap rounded-md border border-gray-200 bg-gray-50/80 p-2 text-xs font-mono text-gray-700">
              {liveLog.join('\n')}
            </pre>
          )}
        </div>
      )}

      {syncError && (
        <div className="rounded-md border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-700 flex items-center gap-2">
          <AlertTriangle className="h-4 w-4 shrink-0" />
          {syncError}
        </div>
      )}

      {syncSuccess && !showProgressBar && (
        <div className="rounded-md border border-green-200 bg-green-50 px-4 py-2 text-sm text-green-700 flex items-center gap-2">
          <CheckCircle2 className="h-4 w-4 shrink-0" />
          {syncSuccess}
        </div>
      )}

      {syncLog.length > 0 && !showProgressBar && (
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
