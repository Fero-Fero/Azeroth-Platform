import type { ModuleDto } from '@/types/stack.types'

type DependencyMaps = {
  requiredBy: Record<string, string[]>
  requiredFor: Record<string, string[]>
}

export function buildModuleDependencyMaps(modules: ModuleDto[]): DependencyMaps {
  const requiredBy: Record<string, string[]> = {}
  const requiredFor: Record<string, string[]> = {}

  for (const module of modules) {
    for (const requiredId of module.requiredModuleIds ?? []) {
      requiredBy[module.id] ??= []
      if (!requiredBy[module.id].includes(requiredId)) {
        requiredBy[module.id].push(requiredId)
      }

      requiredFor[requiredId] ??= []
      if (!requiredFor[requiredId].includes(module.id)) {
        requiredFor[requiredId].push(module.id)
      }
    }
  }

  return { requiredBy, requiredFor }
}

/** Toggle a module on/off, auto-selecting dependencies and blocking removal of locked modules. */
export function applyModuleToggle(
  moduleId: string,
  selectedIds: string[],
  modules: ModuleDto[],
): string[] | null {
  const { requiredBy, requiredFor } = buildModuleDependencyMaps(modules)

  if (selectedIds.includes(moduleId)) {
    const dependents = (requiredFor[moduleId] ?? []).filter((id) => selectedIds.includes(id))
    if (dependents.length > 0) {
      return null
    }

    const next = new Set(selectedIds)
    next.delete(moduleId)

    for (const depId of collectDependencyTree(moduleId, requiredBy)) {
      const stillNeeded = [...next].some((other) => (requiredBy[other] ?? []).includes(depId))
      if (!stillNeeded) {
        next.delete(depId)
      }
    }

    return [...next]
  }

  const next = new Set(selectedIds)
  next.add(moduleId)
  for (const requiredId of requiredBy[moduleId] ?? []) {
    next.add(requiredId)
  }
  return [...next]
}

/** Direct and transitive module dependencies declared by the catalog. */
function collectDependencyTree(
  moduleId: string,
  requiredBy: Record<string, string[]>,
): string[] {
  const seen = new Set<string>()
  const stack = [...(requiredBy[moduleId] ?? [])]

  while (stack.length > 0) {
    const id = stack.pop()!
    if (seen.has(id)) {
      continue
    }

    seen.add(id)
    for (const dep of requiredBy[id] ?? []) {
      stack.push(dep)
    }
  }

  return [...seen]
}

export function isModuleLocked(
  moduleId: string,
  selectedIds: string[],
  modules: ModuleDto[],
): boolean {
  const { requiredFor } = buildModuleDependencyMaps(modules)
  return (
    selectedIds.includes(moduleId) &&
    (requiredFor[moduleId] ?? []).some((dependentId) => selectedIds.includes(dependentId))
  )
}

export function moduleLockReason(
  moduleId: string,
  selectedIds: string[],
  modules: ModuleDto[],
): string | null {
  const { requiredFor } = buildModuleDependencyMaps(modules)
  const dependents = (requiredFor[moduleId] ?? []).filter((id) => selectedIds.includes(id))
  if (dependents.length === 0) {
    return null
  }

  const names = dependents.map((id) => modules.find((module) => module.id === id)?.name ?? id)
  return `Required by ${names.join(', ')}`
}
