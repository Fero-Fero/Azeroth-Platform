import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { patchApi } from '@/services/api'
import { stackKeys } from '@/hooks/useStacks'
import type { CreatePatchRequest, ImportPatchCollectionMode } from '@/types/patch.types'
export const patchKeys = {
  all: ['patches'] as const,
  overview: (stackId: string) => [...patchKeys.all, 'overview', stackId] as const,
  detail: (stackId: string, patchKey: string) =>
    [...patchKeys.all, 'detail', stackId, patchKey] as const,
  configOverridesPreview: (stackId: string, patchKey: string) =>
    [...patchKeys.all, 'config-overrides-preview', stackId, patchKey] as const,
  applyStatus: (stackId: string) => [...patchKeys.all, 'apply-status', stackId] as const,
  browse: (stackId: string) => [...patchKeys.all, 'browse', stackId] as const,
  publishedMpqs: (stackId: string) => [...patchKeys.all, 'published-mpqs', stackId] as const,
}

export function usePatchOverview(stackId: string) {
  return useQuery({
    queryKey: patchKeys.overview(stackId),
    queryFn: async () => (await patchApi.overview(stackId)).data,
    enabled: !!stackId,
  })
}

/**
 * Polls the background apply run status. While a run is in progress it refetches on a short interval
 * so the UI can stream the live phase and trace log; idle it stays quiet.
 */
export function useApplyStatus(stackId: string, active: boolean) {
  return useQuery({
    queryKey: patchKeys.applyStatus(stackId),
    queryFn: async () => (await patchApi.applyStatus(stackId)).data,
    enabled: !!stackId,
    refetchInterval: active ? 1500 : false,
  })
}

export function usePatchDetail(stackId: string, patchKey: string | null) {
  return useQuery({
    queryKey: patchKeys.detail(stackId, patchKey ?? ''),
    queryFn: async () => (await patchApi.detail(stackId, patchKey!)).data,
    enabled: !!stackId && !!patchKey,
  })
}

export function usePatchConfigOverridesPreview(
  stackId: string,
  patchKey: string | null,
  enabled: boolean
) {
  return useQuery({
    queryKey: patchKeys.configOverridesPreview(stackId, patchKey ?? ''),
    queryFn: async () => (await patchApi.configOverridesPreview(stackId, patchKey!)).data,
    enabled: enabled && !!stackId && !!patchKey,
  })
}

export function usePatchFilesBrowse(stackId: string, path: string, enabled = true) {
  return useQuery({
    queryKey: [...patchKeys.browse(stackId), path],
    queryFn: async () => (await patchApi.browseFiles(stackId, path)).data,
    enabled: enabled && !!stackId,
  })
}

function useInvalidatePatches(stackId: string) {
  const queryClient = useQueryClient()
  return () => {
    queryClient.invalidateQueries({ queryKey: patchKeys.all })
    queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
  }
}

/**
 * Triggers a browser download of a run's trace log. Fetched via the API client (bearer auth) as a
 * blob, so it works with the token-based auth that a plain anchor link would miss.
 */
export async function downloadApplyLog(stackId: string, runId?: string | null) {
  const res = await patchApi.downloadApplyLog(stackId, runId)
  const url = window.URL.createObjectURL(res.data)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = `apply-${runId ?? 'latest'}.log`
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  window.URL.revokeObjectURL(url)
}

export async function downloadPatchTemplate(stackId: string) {
  const res = await patchApi.downloadPatchTemplate(stackId)
  const url = window.URL.createObjectURL(res.data)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = 'patch-template.zip'
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  window.URL.revokeObjectURL(url)
}

export function useCreatePatch(stackId: string) {
  const invalidate = useInvalidatePatches(stackId)
  return useMutation({
    mutationFn: (request: CreatePatchRequest) => patchApi.create(stackId, request),
    onSuccess: invalidate,
  })
}

export function useImportPatchCollection(stackId: string) {
  const invalidate = useInvalidatePatches(stackId)
  return useMutation({
    mutationFn: ({
      file,
      mode,
      onProgress,
    }: {
      file: File
      mode: ImportPatchCollectionMode
      onProgress?: (percent: number) => void
    }) => patchApi.importCollection(stackId, file, mode, onProgress),
    onSuccess: invalidate,
  })
}

export function useDeletePatchEntry(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (path: string) => patchApi.deleteEntry(stackId, path),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: patchKeys.all })
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
    },
  })
}

export function useDropAllPatches(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => patchApi.dropAllPatches(stackId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: patchKeys.all })
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
    },
  })
}

export function useInitBaseline(stackId: string) {
  const invalidate = useInvalidatePatches(stackId)
  return useMutation({
    mutationFn: () => patchApi.initBaseline(stackId),
    onSuccess: invalidate,
  })
}

