import type { SetupStep } from '@/setup/types'
import { ahBotStep } from '@/setup/steps/modules/mod-ah-bot/ahBotStep'

export { ahBotStep } from '@/setup/steps/modules/mod-ah-bot/ahBotStep'

export function ahBotModuleSteps(): SetupStep[] {
  return [ahBotStep()]
}
