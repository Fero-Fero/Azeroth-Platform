import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { moduleApi, serverTypeApi } from '@/services/api'
import type { SaveModuleRequest, ServerType } from '@/types/stack.types'

/** Server-type catalog (Standard, Playerbots, ...) available in the stack wizard. */
export function useServerTypes() {
  return useQuery({
    queryKey: ['server-types'],
    staleTime: 5 * 60 * 1000,
    queryFn: async () => (await serverTypeApi.list()).data,
  })
}

/**
 * Branches of a custom-fork repository, resolved via the backend (git ls-remote). Pass the (already
 * debounced) repository URL; the query only runs when `enabled` is true and a URL is present.
 */
export function useRepositoryBranches(repositoryUrl: string, enabled: boolean) {
  return useQuery({
    queryKey: ['repository-branches', repositoryUrl],
    enabled: enabled && repositoryUrl.trim().length > 0,
    staleTime: 60 * 1000,
    retry: false,
    queryFn: async () => (await serverTypeApi.branches(repositoryUrl.trim())).data,
  })
}

export function useModules(serverType?: ServerType) {
  return useQuery({
    queryKey: ['modules', serverType],
    queryFn: async () => {
      const response = await moduleApi.list(serverType)
      return response.data
    },
  })
}

/** Per-service environment-variable templates rendered by the stack wizard. */
export function useServiceEnvTemplates() {
  return useQuery({
    queryKey: ['service-env-templates'],
    staleTime: Infinity,
    queryFn: async () => {
      const response = await moduleApi.serviceEnvTemplates()
      return response.data
    },
  })
}

/** Full catalog (built-in + custom) for administration. */
export function useModuleCatalog() {
  return useQuery({
    queryKey: ['module-catalog'],
    queryFn: async () => {
      const response = await moduleApi.catalog()
      return response.data
    },
  })
}

function useInvalidateModules() {
  const queryClient = useQueryClient()
  return () => {
    queryClient.invalidateQueries({ queryKey: ['module-catalog'] })
    queryClient.invalidateQueries({ queryKey: ['modules'] })
  }
}

export function useCreateModule() {
  const invalidate = useInvalidateModules()
  return useMutation({
    mutationFn: async (request: SaveModuleRequest) => (await moduleApi.create(request)).data,
    onSuccess: invalidate,
  })
}

export function useUploadModulePackage() {
  const invalidate = useInvalidateModules()
  return useMutation({
    mutationFn: async ({
      fields,
      file,
    }: {
      fields: { id: string; name: string; description: string }
      file: File
    }) => (await moduleApi.uploadPackage(fields, file)).data,
    onSuccess: invalidate,
  })
}

export function useReplaceModulePackage() {
  const invalidate = useInvalidateModules()
  return useMutation({
    mutationFn: async ({ moduleId, file }: { moduleId: string; file: File }) =>
      (await moduleApi.replacePackage(moduleId, file)).data,
    onSuccess: invalidate,
  })
}

export function useModuleReadme(moduleId: string | null) {
  return useQuery({
    queryKey: ['module-readme', moduleId],
    enabled: !!moduleId,
    queryFn: async () => (await moduleApi.readme(moduleId!)).data,
  })
}

export function useUpdateModule() {
  const invalidate = useInvalidateModules()
  return useMutation({
    mutationFn: async ({ moduleId, request }: { moduleId: string; request: SaveModuleRequest }) =>
      (await moduleApi.update(moduleId, request)).data,
    onSuccess: invalidate,
  })
}

export function useDeleteModule() {
  const invalidate = useInvalidateModules()
  return useMutation({
    mutationFn: async (moduleId: string) => (await moduleApi.remove(moduleId)).data,
    onSuccess: invalidate,
  })
}
