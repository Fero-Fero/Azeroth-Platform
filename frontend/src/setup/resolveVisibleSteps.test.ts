import { describe, expect, it } from 'vitest'
import { GLOBAL_STEP_IDS } from '@/setup/constants'
import { collectAllSteps } from '@/setup/collectAllSteps'
import { globalSteps } from '@/setup/global-steps'
import { countSetupProgress, resolveVisibleSteps } from '@/setup/resolveVisibleSteps'
import { disablePlayerbotsStep } from '@/setup/steps/modules/mod-playerbots'
import { MODULE_IDS, STEP_IDS } from '@/setup/constants'
import { expressPhaseStepId } from '@/setup/steps/express/expressPhases'
import { createMockContext } from '@/setup/test/fixtures'
import type { SetupStep } from '@/setup/types'
import { ServerType, StackStatus } from '@/types/stack.types'

const EXPRESS_TEST_MODULE_IDS = [
  MODULE_IDS.playerbots,
  MODULE_IDS.ahBot,
  MODULE_IDS.individualProgression,
  MODULE_IDS.dungeonSim,
]

describe('globalSteps', () => {
  it('starts with SOAP, DBC baseline, upload client, upload armory DBC', () => {
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
      status: { playerbots: { confPath: 'x', enabled: true, chatterDisabled: null, loading: false } },
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
        status: StackStatus.Running,
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression],
        } as never,
      },
      status: { soapInitialized: true },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.some((step) => step.id === STEP_IDS.disablePlayerbots)).toBe(false)
    expect(visible.some((step) => step.id === STEP_IDS.reenablePlayerbots)).toBe(false)
    expect(visible.some((step) => step.id === STEP_IDS.prepareProgression)).toBe(true)
  })

  it('shows SOAP after start begins and the database is up', () => {
    const ctx = createMockContext({
      stack: {
        status: StackStatus.Starting,
        services: [{ service: 'ac-database', state: 'running' } as never],
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression, MODULE_IDS.playerbots],
        } as never,
      },
      status: { playerbots: { confPath: 'x', enabled: false, chatterDisabled: null, loading: false } },
    })
    ctx.status.progress.setPlayerbotsPhase('starting')
    const ids = resolveVisibleSteps(collectAllSteps(ctx), ctx).map((step) => step.id)
    expect(ids).not.toContain(STEP_IDS.startStack)
    expect(ids).toContain(STEP_IDS.soapAdmin)
    expect(ids).not.toContain(STEP_IDS.moduleExtraData)
    expect(ids).not.toContain(STEP_IDS.prepareProgression)
  })

  it('keeps the start step visible with a starting phase until the database is up', () => {
    const ctx = createMockContext({
      stack: {
        status: StackStatus.Starting,
        services: [],
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression, MODULE_IDS.playerbots],
        } as never,
      },
      status: { playerbots: { confPath: 'x', enabled: false, chatterDisabled: null, loading: false } },
    })
    ctx.status.progress.setPlayerbotsPhase('starting')
    const ids = resolveVisibleSteps(collectAllSteps(ctx), ctx).map((step) => step.id)
    expect(ids).toContain(STEP_IDS.startStack)
    expect(ids).not.toContain(STEP_IDS.soapAdmin)
  })

  it('waits for DBimport after SOAP before offering Individual Progression', () => {
    const ctx = createMockContext({
      stack: {
        status: StackStatus.Initializing,
        services: [
          { service: 'ac-database', state: 'running' } as never,
          { service: 'ac-db-import', state: 'running' } as never,
        ],
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression, MODULE_IDS.playerbots],
        } as never,
      },
      status: {
        soapInitialized: true,
        playerbots: { confPath: 'x', enabled: false, chatterDisabled: null, loading: false },
      },
    })
    ctx.status.progress.setPlayerbotsPhase('starting')
    const ids = resolveVisibleSteps(collectAllSteps(ctx), ctx).map((step) => step.id)
    expect(ids).toContain(STEP_IDS.waitDbImport)
    expect(ids).not.toContain(STEP_IDS.prepareProgression)
    expect(ids).not.toContain(STEP_IDS.moduleExtraData)
  })

  it('shows prepare progression after SOAP and DBimport, and holds module content until sync', () => {
    const ctx = createMockContext({
      stack: {
        status: StackStatus.Running,
        services: [{ service: 'ac-database', state: 'running' } as never],
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression, MODULE_IDS.playerbots],
        } as never,
      },
      status: {
        soapInitialized: true,
        playerbots: { confPath: 'x', enabled: false, chatterDisabled: null, loading: false },
        moduleExtraData: {
          modules: [],
          loading: false,
          jobPhase: null,
          ipContentMode: 'ServerWideProgression',
          prepared: true,
          deposited: false,
          hasPendingDeposit: true,
        },
      },
    })
    ctx.status.progress.setPlayerbotsPhase('starting')
    const ids = resolveVisibleSteps(collectAllSteps(ctx), ctx).map((step) => step.id)
    expect(ids).toContain(STEP_IDS.prepareProgression)
    expect(ids).not.toContain(STEP_IDS.moduleExtraData)
  })

  it('shows module content after Individual Progression sync, not after SOAP', () => {
    const ctx = createMockContext({
      stack: {
        status: StackStatus.Running,
        services: [{ service: 'ac-database', state: 'running' } as never],
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression, MODULE_IDS.playerbots],
        } as never,
      },
      status: {
        soapInitialized: true,
        playerbots: { confPath: 'x', enabled: false, chatterDisabled: null, loading: false },
        individualProgression: { bootstrapped: true, syncCompleted: true, loading: false },
        moduleExtraData: {
          modules: [],
          loading: false,
          jobPhase: null,
          ipContentMode: 'ServerWideProgression',
          prepared: true,
          deposited: false,
          hasPendingDeposit: true,
        },
      },
    })
    ctx.status.progress.setPlayerbotsPhase('starting')
    const ids = resolveVisibleSteps(collectAllSteps(ctx), ctx).map((step) => step.id)
    expect(ids).toContain(STEP_IDS.moduleExtraData)
    expect(ids).not.toContain(STEP_IDS.prepareProgression)
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

  it('does not show re-enable just because playerbots.conf already reads as off', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression, MODULE_IDS.playerbots],
        } as never,
      },
      status: {
        playerbots: { confPath: 'x', enabled: false, chatterDisabled: null, loading: false },
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
    const ids = resolveVisibleSteps(collectAllSteps(ctx), ctx).map((step) => step.id)
    expect(ids).not.toContain(STEP_IDS.disablePlayerbots)
    expect(ids).not.toContain(STEP_IDS.reenablePlayerbots)
  })

  it('keeps disable complete after awaiting-start even if conf still reads enabled', () => {
    const ctx = createMockContext({
      stack: {
        status: StackStatus.Stopped,
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression, MODULE_IDS.playerbots],
        } as never,
      },
      status: { playerbots: { confPath: 'x', enabled: true, chatterDisabled: null, loading: false } },
    })
    ctx.status.progress.setPlayerbotsPhase('awaiting-start')
    const ids = resolveVisibleSteps(collectAllSteps(ctx), ctx).map((step) => step.id)
    expect(ids).not.toContain(STEP_IDS.disablePlayerbots)
    expect(ids).not.toContain(STEP_IDS.reenablePlayerbots)
    expect(ids).toContain(STEP_IDS.startStack)
  })

  it('shows re-enable only after the stack has started with bots off', () => {
    const ctx = createMockContext({
      stack: {
        status: StackStatus.Running,
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression, MODULE_IDS.playerbots],
        } as never,
      },
      status: {
        soapInitialized: true,
        playerbots: { confPath: 'x', enabled: false, chatterDisabled: null, loading: false },
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
    ctx.status.progress.setPlayerbotsPhase('awaiting-reenable')
    const ids = resolveVisibleSteps(collectAllSteps(ctx), ctx).map((step) => step.id)
    expect(ids).not.toContain(STEP_IDS.disablePlayerbots)
    expect(ids).toContain(STEP_IDS.reenablePlayerbots)
  })

  it('counts SOAP as complete while credentials are still on screen', () => {
    const ctx = createMockContext({
      stack: {
        status: StackStatus.Running,
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression, MODULE_IDS.playerbots],
        } as never,
      },
      status: {
        soapInitialized: true,
        playerbots: { confPath: 'x', enabled: false, chatterDisabled: null, loading: false },
      },
    })
    ctx.status.progress.setPlayerbotsPhase('awaiting-reenable')
    ctx.status.progress.setSessionFlag('soap-credentials-visible', '1')
    const allSteps = collectAllSteps(ctx)
    const soap = allSteps.find((step) => step.id === STEP_IDS.soapAdmin)
    expect(soap?.isComplete(ctx)).toBe(true)
    expect(resolveVisibleSteps(allSteps, ctx).map((step) => step.id)).toContain(STEP_IDS.soapAdmin)
    const progress = countSetupProgress(allSteps, ctx)
    expect(progress.remaining).toBeGreaterThanOrEqual(0)
    const soapRelevant = allSteps.filter((step) => step.id === STEP_IDS.soapAdmin && (step.isComplete(ctx) || step.applies(ctx)))
    expect(soapRelevant[0]?.isComplete(ctx)).toBe(true)
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

  it('hides SOAP, start/stop, and AH Bot steps on Express until Setup and Launch is running', () => {
    const ctx = createMockContext({
      stack: {
        expressProvisionStatus: 'Pending',
        configuration: {
          serverType: ServerType.Express,
          moduleIds: EXPRESS_TEST_MODULE_IDS,
        } as never,
      },
      status: { soapInitialized: false },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.map((step) => step.id)).toEqual([])
  })

  it('shows completed Express phases plus the current one while Setup is running', () => {
    const ctx = createMockContext({
      stack: {
        expressProvisionStatus: 'Running',
        expressProvisionPhase: 'DisableBots',
        configuration: {
          serverType: ServerType.Express,
          moduleIds: EXPRESS_TEST_MODULE_IDS,
        } as never,
      },
      status: { soapInitialized: false },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.map((step) => step.id)).toEqual([
      expressPhaseStepId('SaveChoices'),
      expressPhaseStepId('DisableBots'),
    ])
  })

  it('starts Express status at SaveChoices when the phase is not set yet', () => {
    const ctx = createMockContext({
      stack: {
        expressProvisionStatus: 'Running',
        configuration: {
          serverType: ServerType.Express,
          moduleIds: EXPRESS_TEST_MODULE_IDS,
        } as never,
      },
      status: { soapInitialized: false },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.map((step) => step.id)).toEqual([expressPhaseStepId('SaveChoices')])
  })

  it('keeps finished Express phases visible while waiting for a client', () => {
    const ctx = createMockContext({
      stack: {
        expressProvisionStatus: 'WaitingForClient',
        expressProvisionPhase: 'WaitClient',
        configuration: {
          serverType: ServerType.Express,
          moduleIds: EXPRESS_TEST_MODULE_IDS,
        } as never,
      },
      status: { soapInitialized: false },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.map((step) => step.id)).toEqual([
      expressPhaseStepId('SaveChoices'),
      expressPhaseStepId('DisableBots'),
      expressPhaseStepId('StartStack'),
      expressPhaseStepId('SoapDbc'),
      expressPhaseStepId('AhBot'),
      expressPhaseStepId('GameAccount'),
      expressPhaseStepId('StopStack'),
      expressPhaseStepId('WaitClient'),
      expressPhaseStepId('SwpSync'),
      expressPhaseStepId('EnableBots'),
      expressPhaseStepId('Launcher'),
    ])
  })

  it('keeps upcoming Express phases visible after Continue from client upload', () => {
    const ctx = createMockContext({
      stack: {
        expressProvisionStatus: 'Running',
        expressProvisionPhase: 'WaitClient',
        configuration: {
          serverType: ServerType.Express,
          moduleIds: EXPRESS_TEST_MODULE_IDS,
        } as never,
      },
      status: { soapInitialized: false },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.map((step) => step.id)).toEqual([
      expressPhaseStepId('SaveChoices'),
      expressPhaseStepId('DisableBots'),
      expressPhaseStepId('StartStack'),
      expressPhaseStepId('SoapDbc'),
      expressPhaseStepId('AhBot'),
      expressPhaseStepId('GameAccount'),
      expressPhaseStepId('StopStack'),
      expressPhaseStepId('WaitClient'),
      expressPhaseStepId('SwpSync'),
      expressPhaseStepId('EnableBots'),
      expressPhaseStepId('Launcher'),
    ])
  })

  it('keeps remaining Express phases visible while Server Wide Progression syncs', () => {
    const ctx = createMockContext({
      stack: {
        expressProvisionStatus: 'Running',
        expressProvisionPhase: 'SwpSync',
        configuration: {
          serverType: ServerType.Express,
          moduleIds: EXPRESS_TEST_MODULE_IDS,
        } as never,
      },
      status: { soapInitialized: false },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.map((step) => step.id)).toEqual([
      expressPhaseStepId('SaveChoices'),
      expressPhaseStepId('DisableBots'),
      expressPhaseStepId('StartStack'),
      expressPhaseStepId('SoapDbc'),
      expressPhaseStepId('AhBot'),
      expressPhaseStepId('GameAccount'),
      expressPhaseStepId('StopStack'),
      expressPhaseStepId('WaitClient'),
      expressPhaseStepId('SwpSync'),
      expressPhaseStepId('EnableBots'),
      expressPhaseStepId('Launcher'),
    ])
  })

  it('shows finished Express phases plus the failed one', () => {
    const ctx = createMockContext({
      stack: {
        expressProvisionStatus: 'Failed',
        expressProvisionPhase: 'SoapDbc',
        configuration: {
          serverType: ServerType.Express,
          moduleIds: EXPRESS_TEST_MODULE_IDS,
        } as never,
      },
      status: { soapInitialized: false },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.map((step) => step.id)).toEqual([
      expressPhaseStepId('SaveChoices'),
      expressPhaseStepId('DisableBots'),
      expressPhaseStepId('StartStack'),
      expressPhaseStepId('SoapDbc'),
    ])
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
      status: { playerbots: { confPath: 'x', enabled: true, chatterDisabled: null, loading: false } },
    })
    const allSteps = collectAllSteps(ctx)
    const progress = countSetupProgress(allSteps, ctx)
    expect(progress.remaining).toBeGreaterThan(1)
    expect(resolveVisibleSteps(allSteps, ctx).length).toBeLessThan(progress.remaining)
  })

  it('does not count playerbots pipeline steps when the module is missing', () => {
    const ctx = createMockContext({
      stack: {
        status: StackStatus.Running,
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression],
        } as never,
      },
      status: { soapInitialized: true },
    })
    const remainingIds = collectAllSteps(ctx)
      .filter((step) => !step.isComplete(ctx) && (step.progressApplies ?? step.applies)(ctx))
      .map((step) => step.id)
    expect(remainingIds).not.toContain(STEP_IDS.disablePlayerbots)
    expect(remainingIds).not.toContain(STEP_IDS.reenablePlayerbots)
    expect(remainingIds).toContain(STEP_IDS.prepareProgression)
  })

  it('counts all Express pipeline phases while Setup is running', () => {
    const ctx = createMockContext({
      stack: {
        expressProvisionStatus: 'Running',
        expressProvisionPhase: 'DisableBots',
        configuration: {
          serverType: ServerType.Express,
          moduleIds: EXPRESS_TEST_MODULE_IDS,
        } as never,
      },
    })
    const progress = countSetupProgress(collectAllSteps(ctx), ctx)
    expect(progress.total).toBe(12)
    expect(progress.completed).toBe(1)
    expect(progress.remaining).toBe(11)
  })
})

describe('ollama playerbots chatter step', () => {
  it('does not show on Express (applied automatically during Express Setup)', () => {
    const ctx = createMockContext({
      stack: {
        expressProvisionStatus: 'Completed',
        configuration: {
          serverType: ServerType.Express,
          moduleIds: [...EXPRESS_TEST_MODULE_IDS, MODULE_IDS.ollamaChat],
        } as never,
      },
      status: { playerbots: { confPath: 'x', enabled: true, chatterDisabled: false, loading: false } },
    })
    const visible = resolveVisibleSteps(collectAllSteps(ctx), ctx)
    expect(visible.some((step) => step.id === STEP_IDS.ollamaDisablePlayerbotsChatter)).toBe(false)
  })

  it('shows as a dismissible suggestion on other server types', () => {
    const playerbots = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.Playerbots,
          moduleIds: [MODULE_IDS.playerbots, MODULE_IDS.ollamaBuddy],
        } as never,
      },
      status: { playerbots: { confPath: 'x', enabled: true, chatterDisabled: false, loading: false } },
    })
    expect(
      resolveVisibleSteps(collectAllSteps(playerbots), playerbots).some(
        (step) => step.id === STEP_IDS.ollamaDisablePlayerbotsChatter,
      ),
    ).toBe(true)
    playerbots.status.progress.dismiss(STEP_IDS.ollamaDisablePlayerbotsChatter)
    expect(
      resolveVisibleSteps(collectAllSteps(playerbots), playerbots).some(
        (step) => step.id === STEP_IDS.ollamaDisablePlayerbotsChatter,
      ),
    ).toBe(false)
  })

  it('is never offered for LLM Chatter, which speaks alongside the built-in chatter', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.Playerbots,
          moduleIds: [MODULE_IDS.playerbots, MODULE_IDS.llmChatter],
        } as never,
      },
      status: { playerbots: { confPath: 'x', enabled: true, chatterDisabled: false, loading: false } },
    })
    expect(
      resolveVisibleSteps(collectAllSteps(ctx), ctx).some(
        (step) => step.id === STEP_IDS.ollamaDisablePlayerbotsChatter,
      ),
    ).toBe(false)
  })
})
