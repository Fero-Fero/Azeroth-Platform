import { MODULE_IDS } from '@/setup/constants'
import { ahBotModuleSteps } from '@/setup/steps/modules/mod-ah-bot'
import { individualProgressionModuleSteps } from '@/setup/steps/modules/mod-individual-progression'
import { ollamaModuleSteps } from '@/setup/steps/modules/mod-ollama'
import { dungeonSimModuleSteps } from '@/setup/steps/modules/mod-playerbot-dungeon-sim'
import type { SetupStep } from '@/setup/types'

/**
 * Default-order module steps. Playerbots disable/re-enable stay off this list (IP server-type
 * pipeline only). Server Wide Progression is registered here via the IP module, which calls that
 * custom setup - the IP server type sequences those steps and does not import them again.
 */
const moduleStepRegistry: Record<string, () => SetupStep[]> = {
  [MODULE_IDS.ahBot]: ahBotModuleSteps,
  [MODULE_IDS.individualProgression]: individualProgressionModuleSteps,
  [MODULE_IDS.dungeonSim]: dungeonSimModuleSteps,
  [MODULE_IDS.ollamaChat]: ollamaModuleSteps,
  [MODULE_IDS.ollamaBuddy]: ollamaModuleSteps,
}

export function resolveModuleSteps(moduleIds: string[]): SetupStep[] {
  const steps: SetupStep[] = []
  for (const id of moduleIds) {
    steps.push(...(moduleStepRegistry[id]?.() ?? []))
  }
  return steps
}
