import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { luaApi, revisionApi, serverConfigApi } from '@/services/api'

// ===== Lua scripts =====

export function useLuaScripts(stackId: string) {
  return useQuery({
    queryKey: ['lua', stackId],
    queryFn: async () => (await luaApi.list(stackId)).data,
  })
}

export function useLuaScript(stackId: string, path: string | null) {
  return useQuery({
    queryKey: ['lua', stackId, 'content', path],
    enabled: !!path,
    queryFn: async () => (await luaApi.read(stackId, path!)).data,
  })
}

export function useSaveLuaScript(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ path, content }: { path: string; content: string }) =>
      (await luaApi.save(stackId, path, content)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['lua', stackId] }),
  })
}

export function useUploadLuaScript(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ file, path }: { file: File; path?: string }) =>
      (await luaApi.upload(stackId, file, path)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['lua', stackId] }),
  })
}

export function useDeleteLuaScript(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (path: string) => (await luaApi.remove(stackId, path)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['lua', stackId] }),
  })
}

export function useApplyLua(stackId: string) {
  return useMutation({
    mutationFn: async () => (await luaApi.apply(stackId)).data,
  })
}

// ===== Server config files =====

export function useServerConfigs(stackId: string) {
  return useQuery({
    queryKey: ['server-config', stackId],
    queryFn: async () => (await serverConfigApi.list(stackId)).data,
  })
}

export function useServerConfig(stackId: string, path: string | null) {
  return useQuery({
    queryKey: ['server-config', stackId, 'content', path],
    enabled: !!path,
    queryFn: async () => (await serverConfigApi.read(stackId, path!)).data,
  })
}

export function useSaveServerConfig(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ path, content }: { path: string; content: string }) =>
      (await serverConfigApi.save(stackId, path, content)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['server-config', stackId] }),
  })
}

export function useApplyServerConfig(stackId: string) {
  return useMutation({
    mutationFn: async () => (await serverConfigApi.apply(stackId)).data,
  })
}

// ===== Revisions (DB + config snapshots) =====

export function useRevisions(stackId: string) {
  return useQuery({
    queryKey: ['revisions', stackId],
    queryFn: async () => (await revisionApi.list(stackId)).data,
  })
}

export function useCreateRevision(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async () => (await revisionApi.create(stackId)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['revisions', stackId] }),
  })
}

export function useRestoreRevision(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (revisionId: string) => (await revisionApi.restore(stackId, revisionId)).data,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['revisions', stackId] })
      qc.invalidateQueries({ queryKey: ['stacks'] })
    },
  })
}

export function useDeleteRevision(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (revisionId: string) => (await revisionApi.remove(stackId, revisionId)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['revisions', stackId] }),
  })
}
