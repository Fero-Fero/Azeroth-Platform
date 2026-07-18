import { useEffect, useRef, useState } from 'react'
import { AlertCircle, HardDrive, Image as ImageIcon, Layers, Loader2, Lock, RefreshCw, Trash2 } from 'lucide-react'
import {
  useDeleteDockerBuildFiles,
  useDeleteDockerImage,
  useDeleteDockerVolume,
  useDockerVolumeAudit,
  useDockerVolumeAuditCleanup,
  useStackDockerOverview,
} from '@/hooks/useStackDocker'
import { useDockerCleanupJob } from '@/hooks/useDockerCleanupJob'
import { DiskUsageBar, formatBytes, formatDate } from '@/components/docker/DockerDiskUsage'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import type {
  DockerDiskUsageBreakdownDto,
  DockerObsoleteBuildDirDto,
  DockerReclaimableBreakdownDto,
  StackDockerBuildFilesDto,
  StackDockerImageDto,
  StackDockerOverviewDto,
  StackDockerVolumeDto,
} from '@/types/docker.types'

interface DockerTabProps {
  stackId: string
}

type DockerSubTab = 'resources' | 'disk-usage'

export default function DockerTab({ stackId }: DockerTabProps) {
  const { data, isLoading, isError, error, refetch, isFetching } = useStackDockerOverview(stackId)
  const deleteBuildFiles = useDeleteDockerBuildFiles(stackId)
  const deleteImage = useDeleteDockerImage(stackId)
  const deleteVolume = useDeleteDockerVolume(stackId)
  const {
    job: cleanupJob,
    isRunning: cleanupRunning,
    startCleanup,
    invalidateDockerQueries,
  } = useDockerCleanupJob()

  const [notice, setNotice] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [confirmBuildDelete, setConfirmBuildDelete] = useState(false)
  const [confirmCleanup, setConfirmCleanup] = useState(false)
  const [confirmOldBuildsCleanup, setConfirmOldBuildsCleanup] = useState(false)
  const [confirmImage, setConfirmImage] = useState<StackDockerImageDto | null>(null)
  const [confirmVolume, setConfirmVolume] = useState<StackDockerVolumeDto | null>(null)
  const [cleanupStarting, setCleanupStarting] = useState(false)
  const [subTab, setSubTab] = useState<DockerSubTab>('resources')
  const lastHandledCleanupJobRef = useRef<string | null>(null)

  const busy =
    deleteBuildFiles.isPending ||
    deleteImage.isPending ||
    deleteVolume.isPending ||
    cleanupRunning ||
    cleanupStarting ||
    isFetching

  useEffect(() => {
    if (!cleanupJob || cleanupJob.isRunning || cleanupJob.jobId === lastHandledCleanupJobRef.current) {
      return
    }

    lastHandledCleanupJobRef.current = cleanupJob.jobId
    invalidateDockerQueries()
    void refetch()

    if (cleanupJob.phase === 'Completed') {
      const verb = cleanupJob.action === 'CleanupOldBuilds' ? 'Cleaned up' : 'Reclaimed'
      setNotice(
        cleanupJob.message +
          (cleanupJob.freedBytes ? ` ${verb} about ${formatBytes(cleanupJob.freedBytes)}.` : ''),
      )
      setActionError(null)
    } else if (cleanupJob.phase === 'Failed') {
      setActionError(cleanupJob.message + (cleanupJob.error ? ` ${cleanupJob.error}` : ''))
    }
  }, [cleanupJob, invalidateDockerQueries, refetch])

  const handleCleanup = async () => {
    setConfirmCleanup(false)
    setNotice(null)
    setActionError(null)
    setCleanupStarting(true)
    try {
      await startCleanup('ReclaimDiskSpace')
    } catch (err) {
      setActionError(errorMessage(err))
    } finally {
      setCleanupStarting(false)
    }
  }

  const handleOldBuildsCleanup = async () => {
    setConfirmOldBuildsCleanup(false)
    setNotice(null)
    setActionError(null)
    setCleanupStarting(true)
    try {
      await startCleanup('CleanupOldBuilds')
    } catch (err) {
      setActionError(errorMessage(err))
    } finally {
      setCleanupStarting(false)
    }
  }

  const handleDeleteBuildFiles = async () => {
    setConfirmBuildDelete(false)
    setNotice(null)
    setActionError(null)
    try {
      const result = await deleteBuildFiles.mutateAsync()
      setNotice(result.data.message + (result.data.freedBytes ? ` Freed ${formatBytes(result.data.freedBytes)}.` : ''))
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
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

  if (isLoading) {
    return (
      <div className="flex items-center gap-2 text-sm text-gray-600">
        <Loader2 className="h-4 w-4 animate-spin" />
        Loading Docker resources…
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

  const deletableUnusedImages = data.unusedImages.filter((image) => !image.isActive)
  const reclaimableBytes = data.reclaimableBytes || data.reclaimableBreakdown?.engineReclaimableBytes || 0
  const reclaimEstimate = formatBytes(reclaimableBytes)
  const oldBuildsBytes =
    (data.reclaimableBreakdown?.danglingImageBytes ?? 0) +
    (data.reclaimableBreakdown?.unusedTaggedImageBytes ?? 0) +
    (data.reclaimableBreakdown?.obsoleteBuildDirBytes ?? 0)
  const oldBuildsEstimate = formatBytes(oldBuildsBytes)
  const oldBuildsCount =
    (data.reclaimableBreakdown?.danglingImageCount ?? 0) +
    (data.reclaimableBreakdown?.unusedTaggedImageCount ?? 0) +
    (data.reclaimableBreakdown?.obsoleteBuildDirCount ?? 0)

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h2 className="text-xl font-semibold text-gray-900">Docker resources</h2>
          <p className="mt-1 text-sm text-gray-500">
            Disk usage for the Docker engine, this stack&apos;s resources, and unused leftovers from old builds.
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => setConfirmCleanup(true)}
            disabled={busy}
            title={
              reclaimableBytes > 0
                ? `Reclaim up to ${reclaimEstimate} of Docker disk space`
                : 'Prune build cache and dangling image layers'
            }
            className="inline-flex flex-col items-start gap-0.5 rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-sm font-medium text-amber-900 hover:bg-amber-100 disabled:opacity-50 sm:flex-row sm:items-center sm:gap-2"
          >
            <span className="inline-flex items-center gap-2">
              {cleanupRunning && cleanupJob?.action !== 'CleanupOldBuilds' ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : cleanupStarting ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <Trash2 className="h-4 w-4" />
              )}
              Reclaim disk space
            </span>
            {reclaimableBytes > 0 && (
              <span className="text-xs font-normal text-amber-800 sm:ml-0">Up to {reclaimEstimate}</span>
            )}
          </button>
          <button
            type="button"
            onClick={() => setConfirmOldBuildsCleanup(true)}
            disabled={busy}
            title={
              oldBuildsBytes > 0
                ? `Remove ${oldBuildsCount} old build artifact(s), up to ${oldBuildsEstimate}`
                : 'Remove dangling build layers and unused stack images (keeps build cache)'
            }
            className="inline-flex flex-col items-start gap-0.5 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-800 hover:bg-gray-50 disabled:opacity-50 sm:flex-row sm:items-center sm:gap-2"
          >
            <span className="inline-flex items-center gap-2">
              {cleanupRunning && cleanupJob?.action === 'CleanupOldBuilds' ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <HardDrive className="h-4 w-4" />
              )}
              Clean up old builds
            </span>
            {oldBuildsBytes > 0 && (
              <span className="text-xs font-normal text-gray-500 sm:ml-0">Up to {oldBuildsEstimate}</span>
            )}
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
            <p className="font-medium">
              {cleanupJob.action === 'CleanupOldBuilds'
                ? 'Cleaning up old builds in the background…'
                : 'Reclaiming disk space in the background…'}
            </p>
            <p className="mt-1 text-amber-800">{cleanupJob.message}</p>
            <p className="mt-1 text-xs text-amber-700">
              You can leave this page — the job will keep running until it finishes.
            </p>
          </div>
        </div>
      )}

      <VolumeAuditSection stackId={stackId} />

      <DiskUsageBar disk={data.diskUsage} reclaimableBytes={data.reclaimableBytes} />

      <div className="flex gap-1 rounded-lg bg-gray-100 p-1">
        {(
          [
            { id: 'resources' as const, label: 'Resources' },
            { id: 'disk-usage' as const, label: 'Disk usage' },
          ] as const
        ).map((tab) => (
          <button
            key={tab.id}
            type="button"
            onClick={() => setSubTab(tab.id)}
            className={`flex-1 rounded-md px-3 py-2 text-sm font-medium transition-colors ${
              subTab === tab.id ? 'bg-white text-gray-900 shadow-sm' : 'text-gray-600 hover:text-gray-800'
            }`}
          >
            {tab.label}
          </button>
        ))}
      </div>

      {subTab === 'disk-usage' ? (
        <DiskUsageSubTab data={data} />
      ) : (
        <ResourcesSubTab
          data={data}
          busy={busy}
          deletableUnusedImages={deletableUnusedImages}
          onDeleteBuildFiles={() => setConfirmBuildDelete(true)}
          onDeleteImage={setConfirmImage}
          onDeleteVolume={setConfirmVolume}
        />
      )}

      {notice && (
        <div className="rounded-md border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">{notice}</div>
      )}
      {actionError && (
        <div className="rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{actionError}</div>
      )}

      {confirmCleanup && (
        <ConfirmDialog
          title="Reclaim disk space?"
          message={
            reclaimableBytes > 0
              ? `This will reclaim up to ${reclaimEstimate} by pruning build cache and dangling image layers, removing unused platform images from deleted stacks, and deleting orphaned on-disk build checkouts. Images and volumes belonging to your managed stacks are always kept — even when those stacks are stopped or only partially running.`
              : 'Little reclaimable space was detected, but this will still prune build cache and dangling image layers if present. Images and volumes belonging to your managed stacks are always kept.'
          }
          onCancel={() => setConfirmCleanup(false)}
          onConfirm={handleCleanup}
          confirmLabel="Reclaim disk space"
        />
      )}
      {confirmOldBuildsCleanup && (
        <ConfirmDialog
          title="Clean up old builds?"
          message={
            oldBuildsBytes > 0
              ? `This will remove up to ${oldBuildsEstimate} of old build artifacts (${oldBuildsCount} item(s)): dangling image layers, unused images from deleted stacks, and orphaned on-disk build checkouts. Docker build cache is kept so future compiles stay fast. Images required by managed stacks are never removed.`
              : 'This will prune dangling build layers and remove unused images from deleted stacks and orphaned build checkouts if present. Docker build cache is kept so future compiles stay fast.'
          }
          onCancel={() => setConfirmOldBuildsCleanup(false)}
          onConfirm={handleOldBuildsCleanup}
          confirmLabel="Clean up old builds"
        />
      )}
      {confirmBuildDelete && (
        <ConfirmDialog
          title="Delete build files?"
          message="This removes the on-disk AzerothCore checkout, modules, migrations mirror, and generated compose files for this stack. You will need to recompile before starting again."
          onCancel={() => setConfirmBuildDelete(false)}
          onConfirm={handleDeleteBuildFiles}
        />
      )}
      {confirmImage && (
        <ConfirmDialog
          title="Delete image?"
          message={`Remove ${confirmImage.reference}? You can rebuild it with a worldserver recompile.`}
          onCancel={() => setConfirmImage(null)}
          onConfirm={handleDeleteImage}
        />
      )}
      {confirmVolume && (
        <ConfirmDialog
          title="Delete volume?"
          message={`Remove ${confirmVolume.name}? This permanently deletes stored data in that volume.`}
          onCancel={() => setConfirmVolume(null)}
          onConfirm={handleDeleteVolume}
        />
      )}
    </div>
  )
}

