import { useEffect, useMemo } from 'react'
import type { WizardForm } from '@/types/wizard.types'
import { useGlobalAddonCatalog } from '@/hooks/useAddons'
import {
  addonRequiresUnselectedModule,
  catalogEntrySort,
  ensureRelatedModules,
  orderCatalogForDisplay,
  toggleCatalogSelection,
} from '@/lib/addon-catalog'
import { MODULE_IDS } from '@/setup/constants'
import { cn } from '@/lib/utils'
import { ServerType } from '@/types/stack.types'

const DUNGEON_CLEAR_ADDON_ID = 'dungeon-clear-addon'

interface ExpressAddonsStepProps {
  form: WizardForm
}

export function ExpressAddonsStep({ form }: ExpressAddonsStepProps) {
  const { data: catalog = [], isLoading } = useGlobalAddonCatalog()
  const moduleIds = form.watch('moduleIds') ?? []
  const addonIds = form.watch('addonIds') ?? []
  const selected = useMemo(() => new Set(addonIds), [addonIds])

  const visible = useMemo(() => {
    const dungeonClearInstalled = moduleIds.includes(MODULE_IDS.dungeonClear)
    const tagged = catalog
      .filter((entry) => entry.id !== DUNGEON_CLEAR_ADDON_ID || dungeonClearInstalled)
      .map((entry) => ({
        ...entry,
        suggested:
          entry.relatedModuleIds?.some((id) => moduleIds.includes(id))
          || entry.relatedServerTypes?.includes(ServerType.Express)
          || false,
      }))
    tagged.sort(catalogEntrySort)
    return orderCatalogForDisplay(tagged, selected)
  }, [catalog, moduleIds, selected])

  useEffect(() => {
    if (catalog.length === 0) {
      return
    }

    const dungeonClearInstalled = moduleIds.includes(MODULE_IDS.dungeonClear)
    let next = addonIds.filter((id) => {
      if (id === DUNGEON_CLEAR_ADDON_ID) {
        return dungeonClearInstalled
      }
      const entry = catalog.find((item) => item.id === id)
      if (!entry) {
        return false
      }
      return !addonRequiresUnselectedModule(entry.relatedModuleIds, moduleIds)
    })
    if (dungeonClearInstalled && !next.includes(DUNGEON_CLEAR_ADDON_ID)) {
      next = [...next, DUNGEON_CLEAR_ADDON_ID]
    }
    if (next.length !== addonIds.length || next.some((id, index) => id !== addonIds[index])) {
      form.setValue('addonIds', next, { shouldDirty: true, shouldValidate: true })
    }
  }, [addonIds, catalog, form, moduleIds])

  const toggle = (id: string) => {
    const nextAddons = toggleCatalogSelection(id, catalog, selected)
    let nextModules = [...moduleIds]
    for (const addonId of nextAddons) {
      const entry = catalog.find((item) => item.id === addonId)
      nextModules = ensureRelatedModules(entry?.relatedModuleIds, nextModules)
    }
    form.setValue('moduleIds', nextModules, { shouldDirty: true, shouldValidate: true })
    form.setValue('addonIds', [...nextAddons], { shouldDirty: true, shouldValidate: true })
  }

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Addons</h2>
        <p className="mt-1 text-sm text-gray-500">
          Optional client addons served through the launcher. The Dungeon Clear UI addon is installed
          automatically when the Dungeon Clear module is selected.
        </p>
      </div>

      {isLoading ? (
        <p className="text-sm text-gray-500">Loading addons…</p>
      ) : (
        <div className="space-y-2">
          {visible.map((entry) => {
            const isDungeonClearAddon = entry.id === DUNGEON_CLEAR_ADDON_ID
            const checked = selected.has(entry.id) || isDungeonClearAddon
            const needsModule = addonRequiresUnselectedModule(entry.relatedModuleIds, moduleIds)
            const requiredNames = (entry.relatedModuleIds ?? [])
              .map((id) => id.replace(/^mod-/, '').replace(/-/g, ' '))
              .join(' or ')
            return (
              <label
                key={entry.id}
                className={cn(
                  'flex items-start gap-3 rounded-lg border p-3',
                  entry.parentAddonId && 'ml-6',
                  isDungeonClearAddon ? 'cursor-default border-emerald-200 bg-emerald-50' : 'cursor-pointer',
                  !isDungeonClearAddon && (checked ? 'border-blue-300 bg-blue-50' : 'border-gray-200'),
                )}
              >
                <input
                  type="checkbox"
                  className="mt-1"
                  checked={checked}
                  disabled={isDungeonClearAddon}
                  onChange={() => {
                    if (!isDungeonClearAddon) toggle(entry.id)
                  }}
                />
                <span>
                  <span className="font-medium text-gray-900">{entry.name}</span>
                  {isDungeonClearAddon && (
                    <span className="ml-2 text-xs font-semibold uppercase tracking-wide text-emerald-700">
                      Auto
                    </span>
                  )}
                  {entry.category && (
                    <span className="ml-2 text-xs uppercase tracking-wide text-gray-400">{entry.category}</span>
                  )}
                  <span className="mt-0.5 block text-xs text-gray-500">
                    {isDungeonClearAddon
                      ? 'Installed automatically because the Dungeon Clear module is on this Express server.'
                      : entry.description}
                  </span>
                  {!isDungeonClearAddon && needsModule && requiredNames && (
                    <span className="mt-1 block text-xs text-amber-700">
                      Also installs the {requiredNames} module.
                    </span>
                  )}
                </span>
              </label>
            )
          })}
        </div>
      )}
    </div>
  )
}
