import { useEffect, useRef, useState } from 'react'
import { AlertCircle, ChevronDown, HardDrive, Image as ImageIcon, Layers, Loader2, Lock, RefreshCw, Trash2 } from 'lucide-react'
import {
  useDeleteDockerBuildFiles,
  useDeleteDockerImage,
  useDeleteDockerVolume,
  useStackDockerOverview,
} from '@/hooks/useStackDocker'
import { useDockerCleanupJob } from '@/hooks/useDockerCleanupJob'
import { formatBytes, formatDate } from '@/components/docker/DockerDiskUsage'
import StackDockerSectionTabs, { type StackDockerSection } from '@/components/docker/StackDockerSectionTabs'
import VolumeAuditSection from '@/components/docker/VolumeAuditSection'
import { apiErrorMessage as errorMessage, cn } from '@/lib/utils'
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
  const [section, setSection] = useState<StackDockerSection>('overview')
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
  const stackImageBytes = data.images.reduce((sum, image) => sum + image.sizeBytes, 0)
  const stackVolumeBytes = data.volumes.reduce((sum, volume) => sum + (volume.sizeBytes ?? 0), 0)

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-xl font-semibold text-gray-900">Docker</h2>
          <p className="mt-1 max-w-2xl text-sm text-gray-500">
            Disk usage, stack resources, and volume audit for this stack&apos;s Docker engine.
          </p>
        </div>
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

      <StatusBanners
        cleanupRunning={cleanupRunning}
        cleanupJob={cleanupJob}
        notice={notice}
        actionError={actionError}
        onDismissNotice={() => setNotice(null)}
        onDismissError={() => setActionError(null)}
      />

      <StackDockerSectionTabs
        active={section}
        onChange={setSection}
        imageCount={data.images.length + data.unusedImages.length}
        volumeCount={data.volumes.length}
      />

      <div role="tabpanel">
        {section === 'overview' && (
          <StackOverviewPanel
            data={data}
            reclaimableBytes={reclaimableBytes}
            reclaimEstimate={reclaimEstimate}
            oldBuildsBytes={oldBuildsBytes}
            oldBuildsEstimate={oldBuildsEstimate}
            stackImageBytes={stackImageBytes}
            stackVolumeBytes={stackVolumeBytes}
            busy={busy}
            cleanupRunning={cleanupRunning}
            cleanupStarting={cleanupStarting}
            cleanupJob={cleanupJob}
            onReclaim={() => setConfirmCleanup(true)}
            onCleanOldBuilds={() => setConfirmOldBuildsCleanup(true)}
            onGoToResources={() => setSection('resources')}
            onGoToAudit={() => setSection('audit')}
          />
        )}

        {section === 'resources' && (
          <ResourcesSubTab
            data={data}
            busy={busy}
            deletableUnusedImages={deletableUnusedImages}
            onDeleteBuildFiles={() => setConfirmBuildDelete(true)}
            onDeleteImage={setConfirmImage}
            onDeleteVolume={setConfirmVolume}
          />
        )}

        {section === 'audit' && <VolumeAuditSection stackId={stackId} />}

        {section === 'disk' && <DiskUsageSubTab data={data} />}
      </div>

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

function StatusBanners({
  cleanupRunning,
  cleanupJob,
  notice,
  actionError,
  onDismissNotice,
  onDismissError,
}: {
  cleanupRunning: boolean
  cleanupJob: ReturnType<typeof useDockerCleanupJob>['job']
  notice: string | null
  actionError: string | null
  onDismissNotice: () => void
  onDismissError: () => void
}) {
  if (!cleanupRunning && !notice && !actionError) return null

  return (
    <div className="space-y-2">
      {cleanupRunning && cleanupJob && (
        <div className="flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
          <Loader2 className="mt-0.5 h-4 w-4 shrink-0 animate-spin" />
          <div>
            <p className="font-medium">
              {cleanupJob.action === 'CleanupOldBuilds'
                ? 'Cleaning up old builds in the background…'
                : 'Reclaiming disk space in the background…'}
            </p>
            <p className="mt-0.5 text-amber-800">{cleanupJob.message}</p>
          </div>
        </div>
      )}
      {notice && (
        <div className="flex items-start justify-between gap-3 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">
          <span>{notice}</span>
          <button type="button" onClick={onDismissNotice} className="shrink-0 text-green-700 hover:underline">
            Dismiss
          </button>
        </div>
      )}
      {actionError && (
        <div className="flex items-start justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          <span>{actionError}</span>
          <button type="button" onClick={onDismissError} className="shrink-0 text-red-600 hover:underline">
            Dismiss
          </button>
        </div>
      )}
    </div>
  )
}