function ResourcesSubTab({
  data,
  busy,
  deletableUnusedImages,
  onDeleteBuildFiles,
  onDeleteImage,
  onDeleteVolume,
}: {
  data: StackDockerOverviewDto
  busy: boolean
  deletableUnusedImages: StackDockerImageDto[]
  onDeleteBuildFiles: () => void
  onDeleteImage: (image: StackDockerImageDto) => void
  onDeleteVolume: (volume: StackDockerVolumeDto) => void
}) {
  return (
    <div className="space-y-6">
      <div className="rounded-lg border border-gray-200 bg-white px-4 py-3 text-sm text-gray-700 shadow-sm">
        Estimated footprint on this page: <span className="font-medium">{formatBytes(data.totalBytes)}</span>
        {data.buildCacheBytes > 0 && (
          <span className="text-gray-500">
            {' '}
            · Build cache on engine: {formatBytes(data.buildCacheBytes)}
          </span>
        )}
      </div>

      <BuildFilesSection buildFiles={data.buildFiles} busy={busy} onDelete={onDeleteBuildFiles} />

      <ResourceSection
        title="This stack — images"
        icon={<ImageIcon className="h-4 w-4" />}
        emptyLabel="No stack images found on the Docker engine."
      >
        {data.images.map((image) => (
          <ImageRow key={image.id} image={image} busy={busy} onDelete={() => onDeleteImage(image)} />
        ))}
      </ResourceSection>

      <ResourceSection
        title="Unused / other images"
        icon={<ImageIcon className="h-4 w-4" />}
        emptyLabel="No other unused platform images found."
        subtitle={
          deletableUnusedImages.length > 0
            ? `${deletableUnusedImages.length} tagged image(s) from deleted stacks or old builds can be removed.`
            : 'Images required by managed stacks are protected even when stopped.'
        }
      >
        {data.unusedImages.map((image) => (
          <ImageRow key={image.id} image={image} busy={busy} onDelete={() => onDeleteImage(image)} showOwner />
        ))}
      </ResourceSection>

      <DanglingImagesSection images={data.danglingImages} busy={busy} onDelete={onDeleteImage} />

      <ObsoleteBuildDirsSection dirs={data.obsoleteBuildDirs} />

      <ResourceSection
        title="Volumes"
        icon={<Layers className="h-4 w-4" />}
        emptyLabel="No stack volumes found on the Docker engine."
        subtitle="Stack data volumes are protected and cannot be deleted while the stack is managed."
      >
        {data.volumes.map((volume) => (
          <tr key={volume.name} className="border-t border-gray-100">
            <td className="px-4 py-3 font-mono text-xs text-gray-800">{volume.name}</td>
            <td className="px-4 py-3 text-sm text-gray-600">{formatBytes(volume.sizeBytes)}</td>
            <td className="px-4 py-3 text-sm text-gray-600">{volume.linkCount}</td>
            <td className="px-4 py-3">
              <StatusBadge active={volume.isActive} reason={volume.activeReason} />
            </td>
            <td className="px-4 py-3 text-right">
              <DeleteButton
                disabled={busy || volume.isActive}
                title={volume.isActive ? volume.activeReason ?? 'Volume is in use' : `Delete ${volume.name}`}
                onClick={() => onDeleteVolume(volume)}
              />
            </td>
          </tr>
        ))}
      </ResourceSection>
    </div>
  )
}

