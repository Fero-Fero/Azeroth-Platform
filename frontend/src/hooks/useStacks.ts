import { keepPreviousData, useMutation, useQuery, useQueryClient, type Query } from '@tanstack/react-query'
import { useEffect } from 'react'
import { stackApi } from '@/services/api'
import type { StackDetailsDto } from '@/types/stack.types'

export const stackKeys = {
  all: ['stacks'] as const,
  lists: () => [...stackKeys.all, 'list'] as const,
  list: (filters: string) => [...stackKeys.lists(), { filters }] as const,
  details: () => [...stackKeys.all, 'detail'] as const,
  detail: (id: string) => [...stackKeys.details(), id] as const,
}

let listProbeStartedThisSession = false

function stackFromListCache(
  queryClient: ReturnType<typeof useQueryClient>,
  stackId: string,
): StackDetailsDto | undefined {
  const list = queryClient.getQueryData<StackDetailsDto[]>(stackKeys.lists())
  return list?.find((stack) => stack.stackId === stackId)
}

export function useStacks() {
  const queryClient = useQueryClient()

  const query = useQuery({
    queryKey: stackKeys.lists(),
    queryFn: async () => {
      const response = await stackApi.list()
      return response.data
    },
    staleTime: Infinity,
    gcTime: 30 * 60_000,
    placeholderData: keepPreviousData,
    refetchOnWindowFocus: false,
    refetchInterval: (queryState: Query<StackDetailsDto[], Error>) => {
      const transitional = new Set(['Starting', 'Building', 'Initializing', 'Degraded'])
      const anyTransitioning = queryState.state.data?.some((s) => transitional.has(s.status))
      return anyTransitioning ? 5000 : false
    },
  })

  const probeAll = useMutation({
    mutationFn: () => stackApi.probeAllStatus().then((res) => res.data),
    onSuccess: (data) => {
      queryClient.setQueryData(stackKeys.lists(), data)
    },
  })

  const { mutate: runProbeAll, isPending: isProbing } = probeAll

  useEffect(() => {
    if (!query.isSuccess || listProbeStartedThisSession || !query.data?.length) {
      return
    }

    listProbeStartedThisSession = true
    runProbeAll()
  }, [query.isSuccess, query.data?.length, runProbeAll])

  return {
    ...query,
    probeAll,
    isProbing,
  }
}

export function useStackDetail(
  stackId: string,
  options?: {
    refetchInterval?: number | false | ((query: Query<StackDetailsDto, Error>) => number | false)
    enabled?: boolean
  },
) {
  const queryClient = useQueryClient()

  const detailQuery = useQuery({
    queryKey: stackKeys.detail(stackId),
    queryFn: ({ signal }) => stackApi.get(stackId, signal).then((res) => res.data),
    enabled: (options?.enabled ?? true) && !!stackId,
    staleTime: 15_000,
    gcTime: 10 * 60_000,
    placeholderData: (previousData) =>
      previousData ?? stackFromListCache(queryClient, stackId),
    refetchInterval: options?.refetchInterval,
  })

  useEffect(() => {
    if (!detailQuery.data) return
    queryClient.setQueryData<StackDetailsDto[]>(stackKeys.lists(), (current) => {
      if (!current) return current
      return current.map((stack) =>
        stack.stackId === stackId ? detailQuery.data! : stack,
      )
    })
  }, [detailQuery.data, queryClient, stackId])

  return detailQuery
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
