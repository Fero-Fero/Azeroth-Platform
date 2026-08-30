import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { realmApi } from '@/services/realmApi'
import type { CreateRealmRequest, UpdateRealmRequest } from '@/types/realm.types'

export const realmKeys = {
  all: ['realms'] as const,
  list: (stackId: string) => [...realmKeys.all, 'list', stackId] as const,
}

export function useRealms(stackId: string) {
  return useQuery({
    queryKey: realmKeys.list(stackId),
    queryFn: async () => {
      const response = await realmApi.list(stackId)
      return response.data
    },
    enabled: !!stackId,
  })
}

export function useCreateRealm(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: CreateRealmRequest) => realmApi.create(stackId, request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: realmKeys.list(stackId) }),
  })
}

export function useUpdateRealm(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ realmId, request }: { realmId: number; request: UpdateRealmRequest }) =>
      realmApi.update(stackId, realmId, request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: realmKeys.list(stackId) }),
  })
}

export function useSetRealmAddress(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (host: string) => realmApi.setAddress(stackId, host),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: realmKeys.list(stackId) })
      queryClient.invalidateQueries({ queryKey: ['launcher-profile', stackId] })
    },
  })
}
