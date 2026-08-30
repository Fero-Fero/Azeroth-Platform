import { MODULE_IDS } from '@/setup/constants'
import { hasPlayerbotsModule, isPlayerbotsSetupComplete } from '@/setup/steps/modules/mod-playerbots/playerbotsStatus'
import type { SetupStepContext, SetupStepStatus } from '@/setup/types'
import type { StackDetailsDto } from '@/types/stack.types'

export function hasIndividualProgressionModule(stack: StackDetailsDto): boolean {
  return stack.configuration.moduleIds?.includes(MODULE_IDS.individualProgression) ?? false
}

export function isIpBootstrapped(status: SetupStepStatus): boolean {
  return status.individualProgression.bootstrapped
}

export function isIpSyncComplete(status: SetupStepStatus): boolean {
  return status.individualProgression.syncCompleted
}

export function isIpProgressionReady(status: SetupStepStatus): boolean {
  return isIpBootstrapped(status) && isIpSyncComplete(status)
}

export function isIpPipelineComplete(ctx: SetupStepContext): boolean {
  if (hasPlayerbotsModule(ctx.stack)) {
    return isPlayerbotsSetupComplete(ctx.status)
  }
  return isIpProgressionReady(ctx.status)
}
