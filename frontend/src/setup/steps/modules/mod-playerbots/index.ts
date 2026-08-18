import type { SetupStep } from '@/setup/types'
import { disablePlayerbotsStep } from '@/setup/steps/modules/mod-playerbots/disablePlayerbotsStep'
import { reenablePlayerbotsStep } from '@/setup/steps/modules/mod-playerbots/reenablePlayerbotsStep'

export { disablePlayerbotsStep } from '@/setup/steps/modules/mod-playerbots/disablePlayerbotsStep'
export { reenablePlayerbotsStep } from '@/setup/steps/modules/mod-playerbots/reenablePlayerbotsStep'
export {
  hasPlayerbotsModule,
  isPlayerbotsDisabled,
  isPlayerbotsSetupComplete,
  playerbotsDisabledPhase,
} from '@/setup/steps/modules/mod-playerbots/playerbotsStatus'

/** Pipeline-only on the Individual Progression server type - not spread via the module registry. */
export function playerbotsModuleSteps(): SetupStep[] {
  return [disablePlayerbotsStep(), reenablePlayerbotsStep()]
}
