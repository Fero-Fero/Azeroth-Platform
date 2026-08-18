import type { DockerDiskUsageDto } from '@/types/docker.types'

export function formatBytes(bytes: number | null | undefined): string {
  if (!bytes) return '-'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1)
  return `${(bytes / Math.pow(1024, i)).toFixed(i === 0 ? 0 : 1)} ${units[i]}`
}

export function formatDate(value?: string | null): string {
  if (!value) return '-'
  return new Date(value).toLocaleString()
}

interface DiskUsageBarProps {
  disk?: DockerDiskUsageDto | null
  reclaimableBytes?: number
  showDetails?: boolean
}

export function DiskUsageBar({ disk, reclaimableBytes, showDetails = true }: DiskUsageBarProps) {
  if (!disk || disk.totalBytes <= 0) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white px-4 py-3 text-sm text-gray-500 shadow-sm">
        Docker disk usage is unavailable on this host.
      </div>
    )
  }

  const percent = Math.min(Math.max(disk.usedPercent, 0), 100)
  const barColor =
    percent >= 85 ? 'bg-red-500' : percent >= 65 ? 'bg-amber-500' : 'bg-emerald-500'

  return (
    <div className="rounded-lg border border-gray-200 bg-white px-4 py-4 shadow-sm">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h3 className="font-medium text-gray-900">Docker disk usage</h3>
          <p className="mt-1 text-sm text-gray-500">
            {formatBytes(disk.usedBytes)} used of {formatBytes(disk.totalBytes)} ({percent.toFixed(1)}%)
          </p>
        </div>
        <div className="text-right text-sm text-gray-600">
          <div>{formatBytes(disk.availableBytes)} free</div>
          {reclaimableBytes ? (
            <div className="text-xs text-gray-500">{formatBytes(reclaimableBytes)} reclaimable on engine</div>
          ) : disk.reclaimableBytes > 0 ? (
            <div className="text-xs text-gray-500">{formatBytes(disk.reclaimableBytes)} reclaimable on engine</div>
          ) : null}
        </div>
      </div>
      <div className="mt-3 h-3 w-full overflow-hidden rounded-full bg-gray-100">
        <div className={`h-full rounded-full transition-all ${barColor}`} style={{ width: `${percent}%` }} />
      </div>
      {showDetails && (
        <div className="mt-3 grid gap-2 text-xs text-gray-600 sm:grid-cols-2 lg:grid-cols-4">
          <div>Images: {formatBytes(disk.dockerImagesBytes)}</div>
          <div>Volumes: {formatBytes(disk.dockerVolumesBytes)}</div>
          <div>Build cache: {formatBytes(disk.dockerBuildCacheBytes)}</div>
          <div>Containers: {formatBytes(disk.dockerContainersBytes)}</div>
        </div>
      )}
    </div>
  )
}
