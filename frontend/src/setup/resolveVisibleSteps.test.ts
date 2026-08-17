import { describe, expect, it } from 'vitest'
import { GLOBAL_STEP_IDS } from '@/setup/constants'
import { collectAllSteps } from '@/setup/collectAllSteps'
import { globalSteps } from '@/setup/global-steps'
import { countSetupProgress, resolveVisibleSteps } from '@/setup/resolveVisibleSteps'
import { disablePlayerbotsStep } from '@/setup/steps/modules/mod-playerbots'
import { MODULE_IDS, STEP_IDS } from '@/setup/constants'
import { createMockContext } from '@/setup/test/fixtures'
import type { SetupStep } from '@/setup/types'
import { ServerType } from '@/types/stack.types'

describe('globalSteps', () => {
  it('starts with SOAP, upload client, upload armory DBC', () => {
    expect(globalSteps.map((step) => step.id)).toEqual([...GLOBAL_STEP_IDS])
  })
})

describe('resolveVisibleSteps', () => {
  it('shows one sequenced step at a time', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.playerbots],
        } as never,
      },
      status: { playerbots: { confPath: 'x', enabled: true, loading: false } },
    })
    const steps: SetupStep[] = [
      { ...disablePlayerbotsStep(), sequenced: true },
      {
        id: 'second',
        sequenced: true,
        dependsOn: [STEP_IDS.disablePlayerbots],
        level: 'warning',
        title: 'Second',
        applies: () => true,
        isComplete: () => false,
        summary: () => '',
        Component: () => null,
      },
    ]
    expect(resolveVisibleSteps(steps, ctx).map((step) => step.id)).toEqual([STEP_IDS.disablePlayerbots])
  })

  it('hides playerbots steps when mod-playerbots is not installed', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression],
        } as never,
      },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.some((step) => step.id === STEP_IDS.disablePlayerbots)).toBe(false)
    expect(visible.some((step) => step.id === STEP_IDS.reenablePlayerbots)).toBe(false)
    expect(visible.some((step) => step.id === STEP_IDS.prepareProgression)).toBe(true)
  })

  it('does not show IP playerbots pipeline on the Playerbots server type', () => {
    const ctx = createMockContext({
      stack: {
        configuration: { serverType: ServerType.Playerbots, moduleIds: [] } as never,
      },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.some((step) => step.id === STEP_IDS.disablePlayerbots)).toBe(false)
    expect(visible.some((step) => step.sequenced)).toBe(false)
  })

  it('hides the IP sync hint while the pipeline is incomplete', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression],
        } as never,
      },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.some((step) => step.id === STEP_IDS.ipSyncHint)).toBe(false)
  })

  it('shows SOAP even when client/armory are skipped', () => {
    const ctx = createMockContext({
      status: {
        soapInitialized: false,
        client: { dataUploaded: false, containerRunning: true, loading: false },
        armory: { dbcUploaded: false, containerRunning: true, loading: false },
      },
    })
    ctx.status.progress.skip(STEP_IDS.uploadClient)
    ctx.status.progress.skip(STEP_IDS.uploadArmoryDbc)
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.map((step) => step.id)).toContain(STEP_IDS.soapAdmin)
    expect(visible.map((step) => step.id)).not.toContain(STEP_IDS.uploadClient)
    expect(visible.map((step) => step.id)).not.toContain(STEP_IDS.uploadArmoryDbc)
  })
})

describe('countSetupProgress', () => {
  it('counts remaining sequenced steps that are not yet on screen', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression, MODULE_IDS.playerbots],
        } as never,
      },
      status: { playerbots: { confPath: 'x', enabled: true, loading: false } },
    })
    const allSteps = collectAllSteps(ctx)
    const progress = countSetupProgress(allSteps, ctx)
    expect(progress.remaining).toBeGreaterThan(1)
    expect(progress.total).toBe(progress.remaining)
    expect(resolveVisibleSteps(allSteps, ctx).length).toBeLessThan(progress.remaining)
  })

  it('does not count playerbots pipeline steps when the module is missing', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression],
        } as never,
      },
    })
    const remainingIds = collectAllSteps(ctx)
      .filter((step) => !step.isComplete(ctx) && (step.progressApplies ?? step.applies)(ctx))
      .map((step) => step.id)
    expect(remainingIds).not.toContain(STEP_IDS.disablePlayerbots)
    expect(remainingIds).not.toContain(STEP_IDS.reenablePlayerbots)
    expect(remainingIds).toContain(STEP_IDS.prepareProgression)
  })
})
