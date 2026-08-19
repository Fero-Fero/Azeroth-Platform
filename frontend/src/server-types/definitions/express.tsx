import { RecommendedAddonsNotice } from '@/server-types/notices/RecommendedAddonsNotice'
import type { ServerTypeDefinition } from '@/server-types/types'
import { MODULE_IDS, STEP_IDS } from '@/setup/constants'
import { expressProvisionCompleted, expressProvisionStep } from '@/setup/steps/express/expressProvisionStep'
import type { SetupWorkflowBuilder } from '@/setup/types'
import { ServerType } from '@/types/stack.types'

const IP_RECOMMENDED_ADDON_IDS = ['atlas-loot-individual-progression']
const AUTO_STEP_IDS = new Set<string>([
  STEP_IDS.disablePlayerbots,
  STEP_IDS.prepareProgression,
  STEP_IDS.ipSyncHint,
  STEP_IDS.reenablePlayerbots,
])

export const buildExpressSteps: SetupWorkflowBuilder = (ctx, moduleSteps) => {
  if (expressProvisionCompleted(ctx.stack) || ctx.stack.expressProvisionStatus === 'Running' || ctx.stack.expressProvisionStatus === 'Pending') {
    const independent = moduleSteps.filter((step) => !AUTO_STEP_IDS.has(step.id) && !step.sequenced)
    return [expressProvisionStep(), ...independent]
  }

  return [expressProvisionStep(), ...moduleSteps.filter((step) => !AUTO_STEP_IDS.has(step.id))]
}

export const expressServerType: ServerTypeDefinition = {
  id: ServerType.Express,
  recommendedAddonIds: IP_RECOMMENDED_ADDON_IDS,
  buildSetupSteps: buildExpressSteps,
  wizardModulesNotice: () => (
    <div className="mb-4 space-y-2 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
      <p>
        Express Setup downloads the client, applies the first Server Wide Progression patch, then starts
        the server with your chosen bot count. Playerbots stay off until that first patch lands.
      </p>
      <RecommendedAddonsNotice ids={expressServerType.recommendedAddonIds} />
    </div>
  ),
  wizardReviewNotes: ({ selectedModuleIds }) => (
    <div className="space-y-1 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
      <p>After the first build, Express Setup runs automatically. You do not need to validate patches.</p>
      {selectedModuleIds.includes(MODULE_IDS.ollamaBuddyAdvanced) && (
        <p>Advanced Ollama Bot Buddy is selected (memory / heavier).</p>
      )}
    </div>
  ),
}
