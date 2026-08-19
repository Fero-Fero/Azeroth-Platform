import { describe, expect, it } from 'vitest'
import { collectAllSteps } from '@/setup/collectAllSteps'
import { serverWideProgressionSetup } from '@/setup/custom-setups/serverWideProgression'
import { MODULE_IDS, STEP_IDS } from '@/setup/constants'
import { individualProgressionModuleSteps } from '@/setup/steps/modules/mod-individual-progression'
import { createMockContext } from '@/setup/test/fixtures'
import { ServerType } from '@/types/stack.types'

describe('serverWideProgressionSetup', () => {
  it('is invoked by the Individual Progression module setup', () => {
    expect(individualProgressionModuleSteps().map((step) => step.id)).toEqual(
      serverWideProgressionSetup.buildSteps().map((step) => step.id),
    )
    expect(individualProgressionModuleSteps().map((step) => step.id)).toEqual([
      STEP_IDS.prepareProgression,
      STEP_IDS.ipSyncHint,
    ])
  })

  it('appears on any server type that selected the IP module', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.Standard,
          moduleIds: [MODULE_IDS.individualProgression],
        } as never,
      },
    })
    const ids = collectAllSteps(ctx).map((step) => step.id)
    expect(ids).toContain(STEP_IDS.prepareProgression)
    expect(ids).toContain(STEP_IDS.ipSyncHint)
  })

  it('is skippable and treated as complete when skipped', () => {
    expect(serverWideProgressionSetup.skippable).toBe(true)
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression],
        } as never,
      },
    })
    const prepare = collectAllSteps(ctx).find((step) => step.id === STEP_IDS.prepareProgression)
    expect(prepare?.skippable).toBe(true)
    expect(prepare?.isComplete(ctx)).toBe(false)
    ctx.status.progress.skip(STEP_IDS.prepareProgression)
    expect(prepare?.isComplete(ctx)).toBe(true)
    expect(prepare?.applies(ctx)).toBe(false)
  })

  it('hides SWP prepare when Standard IP content mode is selected', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression],
        } as never,
      },
      status: {
        moduleExtraData: {
          modules: [],
          loading: false,
          jobPhase: null,
          ipContentMode: 'Standard',
          prepared: false,
          deposited: false,
          hasPendingDeposit: false,
        },
      },
    })
    const ids = collectAllSteps(ctx)
      .filter((step) => step.applies(ctx))
      .map((step) => step.id)
    expect(ids).not.toContain(STEP_IDS.prepareProgression)
    expect(ids).not.toContain(STEP_IDS.ipSyncHint)
  })
})
