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
