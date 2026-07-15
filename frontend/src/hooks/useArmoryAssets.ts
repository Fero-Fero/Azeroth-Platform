import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { armoryAssetsApi } from '@/services/api'

export const armoryAssetsInfoKey = (stackId: string) => ['armory-assets', stackId, 'info']
export const armoryStylingKey = (stackId: string) => ['armory-assets', stackId, 'styling']
export const armoryStylingDefaultsKey = (stackId: string) => ['armory-assets', stackId, 'styling-defaults']
/** Unsaved styling draft shared between Styling and Layout tabs for live preview. */
export const armoryStylingPreviewKey = (stackId: string) => ['armory-assets', stackId, 'styling-preview']
export const armoryLayoutKey = (stackId: string) => ['armory-assets', stackId, 'layout']
const infoKey = armoryAssetsInfoKey
const browseKey = (stackId: string) => ['armory-assets', stackId, 'browse']

/** Current uploaded armory asset bundle summary for a stack (data + static). */
export function useArmoryAssetsInfo(stackId: string) {
  return useQuery({
    queryKey: infoKey(stackId),
    queryFn: async () => (await armoryAssetsApi.getInfo(stackId)).data,
    enabled: !!stackId,
  })
}

/** Default palettes for each styling template, fetched from the backend (single source of truth). */
export function useArmoryStylingDefaults(stackId: string) {
  return useQuery({
    queryKey: armoryStylingDefaultsKey(stackId),
    queryFn: async () => (await armoryAssetsApi.getStylingDefaults(stackId)).data,
    enabled: !!stackId,
    staleTime: Infinity,
  })
}

/** Live styling draft for cross-tab layout preview (updated by ArmoryStylingTab). */
export function useArmoryStylingPreview(stackId: string) {
  const qc = useQueryClient()
  return useQuery({
    queryKey: armoryStylingPreviewKey(stackId),
    queryFn: () => qc.getQueryData<import('@/types/armory.types').ArmoryStylingDto>(armoryStylingPreviewKey(stackId)),
    enabled: !!stackId,
    staleTime: 0,
  })
}

/** Current generated armory styling settings for a stack. */
export function useArmoryStyling(stackId: string) {
  return useQuery({
    queryKey: armoryStylingKey(stackId),
    queryFn: async () => (await armoryAssetsApi.getStyling(stackId)).data,
    enabled: !!stackId,
  })
}

/** Current armory homepage layout for a stack. */
export function useArmoryLayout(stackId: string) {
  return useQuery({
    queryKey: armoryLayoutKey(stackId),
    queryFn: async () => (await armoryAssetsApi.getLayout(stackId)).data,
    enabled: !!stackId,
  })
}

/** Saves armory layout settings and marks the armory image for rebuild. */
export function useSaveArmoryLayout(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (layout: import('@/types/armory.types').ArmoryLayoutDto) =>
      (await armoryAssetsApi.saveLayout(stackId, layout)).data,
    onSuccess: (data) => {
      qc.setQueryData(armoryLayoutKey(stackId), data)
      qc.invalidateQueries({ queryKey: infoKey(stackId) })
    },
  })
}

/** Saves armory styling settings and marks the armory image for rebuild. */
export function useSaveArmoryStyling(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (styling: import('@/types/armory.types').ArmoryStylingDto) =>
      (await armoryAssetsApi.saveStyling(stackId, styling)).data,
    onSuccess: (data) => {
      qc.setQueryData(armoryStylingKey(stackId), data)
      qc.invalidateQueries({ queryKey: infoKey(stackId) })
    },
  })
}

/** Uploads a wallpaper for the generated armory theme and marks the armory image for rebuild. */
export function useUploadArmoryWallpaper(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ file, onProgress }: { file: File; onProgress?: (percent: number) => void }) =>
      (await armoryAssetsApi.uploadWallpaper(stackId, file, onProgress)).data,
    onSuccess: (data) => {
      qc.setQueryData(armoryStylingKey(stackId), data)
      qc.invalidateQueries({ queryKey: infoKey(stackId) })
    },
  })
}

/** Uploads a model-viewer bundle (armory.data.zip / armory.textures.zip) for a stack. */
export function useUploadArmoryData(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ file, onProgress }: { file: File; onProgress?: (percent: number) => void }) =>
      (await armoryAssetsApi.uploadData(stackId, file, onProgress)).data,
    onSuccess: (data) => {
      qc.setQueryData(infoKey(stackId), data)
      qc.invalidateQueries({ queryKey: browseKey(stackId) })
    },
  })
}

/** Lists one directory level of the stack's uploaded model-viewer dataset (for the file browser). */
export function useArmoryDataBrowse(stackId: string, path: string, enabled = true) {
  return useQuery({
    queryKey: [...browseKey(stackId), path],
    queryFn: async () => (await armoryAssetsApi.browseData(stackId, path)).data,
    enabled: enabled && !!stackId,
  })
}

/** Uploads the static web-asset bundle (armory.static.zip) for a stack. */
export function useUploadArmoryStatic(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ file, onProgress }: { file: File; onProgress?: (percent: number) => void }) =>
      (await armoryAssetsApi.uploadStatic(stackId, file, onProgress)).data,
    onSuccess: (data) => qc.setQueryData(infoKey(stackId), data),
  })
}

/** Deletes uploaded static web assets while preserving model-viewer data and generated styling assets. */
export function useDeleteArmoryStatic(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async () => (await armoryAssetsApi.deleteStatic(stackId)).data,
    onSuccess: (data) => qc.setQueryData(infoKey(stackId), data),
  })
}

/**
 * Enqueues a detached background job that rebuilds the stack's armory image (baking uploaded static
 * assets) and restarts the armory. Returns the initial job status; track progress via useArmoryJob.
 */
export function useRebuildArmoryImage(stackId: string) {
  return useMutation({
    mutationFn: async () => (await armoryAssetsApi.rebuildImage(stackId)).data,
  })
}

/**
 * Enqueues a detached background job that extracts the stack's server DBCs, converts them for the
 * armory, bakes them into the image and restarts it. Track progress via useArmoryJob.
 */
export function useSyncArmoryDbcs(stackId: string) {
  return useMutation({
    mutationFn: async () => (await armoryAssetsApi.syncDbcs(stackId)).data,
  })
}

/** Deletes a file or folder from the stack's uploaded model-viewer dataset, then refreshes listings. */
export function useDeleteArmoryData(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (path: string) => (await armoryAssetsApi.deleteData(stackId, path)).data,
    onSuccess: (data) => {
      qc.setQueryData(infoKey(stackId), data)
      qc.invalidateQueries({ queryKey: browseKey(stackId) })
    },
  })
}

/** Uploads a single file into a folder of the model-viewer dataset (drag & drop), then refreshes listings. */
export function useUploadArmoryDataFile(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ dir, file }: { dir: string; file: File }) =>
      (await armoryAssetsApi.uploadDataFile(stackId, dir, file)).data,
    onSuccess: (data) => {
      qc.setQueryData(infoKey(stackId), data)
      qc.invalidateQueries({ queryKey: browseKey(stackId) })
    },
  })
}
