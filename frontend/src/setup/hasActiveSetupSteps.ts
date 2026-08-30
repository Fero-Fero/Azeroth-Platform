import { collectAllSteps } from '@/setup/collectAllSteps'
import { resolveVisibleSteps } from '@/setup/resolveVisibleSteps'
import { useSetupStepContext } from '@/setup/useSetupStepContext'
import type { SetupStepContext } from '@/setup/types'
import type { StackDetailsDto } from '@/types/stack.types'

export function hasActiveSetupSteps(ctx: SetupStepContext): boolean {
  return resolveVisibleSteps(collectAllSteps(ctx), ctx).length > 0
}

export function useHasActiveSetupSteps(stack: StackDetailsDto): boolean {
  const ctx = useSetupStepContext(stack, () => {})
  return hasActiveSetupSteps(ctx)
}
