import { serverWideProgressionSetup } from '@/setup/custom-setups/serverWideProgression'
import type { SetupStep } from '@/setup/types'

export { prepareProgressionStep } from '@/setup/steps/modules/mod-individual-progression/prepareProgressionStep'
export { ipSyncHintStep } from '@/setup/steps/modules/mod-individual-progression/ipSyncHintStep'
export {
  hasIndividualProgressionModule,
  isIpBootstrapped,
  isIpPipelineComplete,
  isIpProgressionReady,
  isIpSyncComplete,
} from '@/setup/steps/modules/mod-individual-progression/ipStatus'

/** Server Wide Progression is a custom setup invoked from this module. */
export function individualProgressionModuleSteps(): SetupStep[] {
  return serverWideProgressionSetup.buildSteps()
}
