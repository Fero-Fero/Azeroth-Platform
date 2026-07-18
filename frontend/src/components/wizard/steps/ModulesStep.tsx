import { useEffect, useState } from 'react'
import { AlertCircle, Check, ExternalLink, Loader2, Lock, Package } from 'lucide-react'
import type { WizardForm } from '@/components/wizard/types'
import { useModules, useServerTypes } from '@/hooks/useModules'
import { applyModuleToggle, isModuleLocked, moduleLockReason } from '@/lib/module-dependencies'
import {
  isServerTypeRequiredModule,
  mergeRequiredModuleIds,
  sortModulesForDisplay,
} from '@/lib/server-type-modules'
import { ServerType } from '@/types/stack.types'
import { cn } from '@/lib/utils'

interface ModulesStepProps {
  form: WizardForm
}

export function ModulesStep({ form }: ModulesStepProps) {
  const { watch, setValue } = form
  const serverType = watch('serverType')
  const selectedIds = watch('moduleIds')
  const [search, setSearch] = useState('')

  const { data: modules, isLoading, isError } = useModules(serverType)
  const { data: serverTypes } = useServerTypes()

  // The backend already filters the catalog to modules that are valid for the chosen server type. If the
  // user switches type after selecting modules, drop any selection that is no longer offered so we never
  // submit an incompatible module (e.g. Individual Progression selected, then type changed to Standard).
  useEffect(() => {
    if (!modules) return
    const availableIds = new Set(modules.map((module) => module.id))
    const pruned = selectedIds.filter((id: string) => availableIds.has(id))
    const withRequired = mergeRequiredModuleIds(pruned, serverType, serverTypes)
    const same =
      withRequired.length === selectedIds.length &&
      withRequired.every((id) => selectedIds.includes(id))
    if (!same) {
      setValue('moduleIds', withRequired, { shouldDirty: true })
    }
  }, [modules, selectedIds, serverType, serverTypes, setValue])

  const filtered = sortModulesForDisplay(
    (modules ?? []).filter(
      (module) =>
        module.name.toLowerCase().includes(search.toLowerCase()) ||
        module.description.toLowerCase().includes(search.toLowerCase()),
    ),
    { serverType, serverTypes, selectedIds },
  )

  // Module-specific env var defaults to inject when a module is toggled on
  const MODULE_ENV_DEFAULTS: Record<string, Record<string, string>> = {
    'mod-ah-bot': { AC_AUCTION_HOUSE_BOT_GUIDS: '' },
  }

  const toggle = (id: string) => {
    if (isServerTypeRequiredModule(id, serverType, serverTypes) && selectedIds.includes(id)) {
      return
    }

    const next = applyModuleToggle(id, selectedIds, modules ?? [])
    if (next === null) {
      return
    }

    const isRemoving = selectedIds.includes(id)
    setValue('moduleIds', next, { shouldDirty: true })

    // Auto-inject module-specific env var defaults when module is enabled. Module env vars are
    // worldserver.conf (AC_*) overrides, so they live in the per-service worldserver bucket.
    if (!isRemoving && MODULE_ENV_DEFAULTS[id]) {
      const services = (form.getValues('advanced.serviceEnvVars') as Record<string, Record<string, string>>) ?? {}
      const worldserver = services['worldserver'] ?? {}
      const merged = { ...MODULE_ENV_DEFAULTS[id], ...worldserver } // existing values win (don't overwrite)
      form.setValue('advanced.serviceEnvVars', { ...services, worldserver: merged }, { shouldDirty: true })
    }
  }

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Modules</h2>
        <p className="mt-1 text-sm text-gray-500">
          Select optional AzerothCore modules to include in your build. Only modules compatible with the
          selected server type are shown.
        </p>
      </div>

      {serverType === ServerType.IndividualProgression && (
        <div className="rounded-lg border border-violet-200 bg-violet-50 px-4 py-3 text-sm text-violet-900 space-y-2">
          <p className="text-violet-800">
            After creating the stack, you will be prompted to <strong>disable playerbots</strong> before
            your first launch so you can configure patches and progression content first.
          </p>
          <p className="text-violet-800">
            Install <strong>AtlasLoot Individual Progression</strong> from the <strong>Addons</strong> tab after
            creation — it restores Naxx 40, Ony 40, and Kazzak loot tables for progressive progression.{' '}
            <a
              href="https://github.com/Day36512/Atlas-Loot-Individual-Progression-3.3.5"
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1 font-medium text-violet-700 underline hover:text-violet-900"
            >
              GitHub project
              <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
            </a>
          </p>
        </div>
      )}

      <input
        type="search"
        placeholder="Search modules…"
        value={search}
        onChange={(event) => setSearch(event.target.value)}
        className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
        aria-label="Search modules"
      />

      {isLoading && (
        <div className="flex items-center justify-center gap-2 py-8 text-sm text-gray-500">
          <Loader2 className="h-5 w-5 animate-spin" aria-hidden="true" />
          Loading modules…
        </div>
      )}

      {isError && (
        <div className="flex items-center gap-2 rounded-md border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800">
          <AlertCircle className="h-4 w-4 shrink-0" aria-hidden="true" />
          <span>
            Modules could not be loaded. You can continue — the module list will be available once the backend is running.
          </span>
        </div>
      )}

      {!isLoading && !isError && filtered.length === 0 && (
        <p className="py-6 text-center text-sm text-gray-400">
          {search ? 'No modules match your search.' : 'No modules available.'}
        </p>
      )}

      {!isLoading && !isError && filtered.length > 0 && (
        <ul className="grid gap-2" role="list" aria-label="Available modules">
          {filtered.map((module) => {
            const isSelected = selectedIds.includes(module.id)
            const requiredByServerType = isServerTypeRequiredModule(module.id, serverType, serverTypes)
            const locked =
              (isSelected && requiredByServerType) ||
              isModuleLocked(module.id, selectedIds, modules ?? [])
            const lockReason = requiredByServerType
              ? 'Required for this server type'
              : moduleLockReason(module.id, selectedIds, modules ?? [])

            return (
              <li key={module.id} className="min-w-0">
                <button
                  type="button"
                  role="checkbox"
                  aria-checked={isSelected}
                  aria-disabled={locked}
                  onClick={() => toggle(module.id)}
                  className={cn(
                    'flex w-full items-start gap-3 rounded-lg border-2 p-3 text-left transition-colors focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-1',
                    isSelected ? 'border-blue-600 bg-blue-50' : 'border-gray-200 bg-white hover:border-gray-300',
                    locked && 'cursor-not-allowed opacity-90'
                  )}
                >
                  <div
                    className={cn(
                      'mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded border-2',
                      isSelected ? 'border-blue-600 bg-blue-600' : 'border-gray-300 bg-white'
                    )}
                    aria-hidden="true"
                  >
                    {locked ? (
                      <Lock className="h-3 w-3 text-white" />
                    ) : (
                      isSelected && <Check className="h-3 w-3 text-white" />
                    )}
                  </div>
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <Package className="h-3.5 w-3.5 shrink-0 text-gray-400" aria-hidden="true" />
                      <span className="text-sm font-medium text-gray-900">{module.name}</span>
                      {module.recommended && <RecommendedBadge />}
                    </div>
                    <p className="mt-0.5 wrap-break-word text-xs text-gray-500">{module.description}</p>
                    {lockReason && <p className="mt-1 text-xs text-amber-700">{lockReason}</p>}
                  </div>
                  {module.repository && (
                    <a
                      href={module.repository}
                      target="_blank"
                      rel="noopener noreferrer"
                      onClick={(event) => event.stopPropagation()}
                      className="mt-0.5 shrink-0 text-gray-400 hover:text-blue-600"
                      aria-label={`View ${module.name} repository`}
                    >
                      <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
                    </a>
                  )}
                </button>
              </li>
            )
          })}
        </ul>
      )}

      {selectedIds.length > 0 && (
        <p className="text-xs text-gray-500">
          {selectedIds.length} module{selectedIds.length !== 1 ? 's' : ''} selected
        </p>
      )}
    </div>
  )
}

function RecommendedBadge() {
  return (
    <span className="rounded-full bg-sky-50 px-2 py-0.5 text-[11px] font-medium text-sky-700 ring-1 ring-inset ring-sky-200">
      Recommended
    </span>
  )
}
