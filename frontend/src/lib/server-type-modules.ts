import type { ServerType, ServerTypeInfoDto } from '@/types/stack.types'

export function requiredModuleIdsForServerType(
  serverType: ServerType,
  serverTypes: ServerTypeInfoDto[] | undefined,
): string[] {
  return serverTypes?.find((type) => type.id === serverType)?.requiredModuleIds ?? []
}

export function isServerTypeRequiredModule(
  moduleId: string,
  serverType: ServerType,
  serverTypes: ServerTypeInfoDto[] | undefined,
): boolean {
  return requiredModuleIdsForServerType(serverType, serverTypes).includes(moduleId)
}

export function mergeRequiredModuleIds(
  selectedIds: string[],
  serverType: ServerType,
  serverTypes: ServerTypeInfoDto[] | undefined,
): string[] {
  const required = requiredModuleIdsForServerType(serverType, serverTypes)
  if (required.length === 0) {
    return selectedIds
  }

  const merged = new Set(selectedIds)
  for (const id of required) {
    merged.add(id)
  }
  return [...merged]
}
