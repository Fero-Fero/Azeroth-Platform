import { useState } from 'react'
import { Loader2, RefreshCw, Trash2 } from 'lucide-react'
import { formatBytes, formatDate } from '@/components/docker/DockerDiskUsage'
import { useDockerVolumeAudit, useDockerVolumeAuditCleanup } from '@/hooks/useStackDocker'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

interface VolumeAuditSectionProps {
  stackId: string
}

export default function VolumeAuditSection({ stackId }: VolumeAuditSectionProps) {
  const auditQuery = useDockerVolumeAudit(stackId)
  const cleanup = useDockerVolumeAuditCleanup(stackId)
  const [selectedOrphans, setSelectedOrphans] = useState<Set<string>>(new Set())
  const [selectedStale, setSelectedStale] = useState<Set<string>>(new Set())
  const [confirmCleanup, setConfirmCleanup] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)

  const audit = auditQuery.data
  const safeOrphans = audit?.orphanVolumes.filter((v) => v.isSafeToDelete) ?? []
  const safeStale = audit?.staleOverlayFiles.filter((f) => f.isSafeToDelete) ?? []
  const selectedBytes =
    safeOrphans.filter((v) => selectedOrphans.has(v.volumeName)).reduce((s, v) => s + (v.sizeBytes ?? 0), 0) +
    safeStale.filter((f) => selectedStale.has(f.relativePath)).reduce((s, f) => s + f.sizeBytes, 0)
  const selectedCount = selectedOrphans.size + selectedStale.size

  const runAudit = async () => {
    setNotice(null)
    setActionError(null)
    setSelectedOrphans(new Set())
    setSelectedStale(new Set())
    try {
      await auditQuery.refetch()
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

  const toggleOrphan = (name: string) => {
    setSelectedOrphans((prev) => {
      const next = new Set(prev)
      if (next.has(name)) next.delete(name)
      else next.add(name)
      return next
    })
  }

  const toggleStale = (path: string) => {
    setSelectedStale((prev) => {
      const next = new Set(prev)
      if (next.has(path)) next.delete(path)
      else next.add(path)
      return next
    })
  }

  const selectAllSafe = () => {
    setSelectedOrphans(new Set(safeOrphans.map((v) => v.volumeName)))
    setSelectedStale(new Set(safeStale.map((f) => f.relativePath)))
  }

  const handleCleanup = async () => {
    setConfirmCleanup(false)
    setNotice(null)
    setActionError(null)
    try {
      const result = await cleanup.mutateAsync({
        orphanVolumeNames: [...selectedOrphans],
        staleOverlayPaths: [...selectedStale],
      })
      setNotice(
        result.data.message +
          (result.data.freedBytes ? ` Freed about ${formatBytes(result.data.freedBytes)}.` : ''),
      )
      setSelectedOrphans(new Set())
      setSelectedStale(new Set())
      await auditQuery.refetch()
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

  return (
    <section className="overflow-hidden rounded-xl border border-blue-200 bg-white shadow-sm">
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-blue-100 px-4 py-3 sm:px-5">
        <div>
          <h3 className="font-medium text-gray-900">Volume audit</h3>
          <p className="mt-1 max-w-2xl text-xs text-gray-500">
            Compare manager storage with Docker volumes. Finds orphan volumes from deleted stacks and stale overlay
            files that exist only in Docker. Only confirmed-unused items can be selected for cleanup.
          </p>
        </div>
        <button
          type="button"
          onClick={() => void runAudit()}
          disabled={auditQuery.isFetching || cleanup.isPending}
          className="inline-flex items-center gap-2 rounded-lg border border-blue-300 bg-blue-50 px-4 py-2 text-sm font-semibold text-blue-900 hover:bg-blue-100 disabled:opacity-50"
        >
          {auditQuery.isFetching ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
          Run volume audit
        </button>
      </div>

      <div className="space-y-4 px-4 py-4 sm:px-5">
        {!audit && !auditQuery.isFetching && (
          <p className="text-sm text-gray-500">
            Run an audit to compare manager storage with Docker volumes and find safe cleanup candidates.
          </p>
        )}

        {auditQuery.isError && (
          <div className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {errorMessage(auditQuery.error)}
          </div>
        )}

        {notice && (
          <div className="rounded-md border border-green-200 bg-green-50 px-3 py-2 text-sm text-green-800">{notice}</div>
        )}
        {actionError && (
          <div className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{actionError}</div>
        )}

        {audit && (
          <>
            <p className="text-xs text-gray-500">
              Audited {formatDate(audit.auditedAt)} · Up to {formatBytes(audit.reclaimableBytes)} reclaimable across{' '}
              {audit.reclaimableItemCount} item(s)
            </p>

            {audit.duplicateCopies.length > 0 && (
              <details className="rounded-lg border border-gray-200 bg-gray-50/50">
                <summary className="cursor-pointer px-3 py-2 text-sm font-medium text-gray-900">
                  Duplicate copies ({audit.duplicateCopies.length}) - informational
                </summary>
                <div className="border-t border-gray-200 px-3 py-3">
                  <p className="mb-2 text-xs text-gray-500">
                    These exist on manager disk and in Docker by design - not selectable for cleanup.
                  </p>
                  <div className="overflow-x-auto rounded-md border border-gray-200 bg-white">
                    <table className="min-w-full text-left text-sm">
                      <thead className="bg-gray-50 text-xs uppercase text-gray-500">
                        <tr>
                          <th className="px-3 py-2">Data</th>
                          <th className="px-3 py-2">Manager</th>
                          <th className="px-3 py-2">Docker volume</th>
                        </tr>
                      </thead>
                      <tbody>
                        {audit.duplicateCopies.map((copy) => (
                          <tr key={copy.label} className="border-t border-gray-100">
                            <td className="px-3 py-2">
                              <div className="font-medium text-gray-900">{copy.label}</div>
                              <div className="text-xs text-gray-500">{copy.detail}</div>
                            </td>
                            <td className="px-3 py-2 text-gray-600">
                              {formatBytes(copy.managerBytes)}
                              <div className="font-mono text-[10px] text-gray-400">{copy.managerPath}</div>
                            </td>
                            <td className="px-3 py-2 text-gray-600">
                              {formatBytes(copy.volumeBytes)}
                              <div className="font-mono text-[10px] text-gray-400">{copy.volumeName}</div>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              </details>
            )}

            {audit.driftNotes.length > 0 && (
              <div className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
                {audit.driftNotes.map((note) => (
                  <div key={note.category} className="mt-1 first:mt-0">
                    <span className="font-medium">{note.category}:</span> {note.detail}
                  </div>
                ))}
              </div>
            )}

            {(safeOrphans.length > 0 || safeStale.length > 0) && (
              <div className="flex flex-wrap items-center justify-between gap-2">
                <h4 className="text-sm font-medium text-gray-900">Safe cleanup candidates</h4>
                <div className="flex gap-2">
                  <button
                    type="button"
                    onClick={selectAllSafe}
                    className="rounded-md border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
                  >
                    Select all safe items
                  </button>
                  <button
                    type="button"
                    onClick={() => setConfirmCleanup(true)}
                    disabled={selectedCount === 0 || cleanup.isPending}
                    className="inline-flex items-center gap-1 rounded-md bg-red-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-red-700 disabled:opacity-50"
                  >
                    {cleanup.isPending ? (
                      <Loader2 className="h-3.5 w-3.5 animate-spin" />
                    ) : (
                      <Trash2 className="h-3.5 w-3.5" />
                    )}
                    Delete selected ({selectedCount})
                    {selectedBytes > 0 && ` · ${formatBytes(selectedBytes)}`}
                  </button>
                </div>
              </div>
            )}

            {audit.orphanVolumes.length > 0 && (
              <div className="overflow-x-auto rounded-md border border-gray-200">
                <table className="min-w-full text-left text-sm">
                  <thead className="bg-gray-50 text-xs uppercase text-gray-500">
                    <tr>
                      <th className="w-8 px-3 py-2" />
                      <th className="px-3 py-2">Orphan volume</th>
                      <th className="px-3 py-2">Stack</th>
                      <th className="px-3 py-2">Size</th>
                      <th className="px-3 py-2">Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {audit.orphanVolumes.map((volume) => (
                      <tr key={volume.volumeName} className="border-t border-gray-100">
                        <td className="px-3 py-2">
                          {volume.isSafeToDelete ? (
                            <input
                              type="checkbox"
                              checked={selectedOrphans.has(volume.volumeName)}
                              onChange={() => toggleOrphan(volume.volumeName)}
                              className="rounded border-gray-300"
                            />
                          ) : null}
                        </td>
                        <td className="px-3 py-2 font-mono text-xs">{volume.volumeName}</td>
                        <td className="px-3 py-2 text-gray-600">{volume.inferredStackId ?? '-'}</td>
                        <td className="px-3 py-2">{formatBytes(volume.sizeBytes)}</td>
                        <td className="px-3 py-2 text-xs text-gray-600">{volume.reason}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {audit.staleOverlayFiles.length > 0 && (
              <div>
                <h4 className="mb-2 text-sm font-medium text-gray-900">Stale overlay files</h4>
                <div className="overflow-x-auto rounded-md border border-gray-200">
                  <table className="min-w-full text-left text-sm">
                    <thead className="bg-gray-50 text-xs uppercase text-gray-500">
                      <tr>
                        <th className="w-8 px-3 py-2" />
                        <th className="px-3 py-2">Path in volume</th>
                        <th className="px-3 py-2">Size</th>
                        <th className="px-3 py-2">Reason</th>
                      </tr>
                    </thead>
                    <tbody>
                      {audit.staleOverlayFiles.map((file) => (
                        <tr key={file.relativePath} className="border-t border-gray-100">
                          <td className="px-3 py-2">
                            {file.isSafeToDelete ? (
                              <input
                                type="checkbox"
                                checked={selectedStale.has(file.relativePath)}
                                onChange={() => toggleStale(file.relativePath)}
                                className="rounded border-gray-300"
                              />
                            ) : null}
                          </td>
                          <td className="px-3 py-2 font-mono text-xs">{file.relativePath}</td>
                          <td className="px-3 py-2">{formatBytes(file.sizeBytes)}</td>
                          <td className="px-3 py-2 text-xs text-gray-600">{file.reason}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            )}

            {audit.orphanVolumes.length === 0 &&
              audit.staleOverlayFiles.length === 0 &&
              audit.duplicateCopies.length === 0 && (
                <p className="text-sm text-gray-500">No issues found. Volumes appear consistent with managed stacks.</p>
              )}
          </>
        )}
      </div>

      {confirmCleanup && (
        <ConfirmDialog
          title="Delete selected unused volume data?"
          message={`This will permanently remove ${selectedOrphans.size} orphan volume(s) and ${selectedStale.size} stale overlay file(s) (${formatBytes(selectedBytes)}). Active stack data, database volumes, client base copies, and files present in the manager overlay mirror are never included. This cannot be undone.`}
          onCancel={() => setConfirmCleanup(false)}
          onConfirm={() => void handleCleanup()}
          confirmLabel="Delete selected"
        />
      )}
    </section>
  )
}

function ConfirmDialog({
  title,
  message,
  onCancel,
  onConfirm,
  confirmLabel = 'Delete',
}: {
  title: string
  message: string
  onCancel: () => void
  onConfirm: () => void
  confirmLabel?: string
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-md rounded-lg bg-white p-6 shadow-xl">
        <h3 className="text-lg font-semibold text-gray-900">{title}</h3>
        <p className="mt-2 text-sm text-gray-600">{message}</p>
        <div className="mt-6 flex justify-end gap-3">
          <button
            type="button"
            onClick={onCancel}
            className="rounded-md border border-gray-300 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onConfirm}
            className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700"
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}
