export interface DockerDiskUsageDto {
  totalBytes: number
  usedBytes: number
  availableBytes: number
  usedPercent: number
  isWarning: boolean
  dockerImagesBytes: number
  dockerBuildCacheBytes: number
  reclaimableBytes: number
  dockerImagesReclaimableBytes: number
  dockerBuildCacheReclaimableBytes: number
  dockerVolumesBytes: number
  dockerContainersBytes: number
  dockerVolumesReclaimableBytes: number
  dockerContainersReclaimableBytes: number
}

export interface DockerDiskUsageBreakdownDto {
  dockerImagesBytes: number
  dockerImagesCount: number
  activeImagesBytes: number
  activeImagesCount: number
  reclaimableImagesBytes: number
  reclaimableImagesCount: number
  dockerVolumesBytes: number
  dockerVolumesCount: number
  activeVolumesBytes: number
  activeVolumesCount: number
  dockerBuildCacheBytes: number
  dockerContainersBytes: number
  managedBuildCheckoutBytes: number
  managedBuildCheckoutCount: number
  orphanedBuildCheckoutBytes: number
  orphanedBuildCheckoutCount: number
  danglingLayerBytes: number
  danglingLayerCount: number
  reclaimableBytes: number
  activeImages: StackDockerImageDto[]
  activeVolumes: StackDockerVolumeDto[]
}

export interface DockerReclaimableBreakdownDto {
  buildCacheBytes: number
  danglingImageBytes: number
  danglingImageCount: number
  unusedTaggedImageBytes: number
  unusedTaggedImageCount: number
  obsoleteBuildDirBytes: number
  obsoleteBuildDirCount: number
  engineReclaimableBytes: number
  listedReclaimableBytes: number
}

export interface DockerObsoleteBuildDirDto {
  stackId: string
  path: string
  sizeBytes: number
}

export interface StackDockerBuildFilesDto {
  exists: boolean
  path: string
  sizeBytes: number
  isActive: boolean
  activeReason?: string | null
}

export interface StackDockerImageDto {
  id: string
  repository: string
  tag: string
  reference: string
  ownerStackId?: string | null
  sizeBytes: number
  createdAt?: string | null
  isActive: boolean
  activeReason?: string | null
}

export interface StackDockerVolumeDto {
  name: string
  sizeBytes?: number | null
  linkCount: number
  isActive: boolean
  activeReason?: string | null
}

export interface StackDockerOverviewDto {
  diskUsage?: DockerDiskUsageDto | null
  diskUsageBreakdown?: DockerDiskUsageBreakdownDto | null
  reclaimableBreakdown?: DockerReclaimableBreakdownDto | null
  buildFiles?: StackDockerBuildFilesDto | null
  images: StackDockerImageDto[]
  unusedImages: StackDockerImageDto[]
  danglingImages: StackDockerImageDto[]
  obsoleteBuildDirs: DockerObsoleteBuildDirDto[]
  volumes: StackDockerVolumeDto[]
  buildCacheBytes: number
  reclaimableBytes: number
  totalBytes: number
}

export interface DockerVolumeCleanupResultDto {
  success: boolean
  message: string
  freedBytes: number
  deletedVolumes: number
  deletedFiles: number
}

export interface DockerVolumeAuditDuplicateCopyDto {
  label: string
  managerPath: string
  managerBytes: number
  volumeName: string
  volumeBytes: number
  detail: string
}

export interface DockerVolumeAuditOrphanVolumeDto {
  volumeName: string
  inferredStackId?: string | null
  sizeBytes?: number | null
  linkCount: number
  isSafeToDelete: boolean
  reason: string
}

export interface DockerVolumeAuditStaleFileDto {
  volumeName: string
  relativePath: string
  sizeBytes: number
  reason: string
  isSafeToDelete: boolean
}

export interface DockerVolumeAuditDriftNoteDto {
  category: string
  detail: string
}

export interface DockerVolumeAuditDto {
  auditedAt: string
  duplicateCopies: DockerVolumeAuditDuplicateCopyDto[]
  orphanVolumes: DockerVolumeAuditOrphanVolumeDto[]
  staleOverlayFiles: DockerVolumeAuditStaleFileDto[]
  driftNotes: DockerVolumeAuditDriftNoteDto[]
  reclaimableBytes: number
  reclaimableItemCount: number
}

export interface DockerVolumeCleanupRequestDto {
  orphanVolumeNames: string[]
  staleOverlayPaths: string[]
}

export interface StackDockerDeleteResultDto {
  success: boolean
  message: string
  freedBytes: number
}

export interface DockerCleanupResultDto {
  success: boolean
  message: string
  freedBytes: number
  removedImages: number
  removedBuildDirs: number
}

export type DockerCleanupJobPhase = 'Running' | 'Completed' | 'Failed'
export type DockerCleanupJobAction = 'ReclaimDiskSpace' | 'CleanupOldBuilds'

export interface DockerCleanupJobStatus {
  jobId: string
  action: DockerCleanupJobAction
  phase: DockerCleanupJobPhase
  message: string
  error?: string | null
  success?: boolean | null
  startedAt: string
  finishedAt?: string | null
  estimatedReclaimableBytes: number
  freedBytes: number
  removedImages: number
  removedBuildDirs: number
  isRunning: boolean
}