function DiskUsageSubTab({ data }: { data: StackDockerOverviewDto }) {
  const breakdown = data.diskUsageBreakdown
  const reclaimable = data.reclaimableBreakdown

  if (!breakdown) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white px-4 py-6 text-sm text-gray-500 shadow-sm">
        Disk usage breakdown is unavailable. Try refreshing the page.
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 px-4 py-3">
          <h3 className="font-medium text-gray-900">Where disk space is used</h3>
          <p className="mt-1 text-xs text-gray-500">
            Breakdown of Docker engine storage and on-disk build checkouts across all managed stacks. Host disk usage
            includes everything on the machine — not only Docker.
          </p>
        </div>
        <DiskUsageCategoryTable breakdown={breakdown} />
      </section>

      {reclaimable && <ReclaimableBreakdownSection breakdown={reclaimable} />}

      {breakdown.activeImages.length > 0 && (
        <ResourceSection
          title="Active images (protected from reclaim)"
          icon={<ImageIcon className="h-4 w-4" />}
          emptyLabel="No active platform images found."
          subtitle="These images belong to managed stacks and are never removed by reclaim or old-build cleanup."
        >
          {breakdown.activeImages.map((image) => (
            <tr key={image.id} className="border-t border-gray-100">
              <td className="px-4 py-3 font-mono text-xs text-gray-800">
                {image.reference}
                {image.ownerStackId && (
                  <div className="mt-1 font-sans text-[11px] text-gray-500">Stack {image.ownerStackId}</div>
                )}
              </td>
              <td className="px-4 py-3 text-sm text-gray-600">{formatBytes(image.sizeBytes)}</td>
              <td className="px-4 py-3 text-sm text-gray-600">{formatDate(image.createdAt)}</td>
              <td className="px-4 py-3">
                <StatusBadge active={image.isActive} reason={image.activeReason} />
              </td>
              <td className="px-4 py-3 text-right text-xs text-gray-400">Protected</td>
            </tr>
          ))}
        </ResourceSection>
      )}

      {breakdown.activeVolumes.length > 0 && (
        <ResourceSection
          title="Stack volumes (protected from reclaim)"
          icon={<Layers className="h-4 w-4" />}
          emptyLabel="No stack volumes found."
          subtitle="Data volumes for managed stacks are never removed by reclaim. Reclaim does not delete volumes."
        >
          {breakdown.activeVolumes.map((volume) => (
            <tr key={volume.name} className="border-t border-gray-100">
              <td className="px-4 py-3 font-mono text-xs text-gray-800">{volume.name}</td>
              <td className="px-4 py-3 text-sm text-gray-600">{formatBytes(volume.sizeBytes)}</td>
              <td className="px-4 py-3 text-sm text-gray-600">{volume.linkCount}</td>
              <td className="px-4 py-3">
                <StatusBadge active={volume.isActive} reason={volume.activeReason} />
              </td>
              <td className="px-4 py-3 text-right text-xs text-gray-400">Protected</td>
            </tr>
          ))}
        </ResourceSection>
      )}
    </div>
  )
}