function StackOverviewPanel({
  data,
  reclaimableBytes,
  reclaimEstimate,
  oldBuildsBytes,
  oldBuildsEstimate,
  stackImageBytes,
  stackVolumeBytes,
  busy,
  cleanupRunning,
  cleanupStarting,
  cleanupJob,
  onReclaim,
  onCleanOldBuilds,
  onGoToResources,
  onGoToAudit,
}: {
  data: StackDockerOverviewDto
  reclaimableBytes: number
  reclaimEstimate: string
  oldBuildsBytes: number
  oldBuildsEstimate: string
  stackImageBytes: number
  stackVolumeBytes: number
  busy: boolean
  cleanupRunning: boolean
  cleanupStarting: boolean
  cleanupJob: ReturnType<typeof useDockerCleanupJob>['job']
  onReclaim: () => void
  onCleanOldBuilds: () => void
  onGoToResources: () => void
  onGoToAudit: () => void
}) {
  const disk = data.diskUsage
  const percent = disk && disk.totalBytes > 0 ? Math.min(Math.max(disk.usedPercent, 0), 100) : 0
  const critical = percent >= 85
  const warning = percent >= 65 && !critical
  const reclaimBusy =
    busy &&
    (cleanupRunning ||
      cleanupStarting ||
      (cleanupJob?.isRunning && cleanupJob.action !== 'CleanupOldBuilds'))
  const oldBuildsBusy = busy && cleanupRunning && cleanupJob?.action === 'CleanupOldBuilds'

  return (
    <div className="space-y-4">
      <div
        className={cn(
          'overflow-hidden rounded-xl border shadow-sm',
          critical
            ? 'border-red-300 bg-linear-to-br from-red-50 to-white'
            : warning
              ? 'border-amber-300 bg-linear-to-br from-amber-50 to-white'
              : 'border-slate-200 bg-linear-to-br from-slate-50 to-white',
        )}
      >
        <div className="px-5 py-5 sm:px-6">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">Docker disk usage</p>
              {disk && disk.totalBytes > 0 ? (
                <>
                  <p className="mt-1 text-3xl font-bold tabular-nums text-slate-900">{percent.toFixed(0)}%</p>
                  <p className="mt-1 text-sm text-slate-600">
                    {formatBytes(disk.usedBytes)} used · {formatBytes(disk.availableBytes)} free of{' '}
                    {formatBytes(disk.totalBytes)}
                  </p>
                </>
              ) : (
                <p className="mt-2 text-sm text-slate-500">Disk usage unavailable on this host.</p>
              )}
            </div>
            {reclaimableBytes > 0 && (
              <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-right">
                <p className="text-[11px] font-medium uppercase tracking-wide text-amber-800">Reclaimable now</p>
                <p className="text-xl font-bold tabular-nums text-amber-950">{reclaimEstimate}</p>
              </div>
            )}
          </div>

          {disk && disk.totalBytes > 0 && (
            <div className="mt-4 h-3 w-full overflow-hidden rounded-full bg-white/80 ring-1 ring-slate-200">
              <div
                className={cn(
                  'h-full rounded-full transition-all',
                  critical ? 'bg-red-500' : warning ? 'bg-amber-500' : 'bg-emerald-500',
                )}
                style={{ width: `${percent}%` }}
              />
            </div>
          )}

          <div className="mt-5 flex flex-wrap gap-2">
            <button
              type="button"
              onClick={onReclaim}
              disabled={busy}
              className="inline-flex items-center gap-2 rounded-lg bg-amber-600 px-4 py-2.5 text-sm font-semibold text-white hover:bg-amber-700 disabled:opacity-50"
            >
              {reclaimBusy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
              Reclaim disk space
              {reclaimableBytes > 0 && <span className="font-normal opacity-90">· up to {reclaimEstimate}</span>}
            </button>
            <button
              type="button"
              onClick={onCleanOldBuilds}
              disabled={busy}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-4 py-2.5 text-sm font-medium text-slate-800 hover:bg-slate-50 disabled:opacity-50"
            >
              {oldBuildsBusy ? <Loader2 className="h-4 w-4 animate-spin" /> : <HardDrive className="h-4 w-4" />}
              Clean up old builds
              {oldBuildsBytes > 0 && <span className="font-normal text-slate-500">· up to {oldBuildsEstimate}</span>}
            </button>
          </div>
        </div>

        <div className="grid gap-px border-t border-slate-200 bg-slate-200 sm:grid-cols-3">
          <MetricCell
            label="Stack images"
            value={formatBytes(stackImageBytes)}
            detail={`${data.images.length} image(s)`}
            onClick={onGoToResources}
          />
          <MetricCell
            label="Stack volumes"
            value={formatBytes(stackVolumeBytes)}
            detail={`${data.volumes.length} volume(s)`}
            onClick={onGoToResources}
          />
          <MetricCell
            label="Volume audit"
            value="Run audit"
            detail="Orphans & overlay drift"
            onClick={onGoToAudit}
          />
        </div>
      </div>

      <p className="text-xs text-slate-500">
        Estimated footprint on this page: <span className="font-medium">{formatBytes(data.totalBytes)}</span>
        {data.buildCacheBytes > 0 && (
          <>
            {' '}
            · Build cache on engine: {formatBytes(data.buildCacheBytes)}
          </>
        )}
        . Use <strong>Volume audit</strong> to find orphan volumes and stale overlay files safe to delete.
      </p>
    </div>
  )
}

function MetricCell({
  label,
  value,
  detail,
  onClick,
}: {
  label: string
  value: string
  detail?: string
  onClick?: () => void
}) {
  const content = (
    <>
      <p className="text-[11px] font-medium uppercase tracking-wide text-slate-500">{label}</p>
      <p className="mt-1 text-lg font-semibold text-slate-900">{value}</p>
      {detail && <p className="mt-0.5 text-xs text-slate-500">{detail}</p>}
    </>
  )

  if (onClick) {
    return (
      <button
        type="button"
        onClick={onClick}
        className="bg-white px-4 py-3 text-left transition-colors hover:bg-slate-50"
      >
        {content}
        <span className="mt-1 inline-block text-xs font-medium text-blue-600">Manage →</span>
      </button>
    )
  }

  return <div className="bg-white px-4 py-3">{content}</div>
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
    <div className="space-y-4">
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

      {data.buildFiles?.exists && (
        <details className="rounded-lg border border-gray-200 bg-white shadow-sm">
          <summary className="flex cursor-pointer list-none items-center justify-between gap-2 px-4 py-3 [&::-webkit-details-marker]:hidden">
            <span className="inline-flex items-center gap-2 text-sm font-medium text-gray-900">
              <HardDrive className="h-4 w-4 text-gray-500" />
              Build files
              <span className="font-normal text-gray-500">· {formatBytes(data.buildFiles.sizeBytes)}</span>
            </span>
            <ChevronDown className="h-4 w-4 shrink-0 text-gray-400" />
          </summary>
          <BuildFilesSection buildFiles={data.buildFiles} busy={busy} onDelete={onDeleteBuildFiles} embedded />
        </details>
      )}

      {data.unusedImages.length > 0 && (
        <details className="rounded-lg border border-gray-200 bg-white shadow-sm">
          <summary className="flex cursor-pointer list-none items-center justify-between gap-2 px-4 py-3 [&::-webkit-details-marker]:hidden">
            <span className="inline-flex items-center gap-2 text-sm font-medium text-gray-900">
              <ImageIcon className="h-4 w-4 text-gray-500" />
              Unused / other images
              <span className="font-normal text-gray-500">· {data.unusedImages.length}</span>
            </span>
            <ChevronDown className="h-4 w-4 shrink-0 text-gray-400" />
          </summary>
          <ResourceSection
            title="Unused / other images"
            icon={<ImageIcon className="h-4 w-4" />}
            emptyLabel="No other unused platform images found."
            subtitle={
              deletableUnusedImages.length > 0
                ? `${deletableUnusedImages.length} tagged image(s) from deleted stacks or old builds can be removed.`
                : 'Images required by managed stacks are protected even when stopped.'
            }
            embedded
          >
            {data.unusedImages.map((image) => (
              <ImageRow key={image.id} image={image} busy={busy} onDelete={() => onDeleteImage(image)} showOwner />
            ))}
          </ResourceSection>
        </details>
      )}

      {data.danglingImages.length > 0 && (
        <details className="rounded-lg border border-amber-200 bg-amber-50/40 shadow-sm">
          <summary className="flex cursor-pointer list-none items-center justify-between gap-2 px-4 py-3 [&::-webkit-details-marker]:hidden">
            <span className="inline-flex items-center gap-2 text-sm font-medium text-amber-950">
              <ImageIcon className="h-4 w-4 text-amber-700" />
              Dangling build layers
              <span className="font-normal text-amber-800">· {data.danglingImages.length}</span>
            </span>
            <ChevronDown className="h-4 w-4 shrink-0 text-amber-600" />
          </summary>
          <DanglingImagesSection images={data.danglingImages} busy={busy} onDelete={onDeleteImage} embedded />
        </details>
      )}

      {data.obsoleteBuildDirs.length > 0 && (
        <details className="rounded-lg border border-amber-200 bg-amber-50/40 shadow-sm">
          <summary className="flex cursor-pointer list-none items-center justify-between gap-2 px-4 py-3 [&::-webkit-details-marker]:hidden">
            <span className="inline-flex items-center gap-2 text-sm font-medium text-amber-950">
              <HardDrive className="h-4 w-4 text-amber-700" />
              Orphaned build checkouts
              <span className="font-normal text-amber-800">· {data.obsoleteBuildDirs.length}</span>
            </span>
            <ChevronDown className="h-4 w-4 shrink-0 text-amber-600" />
          </summary>
          <ObsoleteBuildDirsSection dirs={data.obsoleteBuildDirs} embedded />
        </details>
      )}
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
  embedded = false,
}: {
  images: StackDockerImageDto[]
  busy: boolean
  onDelete: (image: StackDockerImageDto) => void
  embedded?: boolean
}) {
  const [expanded, setExpanded] = useState(false)
  if (images.length === 0) {
    return null
  }

  const totalBytes = images.reduce((sum, image) => sum + image.sizeBytes, 0)
  const visible = expanded ? images : images.slice(0, 10)

  const table = (
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
  )

  if (embedded) {
    return (
      <div>
        <p className="border-b border-amber-100 px-4 py-2 text-xs text-amber-800">
          {images.length} untagged layer(s), {formatBytes(totalBytes)} total. Use &quot;Reclaim disk space&quot; to
          prune them all.
        </p>
        {table}
        {images.length > 10 && (
          <div className="border-t border-amber-100 px-4 py-2">
            <button
              type="button"
              onClick={() => setExpanded((value) => !value)}
              className="rounded-md border border-amber-300 px-3 py-1.5 text-xs font-medium text-amber-900 hover:bg-amber-100"
            >
              {expanded ? 'Show less' : `Show all ${images.length}`}
            </button>
          </div>
        )}
      </div>
    )
  }

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
      {table}
    </section>
  )
}

