import type { SetupStep } from '@/setup/types'
import { dungeonSimNotesStep } from '@/setup/steps/modules/mod-playerbot-dungeon-sim/dungeonSimNotesStep'

export { dungeonSimNotesStep } from '@/setup/steps/modules/mod-playerbot-dungeon-sim/dungeonSimNotesStep'

export function dungeonSimModuleSteps(): SetupStep[] {
  return [dungeonSimNotesStep()]
}
