import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { launcherApi } from '@/services/api'
import type {
  LauncherDistributionConfigDto,
  LauncherNewsItemDto,
  LauncherProfileConfigDto,
  LauncherVersionPart,
} from '@/types/launcher.types'

// ===== Global launcher config =====

export function useLauncherConfig() {
  return useQuery({
    queryKey: ['launcher-config'],
    queryFn: async () => (await launcherApi.getConfig()).data,
  })
}

export function useLauncherTemplates() {
  return useQuery({
    queryKey: ['launcher-templates'],
    queryFn: async () => (await launcherApi.getTemplates()).data,
    staleTime: 5 * 60 * 1000,
  })
}

export function useSaveLauncherConfig() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (config: LauncherDistributionConfigDto) =>
      (await launcherApi.saveConfig(config)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-config'] }),
  })
}

export function useUploadLauncherAsset() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ kind, file }: { kind: 'background' | 'logo' | 'icon'; file: File }) =>
      (await launcherApi.uploadAsset(kind, file)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-config'] }),
  })
}

// ===== Build pipeline =====

export function useLauncherBuildStatus(poll: boolean) {
  return useQuery({
    queryKey: ['launcher-build-status'],
    queryFn: async () => (await launcherApi.buildStatus()).data,
    refetchInterval: poll ? 1500 : false,
  })
}

export function useStartLauncherBuild() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (input: LauncherVersionPart | { part: LauncherVersionPart }) => {
      const part = typeof input === 'object' ? input.part : input
      return (await launcherApi.build(part)).data
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-build-status'] }),
  })
}

/**
 * Pings every client-enabled stack for the launcher version it currently serves and compares it to the
 * manager's most recent build. Disabled by default (opt-in via `enabled`) since it makes a live probe of
 * every stack; the launcher page enables it when the admin clicks "Check stacks".
 */
export function useLauncherStackVersions(enabled: boolean) {
  return useQuery({
    queryKey: ['launcher-stack-versions'],
    queryFn: async () => (await launcherApi.stackVersions()).data,
    enabled,
  })
}

/** Re-pushes the current launcher build to a single stack that is stale or missed the last build. */
export function useResendLauncherToStack() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (stackId: string) => (await launcherApi.resendToStack(stackId)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-stack-versions'] }),
  })
}

// ===== Per-stack profile =====

export function useLauncherProfile(stackId: string, enabled = true) {
  return useQuery({
    queryKey: ['launcher-profile', stackId],
    queryFn: async () => (await launcherApi.getProfile(stackId)).data,
    enabled: enabled && stackId.trim().length > 0,
  })
}

export function useSaveLauncherProfile(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (profile: LauncherProfileConfigDto) =>
      (await launcherApi.saveProfile(stackId, profile)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-profile', stackId] }),
  })
}

/**
 * Rescans a stack's client distribution so realmlist/config changes propagate to players and the
 * manifest version bumps. Invalidates the profile so the effective realmlist re-reads.
 */
export function useRescanStackClient(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async () => (await launcherApi.rescanStackClient(stackId)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-profile', stackId] }),
  })
}

/**
 * Forces every launcher on this stack to full-verify all client files on its next check (by bumping
 * the manifest verify token). Use when a same-size edit wouldn't be caught by the quick size check.
 */
export function useForceVerifyStackClient(stackId: string) {
  return useMutation({
    mutationFn: async () => (await launcherApi.forceVerifyStackClient(stackId)).data,
  })
}

/** Re-hash every distributable file, rebuild the manifest, and queue a full launcher sync. */
export function useRebuildStackClientManifest(stackId: string) {
  return useMutation({
    mutationFn: async () => (await launcherApi.rebuildStackClientManifest(stackId)).data,
  })
}

const stackConfigTemplateKey = (stackId: string) => ['stack-config-template', stackId]

/** Reads a stack's editable WTF/Config.wtf settings template (with {{HOST}}/{{PORT}} placeholders). */
export function useStackConfigTemplate(stackId: string) {
  return useQuery({
    queryKey: stackConfigTemplateKey(stackId),
    queryFn: async () => (await launcherApi.getStackConfigTemplate(stackId)).data.content,
  })
}

/** Saves a stack's WTF/Config.wtf settings template. */
export function useSaveStackConfigTemplate(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (content: string) => {
      await launcherApi.saveStackConfigTemplate(stackId, content)
      return content
    },
    onSuccess: (content) => qc.setQueryData(stackConfigTemplateKey(stackId), content),
  })
}

export function useUploadProfileAsset(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ kind, file }: { kind: 'background' | 'logo'; file: File }) =>
      (await launcherApi.uploadProfileAsset(stackId, kind, file)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-profile', stackId] }),
  })
}

export function useDeleteProfileAsset(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (kind: 'background' | 'logo') =>
      (await launcherApi.deleteProfileAsset(stackId, kind)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-profile', stackId] }),
  })
}

// ===== News (global + per-stack) =====

export function useGlobalNews() {
  return useQuery({
    queryKey: ['launcher-news', 'global'],
    queryFn: async () => (await launcherApi.getGlobalNews()).data,
  })
}

export function useSaveGlobalNews() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (items: LauncherNewsItemDto[]) => (await launcherApi.saveGlobalNews(items)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-news', 'global'] }),
  })
}

export function useUploadGlobalNewsImage() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ itemId, file }: { itemId: string; file: File }) =>
      (await launcherApi.uploadGlobalNewsImage(itemId, file)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-news', 'global'] }),
  })
}

export function useStackNews(stackId: string) {
  return useQuery({
    queryKey: ['launcher-news', 'stack', stackId],
    queryFn: async () => (await launcherApi.getStackNews(stackId)).data,
  })
}

export function useSaveStackNews(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (items: LauncherNewsItemDto[]) => (await launcherApi.saveStackNews(stackId, items)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-news', 'stack', stackId] }),
  })
}

export function useUploadStackNewsImage(stackId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ itemId, file }: { itemId: string; file: File }) =>
      (await launcherApi.uploadStackNewsImage(stackId, itemId, file)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['launcher-news', 'stack', stackId] }),
  })
}