function ObsoleteBuildDirsSection({
  dirs,
  embedded = false,
}: {
  dirs: DockerObsoleteBuildDirDto[]
  embedded?: boolean
}) {
  if (dirs.length === 0) {
    return null
  }

  const table = (
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
  )

  if (embedded) {
    return (
      <div>
        <p className="border-b border-amber-100 px-4 py-2 text-xs text-amber-800">
          On-disk build folders that no longer belong to a managed stack. Use &quot;Reclaim disk space&quot; to remove
          them.
        </p>
        {table}
      </div>
    )
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
      {table}
    </section>
  )
}

function BuildFilesSection({
  buildFiles,
  busy,
  onDelete,
  embedded = false,
}: {
  buildFiles?: StackDockerBuildFilesDto | null
  busy: boolean
  onDelete: () => void
  embedded?: boolean
}) {
  const table =
    buildFiles?.exists &&
    (
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
    )

  if (embedded) {
    return table ?? null
  }

  return (
    <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
      <div className="flex items-center gap-2 border-b border-gray-100 px-4 py-3">
        <HardDrive className="h-4 w-4 text-gray-500" />
        <h3 className="font-medium text-gray-900">Build files</h3>
      </div>
      {!buildFiles?.exists ? (
        <p className="px-4 py-6 text-sm text-gray-500">No on-disk build checkout for this stack.</p>
      ) : (
        table
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
  embedded = false,
}: {
  title: string
  icon: React.ReactNode
  emptyLabel: string
  subtitle?: string
  children: React.ReactNode
  embedded?: boolean
}) {
  const rows = Array.isArray(children) ? children : [children]
  const hasRows = rows.some((row) => row !== false && row !== null && row !== undefined)

  const table = (
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
  )

  if (embedded) {
    return (
      <div>
        {subtitle && <p className="border-b border-gray-100 px-4 py-2 text-xs text-gray-500">{subtitle}</p>}
        {!hasRows ? <p className="px-4 py-6 text-sm text-gray-500">{emptyLabel}</p> : table}
      </div>
    )
  }

  return (
    <section className="rounded-lg border border-gray-200 bg-white shadow-sm">
      <div className="border-b border-gray-100 px-4 py-3">
        <div className="flex items-center gap-2">
          {icon}
          <h3 className="font-medium text-gray-900">{title}</h3>
        </div>
        {subtitle && <p className="mt-1 text-xs text-gray-500">{subtitle}</p>}
      </div>
      {!hasRows ? <p className="px-4 py-6 text-sm text-gray-500">{emptyLabel}</p> : table}
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
