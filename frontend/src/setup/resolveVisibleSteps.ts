import type { SetupStep, SetupStepContext } from '@/setup/types'

function isStepCompleteById(id: string, steps: SetupStep[], ctx: SetupStepContext): boolean {
  const step = steps.find((item) => item.id === id)
  if (!step) return true
  if (!step.applies(ctx)) return true
  return step.isComplete(ctx)
}

function resolvePipelineStep(steps: SetupStep[], ctx: SetupStepContext): SetupStep[] {
  for (const step of steps) {
    if (!step.applies(ctx)) continue
    if (step.dependsOn?.some((id) => !isStepCompleteById(id, steps, ctx))) continue
    if (!step.isComplete(ctx)) return [step]
    if (step.showWhenComplete?.(ctx)) return [step]
  }
  return []
}

function isVisibleIndependent(step: SetupStep, ctx: SetupStepContext): boolean {
  if (!step.applies(ctx)) return false
  if (!step.isComplete(ctx)) return true
  return step.showWhenComplete?.(ctx) === true
}

export function resolveVisibleSteps(allSteps: SetupStep[], ctx: SetupStepContext): SetupStep[] {
  const independent = allSteps.filter((step) => !step.sequenced)
  const sequenced = allSteps.filter((step) => step.sequenced)
  return [...independent.filter((step) => isVisibleIndependent(step, ctx)), ...resolvePipelineStep(sequenced, ctx)]
}

export type SetupProgressCounts = {
  total: number
  completed: number
  remaining: number
}

function isProgressRelevant(step: SetupStep, ctx: SetupStepContext): boolean {
  if (step.isComplete(ctx)) return true
  return (step.progressApplies ?? step.applies)(ctx)
}

/** Counts every applicable setup step, including sequenced ones not currently on screen. */
export function countSetupProgress(allSteps: SetupStep[], ctx: SetupStepContext): SetupProgressCounts {
  const relevant = allSteps.filter((step) => isProgressRelevant(step, ctx))
  const completed = relevant.filter((step) => step.isComplete(ctx)).length
  return {
    total: relevant.length,
    completed,
    remaining: relevant.length - completed,
  }
}
