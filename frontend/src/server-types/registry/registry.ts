import {
  customServerType,
  individualProgressionServerType,
  npcBotsServerType,
  playerbotsServerType,
  standardServerType,
} from '@/server-types/definitions'
import { defaultSetup } from '@/server-types/registry/defaultSetup'
import type { ServerTypeDefinition } from '@/server-types/types'
import type { SetupStep, SetupStepContext } from '@/setup/types'

export const serverTypeDefinitions: ServerTypeDefinition[] = [
  standardServerType,
  playerbotsServerType,
  individualProgressionServerType,
  npcBotsServerType,
  customServerType,
]

const byId = new Map(serverTypeDefinitions.map((definition) => [definition.id, definition]))

export function getServerTypeDefinition(id: string): ServerTypeDefinition | undefined {
  return byId.get(id)
}

export function buildServerTypeSteps(ctx: SetupStepContext, moduleSteps: SetupStep[]) {
  const build = byId.get(ctx.stack.configuration.serverType)?.buildSetupSteps ?? defaultSetup
  return build(ctx, moduleSteps)
}

/**
 * Every enabled API catalog id must have a frontend definition.
 * Extra frontend files are allowed (disabled catalog entries are omitted from the API).
 */
export function assertServerTypeRegistry(catalogIds: readonly string[]): void {
  const frontendIds = new Set(serverTypeDefinitions.map((definition) => definition.id))
  for (const id of catalogIds) {
    if (!frontendIds.has(id)) {
      throw new Error(
        `Server type "${id}" exists in the API catalog but has no frontend definition in server-types/definitions/.`,
      )
    }
  }
}
