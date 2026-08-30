import { describe, expect, it } from 'vitest'
import { MODULE_IDS } from '@/setup/constants'
import { isIpPipelineComplete, isIpProgressionReady } from '@/setup/steps/modules/mod-individual-progression/ipStatus'
import { createMockContext } from '@/setup/test/fixtures'
import { ServerType } from '@/types/stack.types'

describe('ipStatus', () => {
  it('is ready only when bootstrapped and synced', () => {
    const status = createMockContext().status
    expect(isIpProgressionReady(status)).toBe(false)
    status.individualProgression.bootstrapped = true
    expect(isIpProgressionReady(status)).toBe(false)
    status.individualProgression.syncCompleted = true
    expect(isIpProgressionReady(status)).toBe(true)
  })

  it('pipeline without playerbots completes when progression is ready', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression],
        } as never,
      },
      status: { individualProgression: { bootstrapped: true, syncCompleted: true, loading: false } },
    })
    expect(isIpPipelineComplete(ctx)).toBe(true)
  })

  it('pipeline with playerbots completes only after playerbots setup is marked complete', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.IndividualProgression,
          moduleIds: [MODULE_IDS.individualProgression, MODULE_IDS.playerbots],
        } as never,
      },
      status: { individualProgression: { bootstrapped: true, syncCompleted: true, loading: false } },
    })
    expect(isIpPipelineComplete(ctx)).toBe(false)
    ctx.status.progress.markPlayerbotsSetupComplete()
    expect(isIpPipelineComplete(ctx)).toBe(true)
  })
})
