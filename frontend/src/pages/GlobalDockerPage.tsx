import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  AlertCircle,
  HardDrive,
  Image as ImageIcon,
  Layers,
  Loader2,
  Lock,
  RefreshCw,
  Trash2,
} from 'lucide-react'
import { DiskUsageBar, formatBytes } from '@/components/docker/DockerDiskUsage'
import { useDockerCleanupJob } from '@/hooks/useDockerCleanupJob'
import {
  useDeleteEngineImage,
  useDeleteEngineVolume,
  useDeleteManagerFile,
  useDockerEngineOverview,
} from '@/hooks/useStackDocker'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import type { DockerEngineImageDto, DockerEngineVolumeEntryDto } from '@/types/docker.types'
import { ManagerVolumeBrowser } from '@/components/docker/ManagerVolumeBrowser'

export default function GlobalDockerPage() {
  const { data, isLoading, isError, error, refetch, isFetching } = useDockerEngineOverview()
  const deleteVolume = useDeleteEngineVolume()
  const deleteImage = useDeleteEngineImage()
  const deleteManagerFile = useDeleteManagerFile()
  const { job: cleanupJob, isRunning: cleanupRunning, startCleanup, invalidateDockerQueries } =
    useDockerCleanupJob()

  const [notice, setNotice] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [confirmCleanup, setConfirmCleanup] = useState(false)
  const [confirmOldBuilds, setConfirmOldBuilds] = useState(false)
  const [confirmVolume, setConfirmVolume] = useState<DockerEngineVolumeEntryDto | null>(null)
  const [confirmImage, setConfirmImage] = useState<DockerEngineImageDto | null>(null)
  const [confirmManagerDir, setConfirmManagerDir] = useState<{ name: string; relativePath: string } | null>(null)
  const [selectedVolumes, setSelectedVolumes] = useState<Set<string>>(new Set())
  const lastHandledCleanupJobRef = useRef<string | null>(null)

  const busy = isFetching || deleteVolume.isPending || deleteImage.isPending || deleteManagerFile.isPending || cleanupRunning

  useEffect(() => {
    if (!cleanupJob || cleanupJob.isRunning || cleanupJob.jobId === lastHandledCleanupJobRef.current) {
      return
    }

    lastHandledCleanupJobRef.current = cleanupJob.jobId
    invalidateDockerQueries()
    void refetch()

    if (cleanupJob.phase === 'Completed') {
      const verb = cleanupJob.action === 'CleanupOldBuilds' ? 'Cleaned up' : 'Reclaimed'
      let msg = cleanupJob.message
      if (cleanupJob.freedBytes) {
        msg += ` ${verb} about ${formatBytes(cleanupJob.freedBytes)}.`
      } else if (reclaimable > 0) {
        msg +=
          ' Nothing was freed — the estimate may include Docker cache or images still in use by protected stacks. Manager volume space is not affected by reclaim.'
      }
      setNotice(msg)
      setActionError(null)
    } else if (cleanupJob.phase === 'Failed') {
      setActionError(cleanupJob.message + (cleanupJob.error ? ` ${cleanupJob.error}` : ''))
    }
  }, [cleanupJob, invalidateDockerQueries, refetch])

  const deletableVolumes =
    data?.volumeGroups.flatMap((g) => g.volumes.filter((v) => v.isDeletable)) ?? []

  const handleDeleteManagerDir = async () => {
    if (!confirmManagerDir) return
    const target = confirmManagerDir
    setConfirmManagerDir(null)
    setNotice(null)
    setActionError(null)
    try {
      const result = await deleteManagerFile.mutateAsync(target.relativePath)
      setNotice(result.data.message + (result.data.freedBytes ? ` Freed ${formatBytes(result.data.freedBytes)}.` : ''))
      await refetch()
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

  const toggleVolume = (name: string) => {
    setSelectedVolumes((prev) => {
      const next = new Set(prev)
      if (next.has(name)) next.delete(name)
      else next.add(name)
      return next
    })
  }

  const selectAllDeletableVolumes = () => {
    setSelectedVolumes(new Set(deletableVolumes.map((v) => v.name)))
  }

  const handleBulkDeleteVolumes = async () => {
    setNotice(null)
    setActionError(null)
    const names = [...selectedVolumes]
    let freed = 0
    try {
      for (const name of names) {
        const result = await deleteVolume.mutateAsync(name)
        freed += result.data.freedBytes ?? 0
      }
      setSelectedVolumes(new Set())
      setNotice(`Removed ${names.length} volume(s).${freed ? ` Freed about ${formatBytes(freed)}.` : ''}`)
      await refetch()
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

  const handleDeleteVolume = async () => {
    if (!confirmVolume) return
    const target = confirmVolume
    setConfirmVolume(null)
    setNotice(null)
    setActionError(null)
    try {
      const result = await deleteVolume.mutateAsync(target.name)
      setNotice(result.data.message)
      await refetch()
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

  const handleDeleteImage = async () => {
    if (!confirmImage) return
    const target = confirmImage
    setConfirmImage(null)
    setNotice(null)
    setActionError(null)
    try {
      const result = await deleteImage.mutateAsync(target.id)
      setNotice(result.data.message)
      await refetch()
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

  const handleCleanup = async () => {
    setConfirmCleanup(false)
    setNotice(null)
    setActionError(null)
    try {
      await startCleanup('ReclaimDiskSpace')
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

  const handleOldBuildsCleanup = async () => {
    setConfirmOldBuilds(false)
    setNotice(null)
    setActionError(null)
    try {
      await startCleanup('CleanupOldBuilds')
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

  if (isLoading) {
    return (
      <div className="flex items-center gap-2 text-sm text-gray-600">
        <Loader2 className="h-4 w-4 animate-spin" />
        Loading Docker engine overview…
      </div>
    )
  }

  if (isError || !data) {
    return (
      <div className="rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        <AlertCircle className="mr-2 inline h-4 w-4" />
        {errorMessage(error)}
      </div>
    )
  }

  const reclaimable = data.reclaimableBytes ?? data.reclaimableBreakdown?.listedReclaimableBytes ?? 0
  const deletableImages = data.images.filter((i) => i.isDeletable)

  return (
    <div className="mx-auto max-w-6xl space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Docker</h1>
          <p className="mt-1 text-sm text-gray-600">
            Engine-wide disk usage: manager data volume, all stack volumes, images, and reclaim actions.
            Per-stack details are under each stack&apos;s Advanced → Docker tab.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => setConfirmCleanup(true)}
            disabled={busy}
            className="inline-flex items-center gap-2 rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-sm font-medium text-amber-900 hover:bg-amber-100 disabled:opacity-50"
          >
            {cleanupRunning ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
            Reclaim disk space
          </button>
          <button
            type="button"
            onClick={() => setConfirmOldBuilds(true)}
            disabled={busy}
            className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-800 hover:bg-gray-50 disabled:opacity-50"
          >
            <HardDrive className="h-4 w-4" />
            Clean up old builds
          </button>
          <button
            type="button"
            onClick={() => refetch()}
            disabled={busy}
            className="inline-flex items-center gap-2 rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50"
          >
            {isFetching ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
            Refresh
          </button>
        </div>
      </div>

      {cleanupRunning && cleanupJob && (
        <div className="flex items-start gap-2 rounded-md border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
          <Loader2 className="mt-0.5 h-4 w-4 shrink-0 animate-spin" />
          <div>
            <p className="font-medium">Cleanup running in the background…</p>
            <p className="mt-1 text-amber-800">{cleanupJob.message}</p>
          </div>
        </div>
      )}

      <DiskUsageBar disk={data.diskUsage} reclaimableBytes={reclaimable} />

      <div className="grid gap-3 sm:grid-cols-3">
        <SummaryCard label="Docker volumes" value={formatBytes(data.totalVolumeBytes)} detail={`${data.deletableVolumeCount} deletable`} />
        <SummaryCard label="Docker images" value={formatBytes(data.totalImageBytes)} detail={`${deletableImages.length} deletable`} />
        <SummaryCard
          label="Reclaimable (cleanup action)"
          value={formatBytes(reclaimable)}
          detail={
            reclaimable > 0 && data.reclaimableBreakdown
              ? [
                  data.reclaimableBreakdown.buildCacheBytes ? `${formatBytes(data.reclaimableBreakdown.buildCacheBytes)} build cache` : null,
                  data.reclaimableBreakdown.danglingImageBytes ? `${formatBytes(data.reclaimableBreakdown.danglingImageBytes)} dangling layers` : null,
                  data.reclaimableBreakdown.unusedTaggedImageBytes ? `${formatBytes(data.reclaimableBreakdown.unusedTaggedImageBytes)} unused images` : null,
                ].filter(Boolean).join(' · ') || 'Prunable Docker cache/images — not stack volumes'
              : reclaimable === 0
                ? 'Nothing left for reclaim (volumes are deleted separately below)'
                : undefined
          }
        />
      </div>

      {notice && (
        <div className="rounded-md border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">{notice}</div>
      )}
      {actionError && (
        <div className="rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{actionError}</div>
      )}

      {data.managerVolume && (
        <section className="rounded-lg border border-blue-200 bg-white shadow-sm">
          <div className="flex items-center gap-2 border-b border-blue-100 px-4 py-3">
            <HardDrive className="h-4 w-4 text-blue-700" />
            <div>
              <h2 className="font-medium text-gray-900">Manager data volume</h2>
              <p className="text-xs text-gray-500">{data.managerVolume.detail}</p>
            </div>
            <span className="ml-auto inline-flex items-center gap-1 rounded-full border border-blue-200 bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-800">
              <Lock className="h-3 w-3" />
              Protected
            </span>
          </div>
          <div className="px-4 py-3 text-sm text-gray-700">
            <span className="font-mono text-xs">{data.managerVolume.name}</span>
            <span className="mx-2 text-gray-400">·</span>
            <span className="font-medium">{formatBytes(data.managerVolume.totalBytes)}</span>
          </div>
          {data.managerVolume.directories.length > 0 && (
            <div className="overflow-x-auto border-t border-gray-100">
              <table className="min-w-full text-left text-sm">
                <thead className="bg-gray-50 text-xs uppercase text-gray-500">
                  <tr>
                    <th className="px-4 py-2">Directory</th>
                    <th className="px-4 py-2">Size</th>
                    <th className="px-4 py-2">Purpose</th>
                    <th className="px-4 py-2" />
                  </tr>
                </thead>
                <tbody>
                  {data.managerVolume.directories.map((dir) => (
                    <tr key={dir.name} className="border-t border-gray-100">
                      <td className="px-4 py-2 font-mono text-xs">{dir.name}</td>
                      <td className="px-4 py-2">{formatBytes(dir.sizeBytes)}</td>
                      <td className="px-4 py-2 text-xs text-gray-600">{dir.detail ?? '—'}</td>
                      <td className="px-4 py-2 text-right">
                        {dir.isDeletable ? (
                          <button
                            type="button"
                            onClick={() =>
                              setConfirmManagerDir({ name: dir.name, relativePath: dir.relativePath || dir.name })
                            }
                            className="text-xs font-medium text-red-700 hover:underline"
                          >
                            Delete
                          </button>
                        ) : (
                          <span className="text-xs text-gray-400">Protected</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <ManagerVolumeBrowser />
        </section>
      )}

      <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-100 px-4 py-3">
          <div className="flex items-center gap-2">
            <Layers className="h-4 w-4 text-gray-500" />
            <h2 className="font-medium text-gray-900">Volumes on engine</h2>
          </div>
          {deletableVolumes.length > 0 && (
            <div className="flex gap-2">
              <button
                type="button"
                onClick={selectAllDeletableVolumes}
                className="rounded-md border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
              >
                Select all deletable ({deletableVolumes.length})
              </button>
              <button
                type="button"
                onClick={() => void handleBulkDeleteVolumes()}
                disabled={busy || selectedVolumes.size === 0}
                className="rounded-md bg-red-600 px-3 py-1 text-xs font-medium text-white hover:bg-red-700 disabled:opacity-50"
              >
                Delete selected ({selectedVolumes.size})
              </button>
            </div>
          )}
        </div>
        <div className="divide-y divide-gray-100">
          {data.volumeGroups.map((group) => (
            <div key={`${group.category}-${group.stackId ?? 'none'}`} className="px-4 py-4">
              <div className="mb-2 flex flex-wrap items-baseline gap-2">
                <h3 className="text-sm font-medium text-gray-900">{group.category}</h3>
                {group.stackName && (
                  <span className="text-xs text-gray-500">
                    {group.stackName}
                    {group.stackId && (
                      <>
                        {' '}
                        ·{' '}
                        <Link to={`/stacks/${group.stackId}`} className="text-blue-600 hover:underline">
                          Open stack
                        </Link>
                      </>
                    )}
                  </span>
                )}
                <span className="text-xs text-gray-500">{formatBytes(group.totalBytes)}</span>
              </div>
              <div className="overflow-x-auto">
                <table className="min-w-full text-left text-sm">
                  <thead className="text-xs uppercase text-gray-500">
                    <tr>
                      <th className="w-8 py-1" />
                      <th className="py-1 pr-4">Volume</th>
                      <th className="py-1 pr-4">Size</th>
                      <th className="py-1 pr-4">Links</th>
                      <th className="py-1 pr-4">Status</th>
                      <th className="py-1 text-right">Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {group.volumes.map((volume) => (
                      <tr key={volume.name} className="border-t border-gray-50">
                        <td className="py-2">
                          {volume.isDeletable ? (
                            <input
                              type="checkbox"
                              checked={selectedVolumes.has(volume.name)}
                              onChange={() => toggleVolume(volume.name)}
                              className="rounded border-gray-300"
                            />
                          ) : null}
                        </td>
                        <td className="py-2 pr-4 font-mono text-xs">{volume.name}</td>
                        <td className="py-2 pr-4">{formatBytes(volume.sizeBytes)}</td>
                        <td className="py-2 pr-4">{volume.linkCount}</td>
                        <td className="py-2 pr-4 text-xs text-gray-600">{volume.detail}</td>
                        <td className="py-2 text-right">
                          {volume.isDeletable ? (
                            <button
                              type="button"
                              onClick={() => setConfirmVolume(volume)}
                              disabled={busy}
                              className="text-xs font-medium text-red-700 hover:underline disabled:opacity-50"
                            >
                              Delete
                            </button>
                          ) : (
                            <span className="inline-flex items-center gap-1 text-xs text-gray-400">
                              <Lock className="h-3 w-3" />
                              Protected
                            </span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          ))}
        </div>
      </section>

      <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
        <div className="flex items-center gap-2 border-b border-gray-100 px-4 py-3">
          <ImageIcon className="h-4 w-4 text-gray-500" />
          <h2 className="font-medium text-gray-900">Images on engine</h2>
        </div>
        <div className="overflow-x-auto">
          <table className="min-w-full text-left text-sm">
            <thead className="bg-gray-50 text-xs uppercase text-gray-500">
              <tr>
                <th className="px-4 py-2">Image</th>
                <th className="px-4 py-2">Category</th>
                <th className="px-4 py-2">Size</th>
                <th className="px-4 py-2">Containers</th>
                <th className="px-4 py-2 text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {data.images.map((image) => (
                <tr key={image.id} className="border-t border-gray-100">
                  <td className="px-4 py-3 font-mono text-xs">{image.reference}</td>
                  <td className="px-4 py-3 text-gray-600">{image.category}</td>
                  <td className="px-4 py-3">{formatBytes(image.sizeBytes)}</td>
                  <td className="px-4 py-3">{image.containerCount}</td>
                  <td className="px-4 py-3 text-right">
                    {image.isDeletable ? (
                      <button
                        type="button"
                        onClick={() => setConfirmImage(image)}
                        disabled={busy}
                        className="text-xs font-medium text-red-700 hover:underline disabled:opacity-50"
                      >
                        Delete
                      </button>
                    ) : (
                      <span className="inline-flex items-center gap-1 text-xs text-gray-400">
                        <Lock className="h-3 w-3" />
                        Protected
                      </span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {confirmCleanup && (
        <ConfirmDialog
          title="Reclaim disk space?"
          message={
            reclaimable > 0
              ? `Prunes build cache and removes up to ${formatBytes(reclaimable)} of unused images and orphaned build checkouts. Managed stack images and volumes are kept. Unused Docker volumes must be deleted separately in the volume list below.`
              : 'Little reclaimable space was detected, but this will still prune build cache and dangling image layers if present. Unused Docker volumes are not removed — delete those separately below.'
          }
          onCancel={() => setConfirmCleanup(false)}
          onConfirm={() => void handleCleanup()}
          confirmLabel="Reclaim disk space"
        />
      )}
      {confirmOldBuilds && (
        <ConfirmDialog
          title="Clean up old builds?"
          message="Removes dangling layers and unused stack images from deleted stacks. Build cache is kept."
          onCancel={() => setConfirmOldBuilds(false)}
          onConfirm={() => void handleOldBuildsCleanup()}
          confirmLabel="Clean up old builds"
        />
      )}
      {confirmVolume && (
        <ConfirmDialog
          title="Delete volume?"
          message={`Remove ${confirmVolume.name}? ${confirmVolume.detail ?? ''} This cannot be undone.`}
          onCancel={() => setConfirmVolume(null)}
          onConfirm={() => void handleDeleteVolume()}
        />
      )}
      {confirmManagerDir && (
        <ConfirmDialog
          title="Delete from manager volume?"
          message={
            confirmManagerDir.name === 'client'
              ? `Remove ${confirmManagerDir.name}/ from the manager volume? This only deletes the legacy manager copy — but if stack client-base Docker volumes are empty, your stacks will lose their clients. Use Migrate legacy client mirrors first, or re-upload on each stack's Client tab.`
              : `Remove ${confirmManagerDir.name}/ and everything inside it from the manager volume? Stack Docker volumes are separate, but deleting legacy mirrors before volumes are populated will break client distribution.`
          }
          onCancel={() => setConfirmManagerDir(null)}
          onConfirm={() => void handleDeleteManagerDir()}
          confirmLabel="Delete"
        />
      )}
      {confirmImage && (
        <ConfirmDialog
          title="Delete image?"
          message={`Remove ${confirmImage.reference}? This cannot be undone.`}
          onCancel={() => setConfirmImage(null)}
          onConfirm={() => void handleDeleteImage()}
        />
      )}
    </div>
  )
}

function SummaryCard({ label, value, detail }: { label: string; value: string; detail?: string }) {
  return (
    <div className="rounded-lg border border-gray-200 bg-white px-4 py-3 shadow-sm">
      <div className="text-xs font-medium uppercase tracking-wide text-gray-500">{label}</div>
      <div className="mt-1 text-lg font-semibold text-gray-900">{value}</div>
      {detail && <div className="mt-0.5 text-xs text-gray-500">{detail}</div>}
    </div>
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
          <button type="button" onClick={onCancel} className="rounded-md border border-gray-300 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50">
            Cancel
          </button>
          <button type="button" onClick={onConfirm} className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700">
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}
