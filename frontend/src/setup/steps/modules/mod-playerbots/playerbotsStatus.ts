import { MODULE_IDS } from '@/setup/constants'
import type { SetupStepContext, SetupStepStatus } from '@/setup/types'
import type { StackDetailsDto } from '@/types/stack.types'

export function hasPlayerbotsModule(stack: StackDetailsDto): boolean {
  return stack.configuration.moduleIds?.includes(MODULE_IDS.playerbots) ?? false
}

export function isPlayerbotsDisabled(status: SetupStepStatus): boolean {
  return status.playerbots.enabled === false
}

export function isPlayerbotsSetupComplete(status: SetupStepStatus): boolean {
  return status.progress.isPlayerbotsSetupComplete()
}

export function playerbotsDisabledPhase(ctx: SetupStepContext): boolean {
  return (
    hasPlayerbotsModule(ctx.stack) &&
    isPlayerbotsDisabled(ctx.status) &&
    ctx.status.progress.getPlayerbotsPhase() === 'awaiting-start'
  )
}

export function playerbotsAwaitingReenable(ctx: SetupStepContext): boolean {
  return (
    hasPlayerbotsModule(ctx.stack) &&
    isPlayerbotsDisabled(ctx.status) &&
    ctx.status.progress.getPlayerbotsPhase() === 'awaiting-reenable'
  )
}