function VolumeAuditSection({ stackId }: { stackId: string }) {
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
    <section className="rounded-lg border border-blue-200 bg-white shadow-sm">
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-blue-100 px-4 py-3">
        <div>
          <h3 className="font-medium text-gray-900">Volume audit</h3>
          <p className="mt-1 text-xs text-gray-500">
            Detect duplicate copies, orphan volumes from deleted stacks, and stale overlay files that exist only in
            Docker (not in the manager mirror). Only confirmed-unused items can be selected for cleanup.
          </p>
        </div>
        <button
          type="button"
          onClick={() => void runAudit()}
          disabled={auditQuery.isFetching || cleanup.isPending}
          className="inline-flex items-center gap-2 rounded-md border border-blue-300 bg-blue-50 px-3 py-2 text-sm font-medium text-blue-900 hover:bg-blue-100 disabled:opacity-50"
        >
          {auditQuery.isFetching ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
          Run volume audit
        </button>
      </div>

      <div className="space-y-4 px-4 py-4">
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
              <div>
                <h4 className="text-sm font-medium text-gray-900">Duplicate copies (informational)</h4>
                <p className="mt-1 text-xs text-gray-500">
                  These exist on manager disk and in Docker by design — not selectable for cleanup.
                </p>
                <div className="mt-2 overflow-x-auto rounded-md border border-gray-200">
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
                    {cleanup.isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Trash2 className="h-3.5 w-3.5" />}
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
                      <th className="px-3 py-2 w-8" />
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
                        <td className="px-3 py-2 text-gray-600">{volume.inferredStackId ?? '—'}</td>
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
                        <th className="px-3 py-2 w-8" />
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

function DiskUsageCategoryTable({ breakdown }: { breakdown: DockerDiskUsageBreakdownDto }) {
  const rows = [
    {
      label: 'Active stack images',
      detail: 'Worldserver, authserver, client, armory, and other images required by managed stacks',
      bytes: breakdown.activeImagesBytes,
      count: breakdown.activeImagesCount,
      tone: 'active' as const,
    },
    {
      label: 'Docker volumes',
      detail: 'Database, client data, configs, and other persistent stack storage',
      bytes: breakdown.activeVolumesBytes || breakdown.dockerVolumesBytes,
      count: breakdown.activeVolumesCount || breakdown.dockerVolumesCount,
      tone: 'active' as const,
    },
    {
      label: 'Build checkouts',
      detail: 'On-disk AzerothCore source trees used for compiles',
      bytes: breakdown.managedBuildCheckoutBytes,
      count: breakdown.managedBuildCheckoutCount,
      tone: 'active' as const,
    },
    {
      label: 'Build cache',
      detail: 'Docker BuildKit cache from past compiles (partially reclaimable)',
      bytes: breakdown.dockerBuildCacheBytes,
      count: breakdown.dockerBuildCacheBytes > 0 ? 1 : 0,
      tone: 'mixed' as const,
    },
    {
      label: 'Container writable layers',
      detail: 'Ephemeral changes inside running/stopped containers',
      bytes: breakdown.dockerContainersBytes,
      count: breakdown.dockerContainersBytes > 0 ? 1 : 0,
      tone: 'neutral' as const,
    },
    {
      label: 'Dangling build layers',
      detail: 'Untagged intermediate layers left by builds (reclaimable)',
      bytes: breakdown.danglingLayerBytes,
      count: breakdown.danglingLayerCount,
      tone: 'reclaimable' as const,
    },
    {
      label: 'Unused images',
      detail: 'Images from deleted stacks or old builds (reclaimable)',
      bytes: breakdown.reclaimableImagesBytes - breakdown.danglingLayerBytes,
      count: breakdown.reclaimableImagesCount - breakdown.danglingLayerCount,
      tone: 'reclaimable' as const,
    },
    {
      label: 'Orphaned build checkouts',
      detail: 'On-disk folders from stacks that no longer exist (reclaimable)',
      bytes: breakdown.orphanedBuildCheckoutBytes,
      count: breakdown.orphanedBuildCheckoutCount,
      tone: 'reclaimable' as const,
    },
  ].filter((row) => row.bytes > 0 || row.count > 0)

  const accountedBytes = rows.reduce((sum, row) => sum + row.bytes, 0)

  return (
    <div className="overflow-x-auto">
      <table className="min-w-full text-left">
        <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-500">
          <tr>
            <th className="px-4 py-2">Category</th>
            <th className="px-4 py-2">Items</th>
            <th className="px-4 py-2">Size</th>
            <th className="px-4 py-2">Status</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.label} className="border-t border-gray-100">
              <td className="px-4 py-3">
                <div className="text-sm font-medium text-gray-900">{row.label}</div>
                <div className="text-xs text-gray-500">{row.detail}</div>
              </td>
              <td className="px-4 py-3 text-sm text-gray-600">{row.count > 0 ? row.count : '—'}</td>
              <td className="px-4 py-3 text-sm font-medium text-gray-800">{formatBytes(row.bytes)}</td>
              <td className="px-4 py-3">
                {row.tone === 'active' ? (
                  <span className="inline-flex rounded-full border border-emerald-200 bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-800">
                    Protected
                  </span>
                ) : row.tone === 'reclaimable' ? (
                  <span className="inline-flex rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-800">
                    Reclaimable
                  </span>
                ) : (
                  <span className="inline-flex rounded-full border border-gray-200 bg-gray-50 px-2 py-0.5 text-xs font-medium text-gray-600">
                    Mixed
                  </span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
        <tfoot className="border-t border-gray-200 bg-gray-50">
          <tr>
            <td className="px-4 py-3 text-sm font-medium text-gray-900">Accounted on this breakdown</td>
            <td className="px-4 py-3" />
            <td className="px-4 py-3 text-sm font-semibold text-gray-900">{formatBytes(accountedBytes)}</td>
            <td className="px-4 py-3 text-xs text-gray-500">
              Up to {formatBytes(breakdown.reclaimableBytes)} reclaimable
            </td>
          </tr>
        </tfoot>
      </table>
    </div>
  )
}

function ImageRow({
  image,
  busy,
  onDelete,
  showOwner = false,
}: {
  image: StackDockerImageDto
  busy: boolean
  onDelete: () => void
  showOwner?: boolean
}) {
  return (
    <tr className="border-t border-gray-100">
      <td className="px-4 py-3 font-mono text-xs text-gray-800">
        {image.reference}
        {showOwner && image.ownerStackId && (
          <div className="mt-1 font-sans text-[11px] text-gray-500">Stack {image.ownerStackId}</div>
        )}
      </td>
      <td className="px-4 py-3 text-sm text-gray-600">{formatBytes(image.sizeBytes)}</td>
      <td className="px-4 py-3 text-sm text-gray-600">{formatDate(image.createdAt)}</td>
      <td className="px-4 py-3">
        <StatusBadge active={image.isActive} reason={image.activeReason} />
      </td>
      <td className="px-4 py-3 text-right">
        <DeleteButton
          disabled={busy || image.isActive}
          title={image.isActive ? image.activeReason ?? 'Image is in use' : `Delete ${image.reference}`}
          onClick={onDelete}
        />
      </td>
    </tr>
  )
}

function ReclaimableBreakdownSection({ breakdown }: { breakdown: DockerReclaimableBreakdownDto }) {
  const rows = [
    {
      label: 'Build cache',
      detail: 'Docker BuildKit cache from past compiles',
      bytes: breakdown.buildCacheBytes,
      count: breakdown.buildCacheBytes > 0 ? 1 : 0,
    },
    {
      label: 'Dangling image layers',
      detail: 'Untagged intermediate layers left by builds',
      bytes: breakdown.danglingImageBytes,
      count: breakdown.danglingImageCount,
    },
    {
      label: 'Unused tagged images',
      detail: 'Images from deleted stacks or old builds — not required by any managed stack',
      bytes: breakdown.unusedTaggedImageBytes,
      count: breakdown.unusedTaggedImageCount,
    },
    {
      label: 'Orphaned build checkouts',
      detail: 'On-disk folders from deleted stacks',
      bytes: breakdown.obsoleteBuildDirBytes,
      count: breakdown.obsoleteBuildDirCount,
    },
  ].filter((row) => row.bytes > 0 || row.count > 0)

  if (rows.length === 0) {
    return null
  }

  return (
    <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
      <div className="border-b border-gray-100 px-4 py-3">
        <h3 className="font-medium text-gray-900">Reclaimable space breakdown</h3>
        <p className="mt-1 text-xs text-gray-500">
          Up to {formatBytes(breakdown.engineReclaimableBytes)} can be reclaimed with &quot;Reclaim disk space&quot;. Item
          sizes below may overlap when layers are shared.
        </p>
      </div>
      <div className="overflow-x-auto">
        <table className="min-w-full text-left">
          <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-500">
            <tr>
              <th className="px-4 py-2">Category</th>
              <th className="px-4 py-2">Items</th>
              <th className="px-4 py-2">Size</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.label} className="border-t border-gray-100">
                <td className="px-4 py-3">
                  <div className="text-sm font-medium text-gray-900">{row.label}</div>
                  <div className="text-xs text-gray-500">{row.detail}</div>
                </td>
                <td className="px-4 py-3 text-sm text-gray-600">{row.count > 0 ? row.count : '—'}</td>
                <td className="px-4 py-3 text-sm text-gray-800">{formatBytes(row.bytes)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function DanglingImagesSection({
  images,
  busy,
  onDelete,
}: {
  images: StackDockerImageDto[]
  busy: boolean
  onDelete: (image: StackDockerImageDto) => void
}) {
  const [expanded, setExpanded] = useState(false)
  if (images.length === 0) {
    return null
  }

  const totalBytes = images.reduce((sum, image) => sum + image.sizeBytes, 0)
  const visible = expanded ? images : images.slice(0, 10)

  return (
    <section className="rounded-lg border border-amber-200 bg-amber-50/40 shadow-sm">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-amber-100 px-4 py-3">
        <div className="flex items-center gap-2">
          <ImageIcon className="h-4 w-4 text-amber-700" />
          <div>
            <h3 className="font-medium text-amber-950">Dangling build layers</h3>
            <p className="text-xs text-amber-800">
              {images.length} untagged layer(s), {formatBytes(totalBytes)} total. Use &quot;Reclaim disk space&quot; to
              prune them all.
            </p>
          </div>
        </div>
        {images.length > 10 && (
          <button
            type="button"
            onClick={() => setExpanded((value) => !value)}
            className="rounded-md border border-amber-300 px-3 py-1.5 text-xs font-medium text-amber-900 hover:bg-amber-100"
          >
            {expanded ? 'Show less' : `Show all ${images.length}`}
          </button>
        )}
      </div>
      <div className="overflow-x-auto">
        <table className="min-w-full text-left">
          <thead className="bg-amber-100/60 text-xs uppercase tracking-wide text-amber-900">
            <tr>
              <th className="px-4 py-2">Image id</th>
              <th className="px-4 py-2">Size</th>
              <th className="px-4 py-2">Created</th>
              <th className="px-4 py-2 text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            {visible.map((image) => (
              <tr key={image.id} className="border-t border-amber-100">
                <td className="px-4 py-3 font-mono text-xs text-amber-950">{image.id.slice(0, 19)}</td>
                <td className="px-4 py-3 text-sm text-amber-900">{formatBytes(image.sizeBytes)}</td>
                <td className="px-4 py-3 text-sm text-amber-900">{formatDate(image.createdAt)}</td>
                <td className="px-4 py-3 text-right">
                  <DeleteButton
                    disabled={busy}
                    title={`Delete dangling layer ${image.id.slice(0, 12)}`}
                    onClick={() => onDelete(image)}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function ObsoleteBuildDirsSection({ dirs }: { dirs: DockerObsoleteBuildDirDto[] }) {
  if (dirs.length === 0) {
    return null
  }

  return (
    <section className="rounded-lg border border-amber-200 bg-amber-50/40 shadow-sm">
      <div className="flex items-center gap-2 border-b border-amber-100 px-4 py-3">
        <HardDrive className="h-4 w-4 text-amber-700" />
        <div>
          <h3 className="font-medium text-amber-950">Orphaned build checkouts</h3>
          <p className="text-xs text-amber-800">
            On-disk build folders that no longer belong to a managed stack. Use &quot;Reclaim disk space&quot; to remove
            them.
          </p>
        </div>
      </div>
      <div className="overflow-x-auto">
        <table className="min-w-full text-left">
          <thead className="bg-amber-100/60 text-xs uppercase tracking-wide text-amber-900">
            <tr>
              <th className="px-4 py-2">Stack id</th>
              <th className="px-4 py-2">Path</th>
              <th className="px-4 py-2">Size</th>
            </tr>
          </thead>
          <tbody>
            {dirs.map((dir) => (
              <tr key={dir.stackId} className="border-t border-amber-100">
                <td className="px-4 py-3 font-mono text-xs text-amber-950">{dir.stackId}</td>
                <td className="px-4 py-3 font-mono text-xs text-amber-900">{dir.path}</td>
                <td className="px-4 py-3 text-sm text-amber-900">{formatBytes(dir.sizeBytes)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function BuildFilesSection({
  buildFiles,
  busy,
  onDelete,
}: {
  buildFiles?: StackDockerBuildFilesDto | null
  busy: boolean
  onDelete: () => void
}) {
  return (
    <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
      <div className="flex items-center gap-2 border-b border-gray-100 px-4 py-3">
        <HardDrive className="h-4 w-4 text-gray-500" />
        <h3 className="font-medium text-gray-900">Build files</h3>
      </div>
      {!buildFiles?.exists ? (
        <p className="px-4 py-6 text-sm text-gray-500">No on-disk build checkout for this stack.</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="min-w-full text-left">
            <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-500">
              <tr>
                <th className="px-4 py-2">Path</th>
                <th className="px-4 py-2">Size</th>
                <th className="px-4 py-2">Status</th>
                <th className="px-4 py-2 text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td className="px-4 py-3 font-mono text-xs text-gray-800">{buildFiles.path}</td>
                <td className="px-4 py-3 text-sm text-gray-600">{formatBytes(buildFiles.sizeBytes)}</td>
                <td className="px-4 py-3">
                  <StatusBadge active={buildFiles.isActive} reason={buildFiles.activeReason} />
                </td>
                <td className="px-4 py-3 text-right">
                  <DeleteButton
                    disabled={busy || buildFiles.isActive}
                    title={
                      buildFiles.isActive
                        ? buildFiles.activeReason ?? 'Build files are in use'
                        : 'Delete on-disk build checkout'
                    }
                    onClick={onDelete}
                  />
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

function ResourceSection({
  title,
  icon,
  emptyLabel,
  subtitle,
  children,
}: {
  title: string
  icon: React.ReactNode
  emptyLabel: string
  subtitle?: string
  children: React.ReactNode
}) {
  const rows = Array.isArray(children) ? children : [children]
  const hasRows = rows.some((row) => row !== false && row !== null && row !== undefined)

  return (
    <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
      <div className="border-b border-gray-100 px-4 py-3">
        <div className="flex items-center gap-2">
          {icon}
          <h3 className="font-medium text-gray-900">{title}</h3>
        </div>
        {subtitle && <p className="mt-1 text-xs text-gray-500">{subtitle}</p>}
      </div>
      {!hasRows ? (
        <p className="px-4 py-6 text-sm text-gray-500">{emptyLabel}</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="min-w-full text-left">
            <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-500">
              <tr>
                <th className="px-4 py-2">Name</th>
                <th className="px-4 py-2">Size</th>
                <th className="px-4 py-2">{title.includes('Volumes') ? 'Links' : 'Created'}</th>
                <th className="px-4 py-2">Status</th>
                <th className="px-4 py-2 text-right">Actions</th>
              </tr>
            </thead>
            <tbody>{children}</tbody>
          </table>
        </div>
      )}
    </section>
  )
}

function StatusBadge({ active, reason }: { active: boolean; reason?: string | null }) {
  return active ? (
    <span
      className="inline-flex items-center gap-1 rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-800"
      title={reason ?? 'In use'}
    >
      <Lock className="h-3 w-3" />
      Active
    </span>
  ) : (
    <span className="inline-flex rounded-full border border-gray-200 bg-gray-50 px-2 py-0.5 text-xs font-medium text-gray-600">
      Unused
    </span>
  )
}

function DeleteButton({
  disabled,
  title,
  onClick,
}: {
  disabled: boolean
  title: string
  onClick: () => void
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      title={title}
      className="inline-flex items-center gap-1 rounded px-2 py-1 text-xs font-medium text-red-700 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
    >
      {disabled ? <Lock className="h-3.5 w-3.5" /> : <Trash2 className="h-3.5 w-3.5" />}
      Delete
    </button>
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
