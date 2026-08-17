import { usePatchOverview, useProgressionSyncStatus } from '@/hooks/usePatches'

export function useIpProgressionStatus(stackId: string, enabled = true) {
  const overview = usePatchOverview(enabled ? stackId : '')
  const sync = useProgressionSyncStatus(enabled ? stackId : '')

  return {
    bootstrapped: overview.data?.serverWideProgressionBootstrapped ?? false,
    syncCompleted:
      sync.data?.hasCompletedInitialSync === true || !!sync.data?.lastSyncAt,
    isLoading: overview.isLoading || sync.isLoading,
  }
}
