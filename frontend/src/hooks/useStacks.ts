import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { stackApi } from '@/services/api'

export const stackKeys = {
  all: ['stacks'] as const,
  lists: () => [...stackKeys.all, 'list'] as const,
  list: (filters: string) => [...stackKeys.lists(), { filters }] as const,
  details: () => [...stackKeys.all, 'detail'] as const,
  detail: (id: string) => [...stackKeys.details(), id] as const,
}

export function useStacks() {
  return useQuery({
    queryKey: stackKeys.lists(),
    queryFn: async () => {
      const response = await stackApi.list()
      return response.data
    },
    // Poll while any stack is mid-transition (e.g. a background start/restart job) so the overview
    // reflects progress and the completed state without a manual refresh.
    refetchInterval: (query) => {
      const transitional = new Set(['Starting', 'Building', 'Initializing', 'Degraded'])
      const anyTransitioning = query.state.data?.some((s) => transitional.has(s.status))
      return anyTransitioning ? 5000 : false
    },
  })
}

export function useCreateStack() {
  const queryClient = useQueryClient()
  
  return useMutation({
    mutationFn: stackApi.create,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    },
  })
}