export function useApplyPatch(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (patchKey: string) => patchApi.apply(stackId, patchKey),
    onSuccess: (res) => {
      queryClient.setQueryData(patchKeys.applyStatus(stackId), res.data)
      queryClient.invalidateQueries({ queryKey: patchKeys.overview(stackId) })
    },
  })
}

export function useReapplyAllPatches(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => patchApi.reapplyAll(stackId),
    onSuccess: (res) => {
      queryClient.setQueryData(patchKeys.applyStatus(stackId), res.data)
      queryClient.invalidateQueries({ queryKey: patchKeys.overview(stackId) })
    },
  })
}

export function useUploadPatchFiles(stackId: string) {
  const invalidate = useInvalidatePatches(stackId)
  return useMutation({
    mutationFn: ({
      patchKey,
      category,
      files,
      description,
    }: {
      patchKey: string
      category: string
      files: FileList | File[]
      description?: string
    }) => patchApi.upload(stackId, patchKey, category, files, description),
    onSuccess: invalidate,
  })
}

export function useUploadContainerFiles(stackId: string) {
  const invalidate = useInvalidatePatches(stackId)
  return useMutation({
    mutationFn: ({
      patchKey,
      category,
      items,
    }: {
      patchKey: string
      category: string
      items: { file: File; path: string }[]
    }) => patchApi.uploadContainer(stackId, patchKey, category, items),
    onSuccess: invalidate,
  })
}

export function useDeletePatchFile(stackId: string) {
  const invalidate = useInvalidatePatches(stackId)
  return useMutation({
    mutationFn: ({ patchKey, category, fileName }: { patchKey: string; category: string; fileName: string }) =>
      patchApi.deleteFile(stackId, patchKey, category, fileName),
    onSuccess: invalidate,
  })
}

export function usePublishedMpqs(stackId: string, enabled = true) {
  return useQuery({
    queryKey: patchKeys.publishedMpqs(stackId),
    queryFn: async () => (await patchApi.publishedMpqs(stackId)).data,
    enabled: !!stackId && enabled,
  })
}

export function useSetMpqRemovals(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ patchKey, fileNames }: { patchKey: string; fileNames: string[] }) =>
      patchApi.setMpqRemovals(stackId, patchKey, fileNames),
    onSuccess: (_res, { patchKey }) => {
      queryClient.invalidateQueries({ queryKey: patchKeys.detail(stackId, patchKey) })
      queryClient.invalidateQueries({ queryKey: patchKeys.publishedMpqs(stackId) })
    },
  })
}

export function useSaveDbcFile(stackId: string) {
  const invalidate = useInvalidatePatches(stackId)
  return useMutation({
    mutationFn: ({ patchKey, fileName, content }: { patchKey: string; fileName: string; content: string }) =>
      patchApi.saveDbc(stackId, patchKey, fileName, content),
    onSuccess: invalidate,
  })
}

export function useSavePatchDescription(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ patchKey, content }: { patchKey: string; content: string }) =>
      patchApi.saveDescription(stackId, patchKey, content),
    onSuccess: (res, { patchKey }) => {
      queryClient.setQueryData(patchKeys.detail(stackId, patchKey), res.data)
      queryClient.invalidateQueries({ queryKey: patchKeys.overview(stackId) })
    },
  })
}

export function useBootstrapIndividualProgression(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => patchApi.bootstrapIndividualProgression(stackId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: patchKeys.all })
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
    },
  })
}

export function useValidateIndividualProgressionPatches(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => patchApi.validatePatches(stackId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: patchKeys.all })
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
    },
  })
}

// ===== Progression Sync Hooks =====

export function useProgressionSyncStatus(stackId: string, poll = false) {
  return useQuery({
    queryKey: [...patchKeys.all, 'progression-sync-status', stackId] as const,
    queryFn: async () => (await patchApi.progressionSyncStatus(stackId)).data,
    enabled: !!stackId,
    refetchInterval: poll ? 1000 : false,
  })
}

export function useRunProgressionSync(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => patchApi.runProgressionSync(stackId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: patchKeys.all })
    },
  })
}

export function useResolveProgressionOptionalFiles(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (decisions: Record<string, boolean>) =>
      patchApi.resolveProgressionOptionalFiles(stackId, decisions),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: patchKeys.all })
    },
  })
}

export function useProgressionIgnoredFiles(stackId: string, enabled = true) {
  return useQuery({
    queryKey: [...patchKeys.all, 'progression-ignored-files', stackId] as const,
    queryFn: async () => (await patchApi.getProgressionIgnoredFiles(stackId)).data,
    enabled: !!stackId && enabled,
  })
}

export function useRepromptProgressionIgnoredFile(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (source: string) =>
      patchApi.repromptProgressionIgnoredFile(stackId, source),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: patchKeys.all })
    },
  })
}
