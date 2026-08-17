import { describe, expect, it } from 'vitest'
import { assertServerTypeRegistry, serverTypeDefinitions } from '@/server-types/registry'
import { ServerType } from '@/types/stack.types'

const DEFAULT_CATALOG_IDS = [
  ServerType.Standard,
  ServerType.Playerbots,
  ServerType.IndividualProgression,
  ServerType.NpcBots,
  ServerType.Custom,
]

describe('serverTypeDefinitions', () => {
  it('has one definition per default catalog id and no extras', () => {
    expect(serverTypeDefinitions.map((definition) => definition.id).sort()).toEqual(
      [...DEFAULT_CATALOG_IDS].sort(),
    )
  })

  it('throws when the API catalog has an id with no frontend file', () => {
    expect(() => assertServerTypeRegistry(['Standard', 'DoesNotExist'])).toThrow(
      /DoesNotExist.*no frontend definition/,
    )
  })

  it('allows extra frontend files when a catalog type is disabled', () => {
    expect(() => assertServerTypeRegistry([ServerType.Standard])).not.toThrow()
  })
})
