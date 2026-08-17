import { buildServerTypeSteps } from '@/server-types'
import { globalSteps } from '@/setup/global-steps'
import { resolveModuleSteps } from '@/setup/steps/modules'
import type { SetupStep, SetupStepContext } from '@/setup/types'

export function collectAllSteps(ctx: SetupStepContext): SetupStep[] {
  const moduleSteps = resolveModuleSteps(ctx.stack.configuration.moduleIds ?? [])
  return [...globalSteps, ...buildServerTypeSteps(ctx, moduleSteps)]
}
