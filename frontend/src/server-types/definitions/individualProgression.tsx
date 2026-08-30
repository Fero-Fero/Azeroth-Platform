import { WipBadge } from '@/components/common/WipBadge'
import { CustomSetupNotice } from '@/server-types/notices/CustomSetupNotice'
import { RecommendedAddonsNotice } from '@/server-types/notices/RecommendedAddonsNotice'
import type { ServerTypeDefinition } from '@/server-types/types'
import { serverWideProgressionSetup } from '@/setup/custom-setups/serverWideProgression'
import { MODULE_IDS, STEP_IDS } from '@/setup/constants'
import { soapAdminStep } from '@/setup/global-steps/soapAdminStep'
import { isDatabaseRunning } from '@/setup/stackServices'
import { startStackStep, waitDbImportStep } from '@/setup/steps/stack'
import { moduleExtraDataStep } from '@/setup/steps/modules/moduleExtraDataStep'
import {
  disablePlayerbotsStep,
  hasPlayerbotsModule,
  isPlayerbotsSetupComplete,
  playerbotsDisabledPhase,
  playerbotsStartingPhase,
  reenablePlayerbotsStep,
} from '@/setup/steps/modules/mod-playerbots'
import type { SetupStep, SetupWorkflowBuilder } from '@/setup/types'
import { ServerType } from '@/types/stack.types'

const IP_CUSTOM_STEP_IDS = new Set<string>([STEP_IDS.prepareProgression, STEP_IDS.ipSyncHint])
const IP_RECOMMENDED_ADDON_IDS = ['atlas-loot-individual-progression']

/**
 * Playerbots disable → start → SOAP → wait for DBimport → Server Wide Progression →
 * module content → re-enable. Independent module steps (AH bot, dungeon sim, …) stay parallel.
 */
export const buildIndividualProgressionSteps: SetupWorkflowBuilder = (_ctx, moduleSteps) => {
  const prepare = moduleSteps.find((step) => step.id === STEP_IDS.prepareProgression)
  const hint = moduleSteps.find((step) => step.id === STEP_IDS.ipSyncHint)
  const independent: SetupStep[] = moduleSteps.filter(
    (step) => !IP_CUSTOM_STEP_IDS.has(step.id) && !step.sequenced,
  )

  return [
    { ...disablePlayerbotsStep(), sequenced: true },
    {
      ...startStackStep({
        label: 'Start stack with playerbots off',
        when: (ctx) => playerbotsDisabledPhase(ctx) || playerbotsStartingPhase(ctx),
        isComplete: (ctx) =>
          isPlayerbotsSetupComplete(ctx.status) ||
          ctx.status.progress.getPlayerbotsPhase() === 'awaiting-reenable' ||
          (ctx.status.progress.getPlayerbotsPhase() === 'starting' && isDatabaseRunning(ctx.stack)),
        onStarted: (ctx) => ctx.status.progress.setPlayerbotsPhase('starting'),
      }),
      sequenced: true,
      dependsOn: [STEP_IDS.disablePlayerbots],
      level: (ctx) => (playerbotsStartingPhase(ctx) ? 'loading' : 'warning'),
      progressApplies: (ctx) =>
        hasPlayerbotsModule(ctx.stack) && !isPlayerbotsSetupComplete(ctx.status),
    },
    {
      ...soapAdminStep(),
      sequenced: true,
      dependsOn: [STEP_IDS.startStack, STEP_IDS.disablePlayerbots],
    },
    {
      ...waitDbImportStep(),
      sequenced: true,
      dependsOn: [STEP_IDS.soapAdmin],
    },
    ...(prepare
      ? [{ ...prepare, sequenced: true, dependsOn: [STEP_IDS.waitDbImport, STEP_IDS.soapAdmin] }]
      : []),
    {
      ...moduleExtraDataStep(),
      sequenced: true,
      dependsOn: [STEP_IDS.prepareProgression, STEP_IDS.waitDbImport],
    },
    {
      ...reenablePlayerbotsStep(),
      sequenced: true,
      dependsOn: [STEP_IDS.moduleExtraData, STEP_IDS.prepareProgression],
      progressApplies: (ctx) =>
        hasPlayerbotsModule(ctx.stack) && !isPlayerbotsSetupComplete(ctx.status),
    },
    ...independent,
    ...(hint ? [hint] : []),
  ]
}

export const individualProgressionServerType: ServerTypeDefinition = {
  id: ServerType.IndividualProgression,
  recommendedAddonIds: IP_RECOMMENDED_ADDON_IDS,
  customSetups: [serverWideProgressionSetup],
  buildSetupSteps: buildIndividualProgressionSteps,
  wizardModulesNotice: ({ selectedModuleIds, browseTab }) => {
    if (browseTab !== 'curated') {
      return null
    }
    return (
      <div className="mb-4 space-y-2 rounded-lg border border-violet-200 bg-violet-50 px-4 py-3 text-sm text-violet-900">
        {selectedModuleIds.includes(MODULE_IDS.playerbots) && (
          <p className="text-violet-800">
            After creating the stack, you will be prompted to <strong>disable playerbots</strong> before
            your first launch so you can configure patches and progression content first.
          </p>
        )}
        <CustomSetupNotice
          setups={individualProgressionServerType.customSetups}
          selectedModuleIds={selectedModuleIds}
        />
        <RecommendedAddonsNotice ids={individualProgressionServerType.recommendedAddonIds} />
      </div>
    )
  },
  wizardReviewNotes: ({ selectedModuleIds }) => {
    const showPlayerbots = selectedModuleIds.includes(MODULE_IDS.playerbots)
    const showProgression = selectedModuleIds.includes(MODULE_IDS.individualProgression)
    if (!showPlayerbots && !showProgression) {
      return null
    }
    return (
      <div className="space-y-1 rounded-lg border border-violet-200 bg-violet-50 px-4 py-3 text-sm text-violet-900">
        {showPlayerbots && <p>You will be asked to disable playerbots before the first launch.</p>}
        {showProgression && (
          <p className="inline-flex flex-wrap items-center gap-x-1.5 gap-y-0.5">
            After creating the stack, you can complete the <strong>optional</strong> Server Wide
            Progression Setup for an even more immersive experience.
            <WipBadge />
          </p>
        )}
      </div>
    )
  },
}
