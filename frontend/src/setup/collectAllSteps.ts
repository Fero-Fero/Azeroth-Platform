import { buildServerTypeSteps } from '@/server-types'
import { STEP_IDS } from '@/setup/constants'
import { globalSteps } from '@/setup/global-steps'
import { resolveModuleSteps } from '@/setup/steps/modules'
import { moduleExtraDataStep } from '@/setup/steps/modules/moduleExtraDataStep'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { ServerType } from '@/types/stack.types'

const EXPRESS_HIDDEN_GLOBAL_IDS = new Set<string>([
  STEP_IDS.soapAdmin,
  STEP_IDS.dbcBaseline,
  STEP_IDS.uploadClient,
  STEP_IDS.uploadArmoryDbc,
])

export function collectAllSteps(ctx: SetupStepContext): SetupStep[] {
  const moduleSteps = resolveModuleSteps(ctx.stack.configuration.moduleIds ?? [])
  const isExpress = ctx.stack.configuration.serverType === ServerType.Express
  const isIndividualProgression =
    ctx.stack.configuration.serverType === ServerType.IndividualProgression
  const globals = isExpress
    ? globalSteps.filter((step) => !EXPRESS_HIDDEN_GLOBAL_IDS.has(step.id))
    : isIndividualProgression
      ? globalSteps.filter((step) => step.id !== STEP_IDS.soapAdmin)
      : globalSteps
  const extraData = isExpress || isIndividualProgression ? [] : [moduleExtraDataStep()]
  return [...globals, ...extraData, ...buildServerTypeSteps(ctx, moduleSteps)]
}
