import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { dockerApi, stackApi } from '@/services/api'
import { stackKeys } from '@/hooks/useStacks'

export const dockerKeys = {
  disk: ['docker', 'disk'] as const,
  engineOverview: ['docker', 'overview'] as const,
  managerFiles: (path: string) => ['docker', 'manager-files', path] as const,
  platformKeys: ['docker', 'platform-keys'] as const,
  overview: (stackId: string) => [...stackKeys.detail(stackId), 'docker'] as const,
  volumeAudit: (stackId: string) => [...stackKeys.detail(stackId), 'docker', 'volume-audit'] as const,
}

export function useDockerDiskUsage(enabled = true) {
  return useQuery({
    queryKey: dockerKeys.disk,
    queryFn: async () => (await dockerApi.getDiskUsage()).data,
    enabled,
    refetchInterval: 60_000,
  })
}

export function useStackDockerOverview(stackId: string, enabled = true) {
  return useQuery({
    queryKey: dockerKeys.overview(stackId),
    queryFn: async () => (await stackApi.getDockerOverview(stackId)).data,
    enabled: !!stackId && enabled,
    refetchInterval: 30_000,
  })
}

export function useDockerCleanup() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => dockerApi.cleanupUnused(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: dockerKeys.disk })
      queryClient.invalidateQueries({ queryKey: ['stacks'] })
      queryClient.invalidateQueries({ queryKey: ['stack'] })
    },
  })
}

export function useDeleteDockerBuildFiles(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => stackApi.deleteDockerBuildFiles(stackId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: dockerKeys.overview(stackId) })
      queryClient.invalidateQueries({ queryKey: dockerKeys.disk })
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
    },
  })
}

export function useDeleteDockerImage(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (imageId: string) => stackApi.deleteDockerImage(stackId, imageId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: dockerKeys.overview(stackId) })
      queryClient.invalidateQueries({ queryKey: dockerKeys.disk })
    },
  })
}

export function useDeleteDockerVolume(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (volumeName: string) => stackApi.deleteDockerVolume(stackId, volumeName),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: dockerKeys.overview(stackId) }),
  })
}

export function useDockerVolumeAudit(stackId: string) {
  return useQuery({
    queryKey: dockerKeys.volumeAudit(stackId),
    queryFn: async () => (await stackApi.getDockerVolumeAudit(stackId)).data,
    enabled: false,
  })
}

export function useDockerEngineOverview() {
  return useQuery({
    queryKey: dockerKeys.engineOverview,
    queryFn: async () => (await dockerApi.getOverview()).data,
    refetchInterval: 30_000,
  })
}

export function useDeleteEngineVolume() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (volumeName: string) => dockerApi.deleteEngineVolume(volumeName),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: dockerKeys.engineOverview })
      queryClient.invalidateQueries({ queryKey: dockerKeys.disk })
    },
  })
}

export function useDeleteEngineImage() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (imageId: string) => dockerApi.deleteEngineImage(imageId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: dockerKeys.engineOverview })
      queryClient.invalidateQueries({ queryKey: dockerKeys.disk })
    },
  })
}

export function useDockerVolumeAuditCleanup(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: import('@/types/docker.types').DockerVolumeCleanupRequestDto) =>
      stackApi.cleanupDockerVolumeAudit(stackId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: dockerKeys.volumeAudit(stackId) })
      queryClient.invalidateQueries({ queryKey: dockerKeys.overview(stackId) })
      queryClient.invalidateQueries({ queryKey: dockerKeys.disk })
    },
  })
}

export function useManagerFiles(path: string) {
  return useQuery({
    queryKey: dockerKeys.managerFiles(path),
    queryFn: async () => (await dockerApi.getManagerFiles(path || undefined)).data,
  })
}

export function usePlatformKeys() {
  return useQuery({
    queryKey: dockerKeys.platformKeys,
    queryFn: async () => (await dockerApi.getPlatformKeys()).data,
  })
}

export function useDeleteManagerFile() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (path: string) => dockerApi.deleteManagerFile(path),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['docker', 'manager-files'] })
      queryClient.invalidateQueries({ queryKey: dockerKeys.engineOverview })
    },
  })
}
