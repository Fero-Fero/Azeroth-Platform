import type { AddonCatalogEntryDto } from '@/types/addon.types'

export function catalogChildIds(
  parentId: string,
  catalog: AddonCatalogEntryDto[],
): string[] {
  return catalog.filter((entry) => entry.parentAddonId === parentId).map((entry) => entry.id)
}

/** True when a child row should be shown (parent checked or already installed). */
export function isCatalogChildVisible(
  entry: AddonCatalogEntryDto,
  catalog: AddonCatalogEntryDto[],
  selectedIds: ReadonlySet<string>,
): boolean {
  if (!entry.parentAddonId) return true

  const parent = catalog.find((item) => item.id === entry.parentAddonId)
  if (!parent) return true

  return selectedIds.has(parent.id) || parent.installed
}

/** Parent-first display order with visible children nested under their parent. */
export function orderCatalogForDisplay(
  catalog: AddonCatalogEntryDto[],
  selectedIds: ReadonlySet<string>,
): AddonCatalogEntryDto[] {
  const roots = catalog.filter((entry) => !entry.parentAddonId)
  const ordered: AddonCatalogEntryDto[] = []

  for (const root of roots) {
    ordered.push(root)
    for (const child of catalog) {
      if (child.parentAddonId === root.id && isCatalogChildVisible(child, catalog, selectedIds)) {
        ordered.push(child)
      }
    }
  }

  return ordered
}

export function toggleCatalogSelection(
  id: string,
  catalog: AddonCatalogEntryDto[],
  selectedIds: ReadonlySet<string>,
): Set<string> {
  const entry = catalog.find((item) => item.id === id)
  if (!entry || entry.installed) {
    return new Set(selectedIds)
  }

  const next = new Set(selectedIds)
  const childIds = catalogChildIds(id, catalog)

  if (next.has(id)) {
    next.delete(id)
    for (const childId of childIds) {
      next.delete(childId)
    }
    return next
  }

  next.add(id)
  for (const childId of childIds) {
    const child = catalog.find((item) => item.id === childId)
    if (child && !child.installed) {
      next.add(childId)
    }
  }

  return next
}

/** Install parents before their children. */
export function sortCatalogIdsForInstall(
  ids: string[],
  catalog: AddonCatalogEntryDto[],
): string[] {
  const byId = new Map(catalog.map((entry) => [entry.id, entry]))
  return [...ids].sort((a, b) => {
    const entryA = byId.get(a)
    const entryB = byId.get(b)
    if (entryA?.parentAddonId === b) return 1
    if (entryB?.parentAddonId === a) return -1
    return (entryA?.name ?? a).localeCompare(entryB?.name ?? b)
  })
}

export function catalogEntrySort(a: AddonCatalogEntryDto, b: AddonCatalogEntryDto) {
  if (!!a.recommended !== !!b.recommended) return a.recommended ? -1 : 1
  if (!!a.suggested !== !!b.suggested) return a.suggested ? -1 : 1
  return a.name.localeCompare(b.name)
}
