import { MODULE_IDS } from '@/setup/constants'
import { ipSyncHintStep } from '@/setup/steps/modules/mod-individual-progression/ipSyncHintStep'
import { prepareProgressionStep } from '@/setup/steps/modules/mod-individual-progression/prepareProgressionStep'
import type { CustomSetup } from '@/server-types/types'

/**
 * Server Wide Progression Setup — optional custom setup (not a module install).
 * Called from the mod-individual-progression module setup.
 */
export const serverWideProgressionSetup: CustomSetup = {
  id: 'server-wide-progression',
  title: 'Server Wide Progression Setup',
  description:
    'Bootstrap progression, sync from mod-individual-progression, then apply patches in order.',
  skippable: true,
  notice: (
    <>
      After creating the stack, you can complete the <strong>optional</strong> Server Wide Progression
      Setup for an even more immersive experience.
    </>
  ),
  requiresModuleIds: [MODULE_IDS.individualProgression],
  buildSteps: () => [prepareProgressionStep(), ipSyncHintStep()],
}
