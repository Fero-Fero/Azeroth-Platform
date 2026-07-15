import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { addonApi } from '@/services/api'

const scopeKey = (stackId?: string) => stackId ?? '__global__'

export const addonKeys = {
  all: ['addons'] as const,
  list: (stackId?: string) => [...addonKeys.all, scopeKey(stackId)] as const,
  catalog: (stackId?: string) => [...addonKeys.all, 'catalog', scopeKey(stackId)] as const,
}

export function useAddons(stackId?: string) {
  return useQuery({
    queryKey: addonKeys.list(stackId),
    queryFn: async () => (await addonApi.list(stackId)).data,
  })
}

export function useUploadAddon(stackId?: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (file: File) => addonApi.upload(stackId, file),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: addonKeys.list(stackId) }),
  })
}

export function useDeleteAddon(stackId?: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => addonApi.remove(stackId, name),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: addonKeys.list(stackId) }),
  })
}

export function useAddonCatalog(stackId?: string) {
  return useQuery({
    queryKey: addonKeys.catalog(stackId),
    queryFn: async () => (await addonApi.catalog(stackId)).data,
  })
}

export function useInstallCatalogAddon(stackId?: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (addonId: string) => addonApi.install(stackId, addonId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: addonKeys.list(stackId) })
      queryClient.invalidateQueries({ queryKey: addonKeys.catalog(stackId) })
    },
  })
}
