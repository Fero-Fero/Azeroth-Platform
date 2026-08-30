import { describe, expect, it } from 'vitest'
import { AH_BOT_GUID_KEY, MODULE_IDS } from '@/setup/constants'
import { mergeModuleEnvDefaults } from '@/setup/steps/modules/envDefaults'

describe('mergeModuleEnvDefaults', () => {
  it('merges AH bot GUID defaults without overwriting existing values', () => {
    const merged = mergeModuleEnvDefaults(MODULE_IDS.ahBot, {
      worldserver: { [AH_BOT_GUID_KEY]: '1,2', OTHER: 'x' },
    })
    expect(merged.worldserver).toEqual({ [AH_BOT_GUID_KEY]: '1,2', OTHER: 'x' })
  })

  it('returns the same object when the module has no defaults', () => {
    const current = { worldserver: { FOO: 'bar' } }
    expect(mergeModuleEnvDefaults('mod-unknown', current)).toBe(current)
  })
})
