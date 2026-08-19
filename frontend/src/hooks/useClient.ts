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

/** Uploads a base client archive (streamed) and installs it as the stack's base layer. */
export function useUploadBaseClient(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ file, onProgress }: { file: File; onProgress?: (percent: number) => void }) =>
      (await clientApi.uploadBase(stackId, file, onProgress)).data,
    onSuccess: (data) => {
      qc.setQueryData(baseInfoKey(stackId), data)
      qc.invalidateQueries({ queryKey: browseKey(stackId) })
    },
  })
}

/** Enqueues a configured-URL download of the base client archive. */
export function useDownloadBaseClient(stackId: string) {
  return useMutation({
    mutationFn: async () => (await clientApi.downloadBase(stackId)).data,
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

/** Uploads a single file into a folder of the base client (drag & drop), then refreshes listings. */
export function useUploadClientFile(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ dir, file }: { dir: string; file: File }) =>
      (await clientApi.uploadFile(stackId, dir, file)).data,
    onSuccess: (data) => {
      qc.setQueryData(baseInfoKey(stackId), data)
      qc.invalidateQueries({ queryKey: browseKey(stackId) })
    },
  })
}
