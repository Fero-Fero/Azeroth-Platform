import { useArmoryAssetsInfo } from '@/hooks/useArmoryAssets'
import { useClientBaseInfo } from '@/hooks/useClient'
import { MODULE_IDS } from '@/setup/constants'
import { useSetupProgressStore } from '@/setup/progress/setupProgressStore'
import { isStackServiceRunning } from '@/setup/stackServices'
import { useIpProgressionStatus } from '@/setup/steps/modules/mod-individual-progression/useIpProgressionStatus'
import { usePlayerbotsConf } from '@/setup/steps/modules/mod-playerbots/usePlayerbotsConf'
import type { SetupStepContext, SetupTabId } from '@/setup/types'
import type { StackDetailsDto } from '@/types/stack.types'

export function useSetupStepContext(
  stack: StackDetailsDto,
  onSelectTab: (tab: SetupTabId) => void,
): SetupStepContext {
  const patchesHref = `/stacks/${stack.stackId}?tab=patches`
  const hasIp = stack.configuration.moduleIds?.includes(MODULE_IDS.individualProgression) ?? false

  const clientBase = useClientBaseInfo(stack.stackId)
  const armoryAssets = useArmoryAssetsInfo(stack.stackId)
  const playerbots = usePlayerbotsConf(stack.stackId)
  const ip = useIpProgressionStatus(stack.stackId, hasIp)
  const progress = useSetupProgressStore(stack.stackId)

  return {
    stack,
    patchesHref,
    onSelectTab,
    status: {
      soapInitialized: stack.isAdminAccountInitialized,
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
