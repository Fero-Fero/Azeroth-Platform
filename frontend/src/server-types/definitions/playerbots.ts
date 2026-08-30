import { defaultSetup } from '@/server-types/registry/defaultSetup'
import type { ServerTypeDefinition } from '@/server-types/types'
import { ServerType } from '@/types/stack.types'

export const playerbotsServerType: ServerTypeDefinition = {
  id: ServerType.Playerbots,
  recommendedAddonIds: [],
  buildSetupSteps: defaultSetup,
}
