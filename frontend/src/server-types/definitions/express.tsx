import { RecommendedAddonsNotice } from '@/server-types/notices/RecommendedAddonsNotice'
import type { ServerTypeDefinition } from '@/server-types/types'
import { STEP_IDS } from '@/setup/constants'
import { expressPipelineSteps } from '@/setup/steps/express/expressPipelineSteps'
import type { SetupWorkflowBuilder } from '@/setup/types'
import { ServerType } from '@/types/stack.types'

const IP_RECOMMENDED_ADDON_IDS = ['atlas-loot-individual-progression']
const AUTO_STEP_IDS = new Set<string>([
  STEP_IDS.disablePlayerbots,
  STEP_IDS.prepareProgression,
  STEP_IDS.ipSyncHint,
  STEP_IDS.reenablePlayerbots,
  STEP_IDS.ahBot,
  STEP_IDS.startStack,
  STEP_IDS.stopStack,
  STEP_IDS.restartStack,
  STEP_IDS.dungeonSim,
  STEP_IDS.ollamaDisablePlayerbotsChatter,
])

export const buildExpressSteps: SetupWorkflowBuilder = (_ctx, moduleSteps) => {
  const independent = moduleSteps.filter((step) => !AUTO_STEP_IDS.has(step.id) && !step.sequenced)
  return [...expressPipelineSteps(), ...independent]
}

export const expressServerType: ServerTypeDefinition = {
  id: ServerType.Express,
  recommendedAddonIds: IP_RECOMMENDED_ADDON_IDS,
  buildSetupSteps: buildExpressSteps,
  wizardModulesNotice: () => (
    <div className="mb-4 space-y-2 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
      <p>
        After the first build, click <strong>Setup and Launch!</strong> on Overview. Express Setup disables
        playerbots, boots the server, configures SOAP, turns bots back on, builds the launcher, then asks for
        a client.
      </p>
      <RecommendedAddonsNotice ids={expressServerType.recommendedAddonIds} />
    </div>
  ),
  wizardReviewNotes: () => (
    <div className="space-y-1 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
      <p>
        After the first build, click <strong>Setup and Launch!</strong> on Overview. You do not walk the
        start/stop setup cards yourself.
      </p>
    </div>
  ),
}
