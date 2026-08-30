import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { clientApi } from '@/services/api'

const baseInfoKey = (stackId: string) => ['client', stackId, 'base-info']
const browseKey = (stackId: string) => ['client', stackId, 'browse']

/** Current base client summary for a stack (existence, size, sanity checks). */
export function useClientBaseInfo(stackId: string, enabled = true) {
  return useQuery({
    queryKey: baseInfoKey(stackId),
    queryFn: async () => (await clientApi.getBaseInfo(stackId)).data,
    enabled: enabled && !!stackId,
    refetchOnMount: 'always',
  })
}

/** Uploads a base client archive and queues extract + volume seed as a background job. */
export function useUploadBaseClient(stackId: string) {
  return useMutation({
    mutationFn: async ({ file, onProgress }: { file: File; onProgress?: (percent: number) => void }) =>
      (await clientApi.uploadBase(stackId, file, onProgress)).data,
  })
}

/** Enqueues a download of the base client from a direct URL. */
export function useDownloadBaseClient(stackId: string) {
  return useMutation({
    mutationFn: async (url: string) => (await clientApi.downloadBase(stackId, url)).data,
  })
}

/** Re-seeds the stack's base client volume from its base directory. */
export function useRescanBaseClient(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async () => (await clientApi.rescanBase(stackId)).data,
    onSuccess: (data) => {
      qc.setQueryData(baseInfoKey(stackId), data)
      qc.invalidateQueries({ queryKey: browseKey(stackId) })
    },
  })
}

/**
 * Enqueues a purge of every client file the stack serves. Destructive and unrecoverable: recovery is
 * to re-upload a base client and reapply the stack's patches.
 */
export function usePurgeClientContent(stackId: string) {
  return useMutation({
    mutationFn: async () => (await clientApi.purge(stackId)).data,
  })
}

/** Lists one directory level of the stack's base client tree (for the file browser). */
export function useClientBrowse(stackId: string, path: string, enabled = true) {
  return useQuery({
    queryKey: [...browseKey(stackId), path],
    queryFn: async () => (await clientApi.browse(stackId, path)).data,
    enabled: enabled && !!stackId,
  })
}

/** Deletes a file or folder from the stack's base client, then refreshes info + browse listings. */
export function useDeleteClientEntry(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (path: string) => (await clientApi.deleteEntry(stackId, path)).data,
    onSuccess: (data) => {
      qc.setQueryData(baseInfoKey(stackId), data)
      qc.invalidateQueries({ queryKey: browseKey(stackId) })
    },
  })
}

/**
 * Uploads a single file into a folder of the client (drag & drop), overwriting any file already at
 * that path, then refreshes listings. The server routes the file to the base or overlay layer and
 * rebuilds the launcher manifest, so the change reaches players without any further action.
 */
export function useUploadClientFile(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({
      dir,
      file,
      onProgress,
    }: {
      dir: string
      file: File
      onProgress?: (percent: number) => void
    }) => (await clientApi.uploadFile(stackId, dir, file, onProgress)).data,
    onSuccess: (data) => {
      qc.setQueryData(baseInfoKey(stackId), data)
      qc.invalidateQueries({ queryKey: browseKey(stackId) })
    },
  })
}
