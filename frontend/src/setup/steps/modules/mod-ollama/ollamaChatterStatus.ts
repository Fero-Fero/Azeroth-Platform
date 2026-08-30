import { MODULE_IDS, OLLAMA_MODULE_IDS, STEP_IDS } from '@/setup/constants'
import type { SetupStepContext } from '@/setup/types'
import type { StackDetailsDto } from '@/types/stack.types'
import { ServerType } from '@/types/stack.types'

export function hasOllamaModule(stack: StackDetailsDto): boolean {
  const ids = stack.configuration.moduleIds ?? []
  return OLLAMA_MODULE_IDS.some((id) => ids.includes(id))
}

export function isExpressStack(stack: StackDetailsDto): boolean {
  return stack.configuration.serverType === ServerType.Express
}

export function isOllamaPlayerbotsChatterApplied(ctx: SetupStepContext): boolean {
  return ctx.status.playerbots.chatterDisabled === true
}

export function isOllamaPlayerbotsChatterDismissed(ctx: SetupStepContext): boolean {
  return !isExpressStack(ctx.stack) && ctx.status.progress.isDismissed(STEP_IDS.ollamaDisablePlayerbotsChatter)
}

export function isOllamaPlayerbotsChatterComplete(ctx: SetupStepContext): boolean {
  return isOllamaPlayerbotsChatterApplied(ctx) || isOllamaPlayerbotsChatterDismissed(ctx)
}

export function ollamaModuleLabel(stack: StackDetailsDto): string {
  const ids = stack.configuration.moduleIds ?? []
  if (ids.includes(MODULE_IDS.ollamaBuddy)) {
    return 'Ollama Bot Buddy'
  }
  return 'Ollama Chat'
}
