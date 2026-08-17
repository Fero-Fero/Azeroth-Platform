import { describe, expect, it } from 'vitest'
import { MODULE_IDS } from '@/setup/constants'
import {
  hasPlayerbotsModule,
  isPlayerbotsDisabled,
  playerbotsDisabledPhase,
} from '@/setup/steps/modules/mod-playerbots/playerbotsStatus'
import { createMockContext, createMockStack, createMockStatus } from '@/setup/test/fixtures'
import { ServerType } from '@/types/stack.types'

describe('playerbotsStatus', () => {
  it('detects the playerbots module from moduleIds only', () => {
    expect(hasPlayerbotsModule(createMockStack({ configuration: { serverType: ServerType.Playerbots, moduleIds: [] } as never }))).toBe(false)
    expect(
      hasPlayerbotsModule(
        createMockStack({
          configuration: { serverType: ServerType.IndividualProgression, moduleIds: [MODULE_IDS.playerbots] } as never,
        }),
      ),
    ).toBe(true)
  })

  it('treats enabled === false as disabled', () => {
    expect(isPlayerbotsDisabled(createMockStatus({ playerbots: { confPath: 'x', enabled: false, loading: false } }))).toBe(true)
    expect(isPlayerbotsDisabled(createMockStatus({ playerbots: { confPath: 'x', enabled: true, loading: false } }))).toBe(false)
    expect(isPlayerbotsDisabled(createMockStatus({ playerbots: { confPath: null, enabled: null, loading: true } }))).toBe(false)
  })

  it('playerbotsDisabledPhase requires module, disabled conf, and awaiting-start', () => {
    const ctx = createMockContext({
      stack: {
        configuration: { serverType: ServerType.IndividualProgression, moduleIds: [MODULE_IDS.playerbots] } as never,
      },
      status: { playerbots: { confPath: 'x', enabled: false, loading: false } },
    })
    expect(playerbotsDisabledPhase(ctx)).toBe(false)
    ctx.status.progress.setPlayerbotsPhase('awaiting-start')
    expect(playerbotsDisabledPhase(ctx)).toBe(true)
  })
})
