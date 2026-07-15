import { useState } from 'react'
import { Camera, RotateCcw, Trash2, Loader2, AlertTriangle, Database } from 'lucide-react'
import {
  useRevisions,
  useCreateRevision,
  useRestoreRevision,
  useDeleteRevision,
} from '@/hooks/useServerFiles'
import type { RevisionDto } from '@/types/server.types'

function formatBytes(bytes: number): string {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const i = Math.floor(Math.log(bytes) / Math.log(1024))
  return `${(bytes / Math.pow(1024, i)).toFixed(i === 0 ? 0 : 1)} ${units[i]}`
}

function formatTimestamp(value: string): string {
  const date = new Date(value)
  return date.toLocaleString()
}

function statusBadge(status: string) {
  switch (status) {
    case 'ready':
      return 'bg-green-100 text-green-800 border-green-200'
    case 'creating':
      return 'bg-blue-100 text-blue-800 border-blue-200'
    case 'failed':
      return 'bg-red-100 text-red-800 border-red-200'
    default:
      return 'bg-gray-100 text-gray-800 border-gray-200'
  }
}

function reasonBadge(reason: string) {
  return reason === 'pre-update'
    ? 'bg-amber-100 text-amber-800 border-amber-200'
    : 'bg-indigo-100 text-indigo-800 border-indigo-200'
}

