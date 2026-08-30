import { describe, expect, it } from 'vitest'
import { MODULE_IDS, STEP_IDS } from '@/setup/constants'
import {
  hasOllamaModule,
  isExpressStack,
  isOllamaPlayerbotsChatterComplete,
} from '@/setup/steps/modules/mod-ollama/ollamaChatterStatus'
import { createMockContext, createMockStack } from '@/setup/test/fixtures'
import { ServerType } from '@/types/stack.types'

describe('ollamaChatterStatus', () => {
  it('detects either Ollama module', () => {
    expect(hasOllamaModule(createMockStack())).toBe(false)
    expect(
      hasOllamaModule(
        createMockStack({
          configuration: { serverType: ServerType.Playerbots, moduleIds: [MODULE_IDS.ollamaChat] } as never,
        }),
      ),
    ).toBe(true)
    expect(
      hasOllamaModule(
        createMockStack({
          configuration: { serverType: ServerType.Playerbots, moduleIds: [MODULE_IDS.ollamaBuddy] } as never,
        }),
      ),
    ).toBe(true)
  })

  it('detects Express stacks', () => {
    expect(
      isExpressStack(
        createMockStack({
          configuration: { serverType: ServerType.Express, moduleIds: [MODULE_IDS.ollamaChat] } as never,
        }),
      ),
    ).toBe(true)
    expect(
      isExpressStack(
        createMockStack({
          configuration: { serverType: ServerType.Playerbots, moduleIds: [MODULE_IDS.ollamaChat] } as never,
        }),
      ),
    ).toBe(false)
  })

  it('allows dismiss on non-Express', () => {
    const ctx = createMockContext({
      stack: {
        configuration: {
          serverType: ServerType.Standard,
          moduleIds: [MODULE_IDS.ollamaBuddy],
        } as never,
      },
      status: { playerbots: { confPath: 'x', enabled: true, chatterDisabled: false, loading: false } },
    })
    expect(isOllamaPlayerbotsChatterComplete(ctx)).toBe(false)
    ctx.status.progress.dismiss(STEP_IDS.ollamaDisablePlayerbotsChatter)
    expect(isOllamaPlayerbotsChatterComplete(ctx)).toBe(true)
  })
})
