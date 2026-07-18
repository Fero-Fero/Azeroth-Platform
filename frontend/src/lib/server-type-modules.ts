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

type SortableModule = { id: string; name: string; recommended?: boolean; requiredModuleIds?: string[] }

/** Puts server-type required modules first, then dependency-required selections, then recommended, then name. */
export function sortModulesForDisplay<T extends SortableModule>(
  modules: T[],
  options: {
    serverType: ServerType
    serverTypes: ServerTypeInfoDto[] | undefined
    selectedIds?: string[]
  },
): T[] {
  const serverRequiredOrder = requiredModuleIdsForServerType(options.serverType, options.serverTypes)
  const serverRequired = new Set(serverRequiredOrder)
  const selected = new Set(options.selectedIds ?? [])

  const dependencyRequired = (id: string) =>
    modules.some(
      (module) =>
        selected.has(module.id) &&
        module.requiredModuleIds?.includes(id) &&
        selected.has(id),
    )

  const rank = (id: string): number => {
    if (serverRequired.has(id)) return 0
    if (dependencyRequired(id)) return 1
    return 2
  }

  return [...modules].sort((a, b) => {
    const rankDiff = rank(a.id) - rank(b.id)
    if (rankDiff !== 0) return rankDiff

    if (serverRequired.has(a.id) && serverRequired.has(b.id)) {
      return serverRequiredOrder.indexOf(a.id) - serverRequiredOrder.indexOf(b.id)
    }

    if (!!a.recommended !== !!b.recommended) return a.recommended ? -1 : 1
    return a.name.localeCompare(b.name)
  })
}
