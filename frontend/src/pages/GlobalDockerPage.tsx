import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  AlertCircle,
  ChevronDown,
  FolderOpen,
  HardDrive,
  Loader2,
  Lock,
  RefreshCw,
  Trash2,
} from 'lucide-react'
import { formatBytes } from '@/components/docker/DockerDiskUsage'
import DockerSectionTabs, { type DockerPageSection } from '@/components/docker/DockerSectionTabs'
import { ManagerVolumeBrowser } from '@/components/docker/ManagerVolumeBrowser'
import VolumeAuditSection from '@/components/docker/VolumeAuditSection'
import { useDockerCleanupJob } from '@/hooks/useDockerCleanupJob'
import {
  useDeleteEngineImage,
  useDeleteEngineVolume,
  useDeleteManagerFile,
  useDockerEngineOverview,
} from '@/hooks/useStackDocker'
import { useStacks } from '@/hooks/useStacks'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import { cn } from '@/lib/utils'
import type {
  DockerEngineImageDto,
  DockerEngineOverviewDto,
  DockerEngineVolumeEntryDto,
} from '@/types/docker.types'

export default function GlobalDockerPage() {
  const { data, isLoading, isError, error, refetch, isFetching } = useDockerEngineOverview()
  const deleteVolume = useDeleteEngineVolume()
  const deleteEngineImage = useDeleteEngineImage()
  const deleteManagerFile = useDeleteManagerFile()
  const { job: cleanupJob, isRunning: cleanupRunning, startCleanup, invalidateDockerQueries } =
    useDockerCleanupJob()

  const [section, setSection] = useState<DockerPageSection>('overview')
  const [notice, setNotice] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [confirmCleanup, setConfirmCleanup] = useState(false)
  const [confirmOldBuilds, setConfirmOldBuilds] = useState(false)
  const [confirmVolume, setConfirmVolume] = useState<DockerEngineVolumeEntryDto | null>(null)
  const [confirmImage, setConfirmImage] = useState<DockerEngineImageDto | null>(null)
  const [confirmManagerDir, setConfirmManagerDir] = useState<{ name: string; relativePath: string } | null>(null)
  const [selectedVolumes, setSelectedVolumes] = useState<Set<string>>(new Set())
  const lastHandledCleanupJobRef = useRef<string | null>(null)

  const busy =
    isFetching ||
    deleteVolume.isPending ||
    deleteEngineImage.isPending ||
    deleteManagerFile.isPending ||
    cleanupRunning

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
      const reclaimable =
        data?.reclaimableBytes ?? data?.reclaimableBreakdown?.listedReclaimableBytes ?? 0
      if (cleanupJob.freedBytes) {
        msg += ` ${verb} about ${formatBytes(cleanupJob.freedBytes)}.`
      } else if (reclaimable > 0) {
        msg +=
          ' Nothing was freed — the estimate may include Docker cache or images still in use by protected stacks.'
      }
      setNotice(msg)
      setActionError(null)
    } else if (cleanupJob.phase === 'Failed') {
      setActionError(cleanupJob.message + (cleanupJob.error ? ` ${cleanupJob.error}` : ''))
    }
  }, [cleanupJob, data, invalidateDockerQueries, refetch])

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
  const deletableVolumes = data.volumeGroups.flatMap((g) => g.volumes.filter((v) => v.isDeletable))
  const deletableImages = data.images.filter((i) => i.isDeletable)
  const volumeCount = data.volumeGroups.reduce((sum, group) => sum + group.volumes.length, 0)

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
      const result = await deleteEngineImage.mutateAsync(target.id)
      setNotice(result.data.message)
      await refetch()
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

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

  return (
    <div className="mx-auto max-w-6xl space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Docker</h1>
          <p className="mt-1 max-w-2xl text-sm text-gray-500">
            Engine-wide disk usage and cleanup. Stack-specific Docker details live under each stack&apos;s
            Advanced tab.
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

      <DockerSectionTabs
        active={section}
        onChange={setSection}
        volumeCount={volumeCount}
        imageCount={data.images.length}
        deletableVolumeCount={data.deletableVolumeCount}
      />

      <div role="tabpanel">
        {section === 'overview' && (
          <OverviewPanel
            data={data}
            reclaimable={reclaimable}
            deletableVolumeCount={deletableVolumes.length}
            deletableImageCount={deletableImages.length}
            busy={busy}
            onReclaim={() => setConfirmCleanup(true)}
            onCleanOldBuilds={() => setConfirmOldBuilds(true)}
            onGoToVolumes={() => setSection('volumes')}
            onGoToImages={() => setSection('images')}
            onGoToAudit={() => setSection('audit')}
          />
        )}

        {section === 'volumes' && (
          <VolumesPanel
            data={data}
            selectedVolumes={selectedVolumes}
            deletableVolumes={deletableVolumes}
            busy={busy}
            onToggleVolume={toggleVolume}
            onSelectAll={selectAllDeletableVolumes}
            onBulkDelete={() => void handleBulkDeleteVolumes()}
            onDeleteVolume={setConfirmVolume}
          />
        )}

        {section === 'images' && (
          <ImagesPanel data={data} busy={busy} onDeleteImage={setConfirmImage} />
        )}

        {section === 'manager' && data.managerVolume && (
          <ManagerPanel data={data} onDeleteDir={setConfirmManagerDir} />
        )}

        {section === 'audit' && <GlobalVolumeAuditPanel />}
      </div>

      {confirmCleanup && (
        <ConfirmDialog
          title="Reclaim disk space?"
          message={
            reclaimable > 0
              ? `Prunes build cache and removes up to ${formatBytes(reclaimable)} of unused images and orphaned build checkouts. Managed stack images and volumes are kept. Delete unused volumes separately under the Volumes tab.`
              : 'Prunes build cache and dangling image layers if present. Unused Docker volumes are not removed — delete those under the Volumes tab.'
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
              ? `Remove ${confirmManagerDir.name}/ from the manager volume? Client data lives in per-stack Docker volumes, so this only deletes leftover manager files.`
              : `Remove ${confirmManagerDir.name}/ and everything inside it from the manager volume?`
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
            <p className="font-medium">Cleanup running…</p>
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

function OverviewPanel({
  data,
  reclaimable,
  deletableVolumeCount,
  deletableImageCount,
  busy,
  onReclaim,
  onCleanOldBuilds,
  onGoToVolumes,
  onGoToImages,
  onGoToAudit,
}: {
  data: DockerEngineOverviewDto
  reclaimable: number
  deletableVolumeCount: number
  deletableImageCount: number
  busy: boolean
  onReclaim: () => void
  onCleanOldBuilds: () => void
  onGoToVolumes: () => void
  onGoToImages: () => void
  onGoToAudit: () => void
}) {
  const disk = data.diskUsage
  const percent = disk && disk.totalBytes > 0 ? Math.min(Math.max(disk.usedPercent, 0), 100) : 0
  const critical = percent >= 85
  const warning = percent >= 65 && !critical

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
            {reclaimable > 0 && (
              <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-right">
                <p className="text-[11px] font-medium uppercase tracking-wide text-amber-800">Reclaimable now</p>
                <p className="text-xl font-bold tabular-nums text-amber-950">{formatBytes(reclaimable)}</p>
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
              {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
              Reclaim disk space
            </button>
            <button
              type="button"
              onClick={onCleanOldBuilds}
              disabled={busy}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-4 py-2.5 text-sm font-medium text-slate-800 hover:bg-slate-50 disabled:opacity-50"
            >
              Clean up old builds
            </button>
          </div>
        </div>

        <div className="grid gap-px border-t border-slate-200 bg-slate-200 sm:grid-cols-2 lg:grid-cols-4">
          <MetricCell label="Volumes" value={formatBytes(data.totalVolumeBytes)} detail={`${deletableVolumeCount} deletable`} onClick={onGoToVolumes} />
          <MetricCell label="Images" value={formatBytes(data.totalImageBytes)} detail={`${deletableImageCount} deletable`} onClick={onGoToImages} />
          <MetricCell
            label="Build cache"
            value={formatBytes(disk?.dockerBuildCacheBytes)}
            detail="Included in reclaim"
          />
          <MetricCell label="Volume audit" value="Run audit" detail="Orphans & overlay drift" onClick={onGoToAudit} />
        </div>
      </div>

      <p className="text-xs text-slate-500">
        <strong>Reclaim disk space</strong> prunes Docker build cache and unused images.{' '}
        <strong>Volumes</strong> must be deleted individually under the Volumes tab — they are never removed
        automatically.
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

function VolumesPanel({
  data,
  selectedVolumes,
  deletableVolumes,
  busy,
  onToggleVolume,
  onSelectAll,
  onBulkDelete,
  onDeleteVolume,
}: {
  data: DockerEngineOverviewDto
  selectedVolumes: Set<string>
  deletableVolumes: DockerEngineVolumeEntryDto[]
  busy: boolean
  onToggleVolume: (name: string) => void
  onSelectAll: () => void
  onBulkDelete: () => void
  onDeleteVolume: (volume: DockerEngineVolumeEntryDto) => void
}) {
  return (
    <section className="rounded-xl border border-gray-200 bg-white shadow-sm">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-100 px-4 py-3">
        <div>
          <h2 className="font-medium text-gray-900">Volumes on engine</h2>
          <p className="text-xs text-gray-500">{formatBytes(data.totalVolumeBytes)} total across all stacks</p>
        </div>
        {deletableVolumes.length > 0 && (
          <div className="flex gap-2">
            <button
              type="button"
              onClick={onSelectAll}
              className="rounded-md border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
            >
              Select all deletable ({deletableVolumes.length})
            </button>
            <button
              type="button"
              onClick={onBulkDelete}
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
          <VolumeGroupSection
            key={`${group.category}-${group.stackId ?? 'none'}`}
            group={group}
            selectedVolumes={selectedVolumes}
            busy={busy}
            onToggleVolume={onToggleVolume}
            onDeleteVolume={onDeleteVolume}
          />
        ))}
      </div>
    </section>
  )
}

function VolumeGroupSection({
  group,
  selectedVolumes,
  busy,
  onToggleVolume,
  onDeleteVolume,
}: {
  group: DockerEngineOverviewDto['volumeGroups'][number]
  selectedVolumes: Set<string>
  busy: boolean
  onToggleVolume: (name: string) => void
  onDeleteVolume: (volume: DockerEngineVolumeEntryDto) => void
}) {
  const deletableInGroup = group.volumes.filter((v) => v.isDeletable).length
  const defaultOpen = deletableInGroup > 0

  return (
    <details open={defaultOpen} className="group px-4 py-3">
      <summary className="cursor-pointer list-none marker:content-none">
        <div className="flex flex-wrap items-baseline gap-2">
          <span className="text-sm font-medium text-gray-900">{group.category}</span>
          {group.stackName && (
            <span className="text-xs text-gray-500">
              {group.stackName}
              {group.stackId && (
                <>
                  {' '}
                  ·{' '}
                  <Link to={`/stacks/${group.stackId}`} className="text-blue-600 hover:underline" onClick={(e) => e.stopPropagation()}>
                    Open stack
                  </Link>
                </>
              )}
            </span>
          )}
          <span className="text-xs text-gray-400">
            {group.volumes.length} volume{group.volumes.length === 1 ? '' : 's'} · {formatBytes(group.totalBytes)}
          </span>
        </div>
      </summary>
      <div className="mt-3 overflow-x-auto">
        <table className="min-w-full text-left text-sm">
          <thead className="text-xs uppercase text-gray-500">
            <tr>
              <th className="w-8 py-1" />
              <th className="py-1 pr-4">Volume</th>
              <th className="py-1 pr-4">Size</th>
              <th className="py-1 pr-4 hidden sm:table-cell">Links</th>
              <th className="py-1 pr-4 hidden md:table-cell">Status</th>
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
                      onChange={() => onToggleVolume(volume.name)}
                      className="rounded border-gray-300"
                    />
                  ) : null}
                </td>
                <td className="py-2 pr-4 font-mono text-xs">{volume.name}</td>
                <td className="py-2 pr-4">{formatBytes(volume.sizeBytes)}</td>
                <td className="py-2 pr-4 hidden sm:table-cell">{volume.linkCount}</td>
                <td className="py-2 pr-4 text-xs text-gray-600 hidden md:table-cell">{volume.detail}</td>
                <td className="py-2 text-right">
                  {volume.isDeletable ? (
                    <button
                      type="button"
                      onClick={() => onDeleteVolume(volume)}
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
    </details>
  )
}

function ImagesPanel({
  data,
  busy,
  onDeleteImage,
}: {
  data: DockerEngineOverviewDto
  busy: boolean
  onDeleteImage: (image: DockerEngineImageDto) => void
}) {
  return (
    <section className="rounded-xl border border-gray-200 bg-white shadow-sm">
      <div className="border-b border-gray-100 px-4 py-3">
        <h2 className="font-medium text-gray-900">Images on engine</h2>
        <p className="text-xs text-gray-500">{formatBytes(data.totalImageBytes)} total · {data.images.length} images</p>
      </div>
      <div className="overflow-x-auto">
        <table className="min-w-full text-left text-sm">
          <thead className="bg-gray-50 text-xs uppercase text-gray-500">
            <tr>
              <th className="px-4 py-2">Image</th>
              <th className="px-4 py-2 hidden sm:table-cell">Category</th>
              <th className="px-4 py-2">Size</th>
              <th className="px-4 py-2 hidden sm:table-cell">Containers</th>
              <th className="px-4 py-2 text-right">Actions</th>
            </tr>
          </thead>
          <tbody>
            {data.images.map((image) => (
              <tr key={image.id} className="border-t border-gray-100">
                <td className="px-4 py-3 font-mono text-xs">{image.reference}</td>
                <td className="px-4 py-3 text-gray-600 hidden sm:table-cell">{image.category}</td>
                <td className="px-4 py-3">{formatBytes(image.sizeBytes)}</td>
                <td className="px-4 py-3 hidden sm:table-cell">{image.containerCount}</td>
                <td className="px-4 py-3 text-right">
                  {image.isDeletable ? (
                    <button
                      type="button"
                      onClick={() => onDeleteImage(image)}
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
  )
}

function ManagerPanel({
  data,
  onDeleteDir,
}: {
  data: DockerEngineOverviewDto
  onDeleteDir: (dir: { name: string; relativePath: string }) => void
}) {
  const manager = data.managerVolume!
  const hasDirectories = manager.directories.length > 0
  const [browserOpen, setBrowserOpen] = useState(true)

  return (
    <section className="overflow-hidden rounded-xl border border-blue-200 bg-white shadow-sm">
      <div className="flex items-center gap-2 border-b border-blue-100 bg-blue-50/50 px-4 py-3">
        <HardDrive className="h-4 w-4 text-blue-700" />
        <div className="min-w-0 flex-1">
          <h2 className="font-medium text-gray-900">Manager data volume</h2>
          <p className="truncate text-xs text-gray-500">{manager.detail}</p>
        </div>
        <span className="inline-flex shrink-0 items-center gap-1 rounded-full border border-blue-200 bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-800">
          <Lock className="h-3 w-3" />
          Protected
        </span>
      </div>

      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-blue-100 bg-white px-4 py-3">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-gray-700">
          <span className="font-mono text-xs">{manager.name}</span>
          <span className="text-gray-300">·</span>
          <span className="font-medium">{formatBytes(manager.totalBytes)}</span>
        </div>
        <button
          type="button"
          onClick={() => setBrowserOpen((open) => !open)}
          className={cn(
            'inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-semibold transition-colors',
            browserOpen
              ? 'bg-blue-600 text-white hover:bg-blue-700'
              : 'border border-blue-300 bg-blue-50 text-blue-800 hover:bg-blue-100',
          )}
        >
          <FolderOpen className="h-4 w-4" />
          {browserOpen ? 'Hide file browser' : 'Browse volume files'}
          <ChevronDown className={cn('h-4 w-4 transition-transform', browserOpen && 'rotate-180')} />
        </button>
      </div>

      {browserOpen && (
        <div className="border-b border-blue-100 bg-blue-50/30">
          <div className="border-b border-blue-100 px-4 py-2">
            <p className="text-xs text-blue-900/80">
              Inspect platform data or remove deletable files from the manager volume.
            </p>
          </div>
          <ManagerVolumeBrowser />
        </div>
      )}

      {hasDirectories && (
        <details className="border-t border-gray-100">
          <summary className="cursor-pointer list-none px-4 py-3 text-sm font-medium text-slate-700 marker:content-none">
            Top-level directories ({manager.directories.length})
          </summary>
          <div className="overflow-x-auto border-t border-gray-100">
            <table className="min-w-full text-left text-sm">
              <thead className="bg-gray-50 text-xs uppercase text-gray-500">
                <tr>
                  <th className="px-4 py-2">Directory</th>
                  <th className="px-4 py-2">Size</th>
                  <th className="px-4 py-2 hidden sm:table-cell">Purpose</th>
                  <th className="px-4 py-2" />
                </tr>
              </thead>
              <tbody>
                {manager.directories.map((dir) => (
                  <tr key={dir.name} className="border-t border-gray-100">
                    <td className="px-4 py-2 font-mono text-xs">{dir.name}</td>
                    <td className="px-4 py-2">{formatBytes(dir.sizeBytes)}</td>
                    <td className="px-4 py-2 text-xs text-gray-600 hidden sm:table-cell">{dir.detail ?? '—'}</td>
                    <td className="px-4 py-2 text-right">
                      {dir.isDeletable ? (
                        <button
                          type="button"
                          onClick={() =>
                            onDeleteDir({ name: dir.name, relativePath: dir.relativePath || dir.name })
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
        </details>
      )}
    </section>
  )
}

function GlobalVolumeAuditPanel() {
  const { data: stacks = [], isLoading } = useStacks()
  const [stackId, setStackId] = useState('')

  useEffect(() => {
    if (stackId || stacks.length === 0) return
    setStackId(stacks[0].stackId)
  }, [stackId, stacks])

  if (isLoading) {
    return (
      <div className="flex items-center gap-2 rounded-xl border border-gray-200 bg-white px-4 py-8 text-sm text-gray-600 shadow-sm">
        <Loader2 className="h-4 w-4 animate-spin" />
        Loading stacks…
      </div>
    )
  }

  if (stacks.length === 0) {
    return (
      <div className="rounded-xl border border-gray-200 bg-white px-4 py-8 text-sm text-gray-500 shadow-sm">
        No managed stacks yet. Volume audit runs in the context of a stack&apos;s Docker engine.
      </div>
    )
  }

  const selected = stacks.find((stack) => stack.stackId === stackId)

  return (
    <div className="space-y-4">
      <div className="rounded-xl border border-slate-200 bg-white px-4 py-4 shadow-sm sm:px-5">
        <label htmlFor="audit-stack" className="text-sm font-medium text-gray-900">
          Stack context
        </label>
        <p className="mt-1 text-xs text-gray-500">
          Audits use this stack&apos;s Docker engine connection. Orphan volumes from deleted stacks appear in any
          stack&apos;s audit — pick whichever stack you manage most often.
        </p>
        <select
          id="audit-stack"
          value={stackId}
          onChange={(event) => setStackId(event.target.value)}
          className="mt-3 w-full max-w-md rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
        >
          {stacks.map((stack) => (
            <option key={stack.stackId} value={stack.stackId}>
              {stack.stackName} ({stack.stackId})
            </option>
          ))}
        </select>
        {selected && (
          <p className="mt-2 text-xs text-slate-500">
            Stack-specific overlay drift checks apply when client is enabled.{' '}
            <Link to={`/stacks/${selected.stackId}`} className="font-medium text-blue-600 hover:underline">
              Open stack →
            </Link>
          </p>
        )}
      </div>

      {stackId && <VolumeAuditSection key={stackId} stackId={stackId} />}
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
