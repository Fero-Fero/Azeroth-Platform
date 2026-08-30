import { useEffect } from 'react'
import { useArmoryAssetsInfo } from '@/hooks/useArmoryAssets'
import { useClientBaseInfo } from '@/hooks/useClient'
import {
  useAutoPrepareModuleExtras,
  useDbcStoreStatus,
  useModuleExtraDataChoices,
  useModuleExtraDataJob,
  useModuleExtraDataStackStatus,
} from '@/hooks/useModuleExtraData'
import { MODULE_IDS } from '@/setup/constants'
import { useSetupProgressStore } from '@/setup/progress/setupProgressStore'
import { isDatabaseRunning, isStackServiceRunning } from '@/setup/stackServices'
import { useIpProgressionStatus } from '@/setup/steps/modules/mod-individual-progression/useIpProgressionStatus'
import { usePlayerbotsConf } from '@/setup/steps/modules/mod-playerbots/usePlayerbotsConf'
import type { SetupStepContext, SetupTabId } from '@/setup/types'
import type { StackDetailsDto } from '@/types/stack.types'

export function useSetupStepContext(
  stack: StackDetailsDto,
  onSelectTab: (tabId: SetupTabId) => void,
): SetupStepContext {
  const patchesHref = `/stacks/${stack.stackId}?tab=patches`
  const hasIp = stack.configuration.moduleIds?.includes(MODULE_IDS.individualProgression) ?? false

  const clientBase = useClientBaseInfo(stack.stackId)
  const armoryAssets = useArmoryAssetsInfo(stack.stackId)
  const dbcStore = useDbcStoreStatus()
  const extraChoices = useModuleExtraDataChoices(stack.stackId)
  const extraStack = useModuleExtraDataStackStatus(stack.stackId)
  const extraJob = useModuleExtraDataJob(stack.stackId, extraChoices.data?.modules?.length ? true : false)
  useAutoPrepareModuleExtras(stack.stackId, dbcStore.data?.ready === true)
  const playerbots = usePlayerbotsConf(stack.stackId)
  const ip = useIpProgressionStatus(stack.stackId, hasIp)
  const progress = useSetupProgressStore(stack.stackId)

  useEffect(() => {
    if (progress.getPlayerbotsPhase() === 'starting' && isDatabaseRunning(stack)) {
      progress.setPlayerbotsPhase('awaiting-reenable')
    }
  }, [progress, stack])

  return {
    stack,
    patchesHref,
    onSelectTab,
    status: {
      soapInitialized: stack.isAdminAccountInitialized,
      dbcStore: {
        ready: dbcStore.data?.ready ?? false,
        inProgress: dbcStore.data?.inProgress ?? false,
        loading: dbcStore.isLoading,
        tag: dbcStore.data?.tag ?? null,
      },
      moduleExtraData: {
        modules: extraChoices.data?.modules ?? [],
        loading: extraChoices.isLoading || extraStack.isLoading,
        jobPhase: extraJob.data?.phase ?? null,
        ipContentMode: extraStack.data?.ipContentMode ?? extraChoices.data?.saved?.ipContentMode ?? 'Unset',
        prepared: extraStack.data?.prepared ?? false,
        deposited: extraStack.data?.deposited ?? false,
        hasPendingDeposit: extraStack.data?.hasPendingDeposit ?? false,
      },
      client: {
        dataUploaded: clientBase.data?.exists ?? false,
        containerRunning: isStackServiceRunning(stack, 'client'),
        loading: clientBase.isLoading,
      },
      armory: {
        dbcUploaded: armoryAssets.data?.dataUploaded ?? false,
        containerRunning: stack.armoryRunning || isStackServiceRunning(stack, 'frontend-armory'),
        loading: armoryAssets.isLoading,
      },
      playerbots: {
        confPath: playerbots.path,
        enabled: playerbots.enabled,
        chatterDisabled: playerbots.chatterDisabled,
        loading: playerbots.isLoading,
      },
      individualProgression: {
        bootstrapped: ip.bootstrapped,
        syncCompleted: ip.syncCompleted,
        loading: ip.isLoading,
      },
      progress,
    },
  }
}