export default function RevisionsTab({ stackId }: { stackId: string }) {
  const { data: revisions, isLoading, error } = useRevisions(stackId)
  const createRevision = useCreateRevision(stackId)
  const restoreRevision = useRestoreRevision(stackId)
  const deleteRevision = useDeleteRevision(stackId)

  const [confirmRestore, setConfirmRestore] = useState<RevisionDto | null>(null)
  const [confirmDelete, setConfirmDelete] = useState<RevisionDto | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const busy = createRevision.isPending || restoreRevision.isPending || deleteRevision.isPending

  const handleCreate = async () => {
    setNotice(null)
    try {
      await createRevision.mutateAsync()
      setNotice('Snapshot created.')
    } catch (err: unknown) {
      setNotice(errorMessage(err, 'Failed to create snapshot.'))
    }
  }

  const handleRestore = async () => {
    if (!confirmRestore) return
    const target = confirmRestore
    setConfirmRestore(null)
    setNotice(null)
    try {
      await restoreRevision.mutateAsync(target.id)
      setNotice('Revision restored. Restart the stack to apply.')
    } catch (err: unknown) {
      setNotice(errorMessage(err, 'Failed to restore revision.'))
    }
  }

  const handleDelete = async () => {
    if (!confirmDelete) return
    const target = confirmDelete
    setConfirmDelete(null)
    try {
      await deleteRevision.mutateAsync(target.id)
    } catch (err: unknown) {
      setNotice(errorMessage(err, 'Failed to delete revision.'))
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex items-start justify-between gap-4">
        <p className="text-sm text-gray-600 max-w-2xl">
          Revisions are point-in-time snapshots of the world/auth/characters databases and the
          server <code className="font-mono">.conf</code> files. One is captured automatically before
          every <strong>Update</strong>. Restoring rolls the databases and config back; restart the
          stack afterwards for changes to take effect.
        </p>
        <button
          onClick={handleCreate}
          disabled={busy}
          className="shrink-0 inline-flex items-center gap-2 px-4 py-2 bg-indigo-600 text-white rounded hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition"
        >
          {createRevision.isPending ? (
            <Loader2 className="h-4 w-4 animate-spin" />
          ) : (
            <Camera className="h-4 w-4" />
          )}
          {createRevision.isPending ? 'Creating snapshot...' : 'Create snapshot'}
        </button>
      </div>

      {notice && (
        <div className="text-sm bg-blue-50 border border-blue-200 text-blue-800 rounded px-3 py-2">
          {notice}
        </div>
      )}

      {isLoading && <div className="text-sm text-gray-500">Loading revisions…</div>}

      {error && (
        <div className="text-sm bg-red-50 border border-red-200 text-red-700 rounded px-3 py-2">
          Failed to load revisions.
        </div>
      )}

      {revisions && revisions.length === 0 && (
        <div className="bg-gray-50 border border-gray-200 rounded-lg p-8 text-center">
          <Database className="h-8 w-8 text-gray-400 mx-auto mb-2" />
          <p className="text-gray-600">No revisions yet. Create a snapshot or run an update.</p>
        </div>
      )}

      {revisions && revisions.length > 0 && (
        <div className="overflow-hidden border border-gray-200 rounded-lg">
          <table className="min-w-full divide-y divide-gray-200 text-sm">
            <thead className="bg-gray-50">
              <tr>
                <th className="px-4 py-2 text-left font-medium text-gray-500">Created</th>
                <th className="px-4 py-2 text-left font-medium text-gray-500">Reason</th>
                <th className="px-4 py-2 text-left font-medium text-gray-500">Core</th>
                <th className="px-4 py-2 text-left font-medium text-gray-500">Patch</th>
                <th className="px-4 py-2 text-left font-medium text-gray-500">Size</th>
                <th className="px-4 py-2 text-left font-medium text-gray-500">Status</th>
                <th className="px-4 py-2 text-right font-medium text-gray-500">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-200 bg-white">
              {revisions.map((rev) => (
                <tr key={rev.id}>
                  <td className="px-4 py-2 text-gray-900 whitespace-nowrap">{formatTimestamp(rev.createdAt)}</td>
                  <td className="px-4 py-2">
                    <span className={`px-2 py-0.5 rounded-full text-xs font-medium border ${reasonBadge(rev.reason)}`}>
                      {rev.reason}
                    </span>
                  </td>
                  <td className="px-4 py-2 font-mono text-gray-700">
                    {rev.coreCommitSha ? rev.coreCommitSha.substring(0, 7) : '—'}
                  </td>
                  <td className="px-4 py-2 text-gray-700">{rev.appliedPatchLevel}</td>
                  <td className="px-4 py-2 text-gray-700 whitespace-nowrap">{formatBytes(rev.sizeBytes)}</td>
                  <td className="px-4 py-2">
                    <span className={`px-2 py-0.5 rounded-full text-xs font-medium border ${statusBadge(rev.status)}`} title={rev.error ?? undefined}>
                      {rev.status}
                    </span>
                  </td>
                  <td className="px-4 py-2">
                    <div className="flex items-center justify-end gap-2">
                      <button
                        onClick={() => setConfirmRestore(rev)}
                        disabled={busy || rev.status !== 'ready'}
                        className="inline-flex items-center gap-1 px-2.5 py-1 border border-blue-300 text-blue-700 rounded hover:bg-blue-50 disabled:opacity-40 disabled:cursor-not-allowed transition"
                        title="Restore this revision"
                      >
                        <RotateCcw className="h-3.5 w-3.5" />
                        Restore
                      </button>
                      <button
                        onClick={() => setConfirmDelete(rev)}
                        disabled={busy}
                        className="inline-flex items-center gap-1 px-2.5 py-1 border border-red-300 text-red-700 rounded hover:bg-red-50 disabled:opacity-40 disabled:cursor-not-allowed transition"
                        title="Delete this revision"
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Restore confirmation */}
      {confirmRestore && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
          <div className="bg-white rounded-lg p-6 max-w-md mx-4">
            <div className="flex items-start gap-3 mb-4">
              <AlertTriangle className="h-6 w-6 text-amber-500 shrink-0 mt-0.5" />
              <div>
                <h3 className="text-lg font-semibold mb-1">Restore this revision?</h3>
                <p className="text-sm text-gray-600">
                  This drops and recreates the <strong>world</strong>, <strong>auth</strong>, and{' '}
                  <strong>characters</strong> databases from the snapshot taken on{' '}
                  <strong>{formatTimestamp(confirmRestore.createdAt)}</strong>, and restores the
                  server config files. Current data will be lost. Restart the stack afterwards.
                </p>
              </div>
            </div>
            <div className="flex gap-3 justify-end">
              <button
                onClick={() => setConfirmRestore(null)}
                className="px-4 py-2 border border-gray-300 rounded hover:bg-gray-50 transition"
              >
                Cancel
              </button>
              <button
                onClick={handleRestore}
                className="px-4 py-2 bg-amber-600 text-white rounded hover:bg-amber-700 transition"
              >
                Restore
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Delete confirmation */}
      {confirmDelete && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
          <div className="bg-white rounded-lg p-6 max-w-md mx-4">
            <h3 className="text-lg font-semibold mb-2">Delete revision?</h3>
            <p className="text-sm text-gray-600 mb-6">
              Permanently delete the snapshot from{' '}
              <strong>{formatTimestamp(confirmDelete.createdAt)}</strong> and its dump files. This
              cannot be undone.
            </p>
            <div className="flex gap-3 justify-end">
              <button
                onClick={() => setConfirmDelete(null)}
                className="px-4 py-2 border border-gray-300 rounded hover:bg-gray-50 transition"
              >
                Cancel
              </button>
              <button
                onClick={handleDelete}
                className="px-4 py-2 bg-red-600 text-white rounded hover:bg-red-700 transition"
              >
                Delete
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

function errorMessage(err: unknown, fallback: string): string {
  if (
    typeof err === 'object' &&
    err !== null &&
    'response' in err &&
    typeof (err as { response?: { data?: { error?: string } } }).response?.data?.error === 'string'
  ) {
    return (err as { response: { data: { error: string } } }).response.data.error
  }
  return fallback
}
