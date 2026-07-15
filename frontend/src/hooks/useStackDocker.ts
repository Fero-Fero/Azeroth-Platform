import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { dockerApi, stackApi } from '@/services/api'
import { stackKeys } from '@/hooks/useStacks'

export const dockerKeys = {
  disk: ['docker', 'disk'] as const,
  overview: (stackId: string) => [...stackKeys.detail(stackId), 'docker'] as const,
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
