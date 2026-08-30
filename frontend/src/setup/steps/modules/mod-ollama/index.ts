import type { SetupStep } from '@/setup/types'
import { disablePlayerbotsChatterStep } from '@/setup/steps/modules/mod-ollama/disablePlayerbotsChatterStep'

export { disablePlayerbotsChatterStep } from '@/setup/steps/modules/mod-ollama/disablePlayerbotsChatterStep'
export {
  hasOllamaModule,
  isOllamaPlayerbotsChatterComplete,
} from '@/setup/steps/modules/mod-ollama/ollamaChatterStatus'

export function ollamaModuleSteps(): SetupStep[] {
  return [disablePlayerbotsChatterStep()]
}
