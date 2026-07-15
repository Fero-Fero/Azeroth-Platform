import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { AlertCircle, Check, ExternalLink, Hammer, Loader2, Lock, Package, Save } from 'lucide-react'
import { buildApi, stackApi } from '@/services/api'
import { stackKeys } from '@/hooks/useStacks'
import { useModules, useServerTypes } from '@/hooks/useModules'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import { applyModuleToggle, isModuleLocked, moduleLockReason } from '@/lib/module-dependencies'
import {
  isServerTypeRequiredModule,
  mergeRequiredModuleIds,
} from '@/lib/server-type-modules'
import type { ModuleDto, StackConfigurationDto, StackDetailsDto } from '@/types/stack.types'
import { INDIVIDUAL_PROGRESSION_MODULE_ID } from '@/types/individual-progression.types'
import ModuleCatalogPage from '@/pages/ModuleCatalogPage'

const WORLDSERVER = 'worldserver'

const MODULE_ENV_DEFAULTS: Record<string, Record<string, string>> = {
  'mod-ah-bot': { AC_AUCTION_HOUSE_BOT_GUIDS: '' },
}

interface StackModulesTabProps {
  stack: StackDetailsDto
}

export default function StackModulesTab({ stack }: StackModulesTabProps) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [selectedIds, setSelectedIds] = useState<string[]>(stack.configuration.moduleIds ?? [])
  const [serviceEnvVars, setServiceEnvVars] = useState(stack.configuration.advanced.serviceEnvVars ?? {})
  const [search, setSearch] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [showCatalog, setShowCatalog] = useState(false)

  const { data: modules = [], isLoading, isError } = useModules(stack.configuration.serverType)
  const { data: serverTypes } = useServerTypes()

  useEffect(() => {
    const withRequired = mergeRequiredModuleIds(
      selectedIds,
      stack.configuration.serverType,
      serverTypes,
    )
    const same =
      withRequired.length === selectedIds.length &&
      withRequired.every((id) => selectedIds.includes(id))
    if (!same) {
      setSelectedIds(withRequired)
    }
  }, [selectedIds, serverTypes, stack.configuration.serverType])

  useEffect(() => {
    setSelectedIds(stack.configuration.moduleIds ?? [])
    setServiceEnvVars(stack.configuration.advanced.serviceEnvVars ?? {})
  }, [stack.configuration.moduleIds])

  const moduleById = useMemo(
    () => new Map(modules.map((module) => [module.id, module] as const)),
    [modules],
  )

  const selectedSet = useMemo(() => new Set(selectedIds), [selectedIds])

  const installedModules = selectedIds.map((id) => moduleById.get(id) ?? missingModule(id)).sort(recommendedFirst)
  const availableModules = modules.filter((module) => !selectedSet.has(module.id))
  const filteredAvailable = availableModules
    .filter((module) => {
      const q = search.trim().toLowerCase()
      if (!q) return true
      return (
        module.name.toLowerCase().includes(q) ||
        module.id.toLowerCase().includes(q) ||
        module.description.toLowerCase().includes(q)
      )
    })
    .sort(recommendedFirst)

  const builtInAvailable = filteredAvailable.filter((module) => module.isBuiltIn)
  const customAvailable = filteredAvailable.filter((module) => !module.isBuiltIn)

  const dirty = !sameIds(selectedIds, stack.configuration.moduleIds ?? [])
  const canBuild = stack.status !== 'Building'

  const buildConfig = (moduleIds: string[]): StackConfigurationDto => {
    return {
      ...stack.configuration,
      moduleIds,
      advanced: {
        ...stack.configuration.advanced,
        serviceEnvVars,
        // Keep the legacy flat worldserver mirror in sync with the canonical service bucket.
        customEnvVars: serviceEnvVars[WORLDSERVER] ?? stack.configuration.advanced.customEnvVars ?? {},
      },
    }
  }

  const saveModules = async (moduleIds: string[]) => {
    const updated = await stackApi.updateConfig(stack.stackId, buildConfig(moduleIds))
    await queryClient.invalidateQueries({ queryKey: stackKeys.detail(stack.stackId) })
    await queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    return updated.data
  }

  const saveMutation = useMutation({
    mutationFn: () => saveModules(selectedIds),
    onSuccess: () => setError(null),
    onError: (err) => setError(errorMessage(err)),
  })

  const saveAndBuildMutation = useMutation({
    mutationFn: async () => {
      if (dirty) {
        await saveModules(selectedIds)
      }
      return buildApi.start(stack.stackId, undefined, 'Merge')
    },
    onSuccess: () => {
      setError(null)
      navigate(`/stacks/${stack.stackId}/build`)
    },
    onError: (err) => setError(errorMessage(err)),
  })

  const busy = saveMutation.isPending || saveAndBuildMutation.isPending

  const toggleModule = (moduleId: string) => {
    setError(null)
    if (
      isServerTypeRequiredModule(moduleId, stack.configuration.serverType, serverTypes) &&
      selectedIds.includes(moduleId)
    ) {
      return
    }

    const next = applyModuleToggle(moduleId, selectedIds, modules)
    if (next === null) {
      return
    }

    if (!selectedIds.includes(moduleId) && MODULE_ENV_DEFAULTS[moduleId]) {
      setServiceEnvVars((currentEnv) => {
        const worldserver = currentEnv[WORLDSERVER] ?? {}
        return { ...currentEnv, [WORLDSERVER]: { ...MODULE_ENV_DEFAULTS[moduleId], ...worldserver } }
      })
    }
    setSelectedIds(next)
  }

  return (
    <div className="space-y-6">
      {selectedIds.includes(INDIVIDUAL_PROGRESSION_MODULE_ID) && (
        <div className="rounded-lg border border-violet-200 bg-violet-50 px-5 py-4 text-sm text-violet-900">
          <p className="font-semibold">Individual Progression</p>
          <p className="mt-1 text-violet-800">
            Server-wide progression patches are managed under{' '}
            <button
              type="button"
              onClick={() => navigate(`/stacks/${stack.stackId}?tab=patches`)}
              className="font-medium text-violet-700 underline hover:text-violet-900"
            >
              Game → Patches
            </button>
            . Bootstrap progression there and apply content releases manually.
          </p>
        </div>
      )}

      <div className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Installed modules</h2>
            <p className="mt-1 text-sm text-gray-500">
              Modules compiled into this stack&apos;s worldserver. Changing this selection requires a
              worldserver rebuild before it takes effect.
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => saveMutation.mutate()}
              disabled={!dirty || busy || !canBuild}
              title={!dirty ? 'No changes to save — use Recompile worldserver to rebuild with the current selection' : 'Save module selection without rebuilding'}
              className="inline-flex items-center gap-2 rounded-md border border-blue-300 px-4 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {saveMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              Save selection
            </button>
            <button
              type="button"
              onClick={() => saveAndBuildMutation.mutate()}
              disabled={busy || !canBuild}
              className={`inline-flex items-center gap-2 rounded-md px-4 py-2 text-sm font-medium text-white hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50 ${
                (stack.modulesPendingRebuild?.length ?? 0) > 0 ? 'bg-red-600 hover:bg-red-700' : 'bg-amber-600 hover:bg-amber-700'
              }`}
              title={
                (stack.modulesPendingRebuild?.length ?? 0) > 0
                  ? 'Recompile required — selected modules are not yet built into the worldserver'
                  : 'Rebuild the worldserver with the current module selection'
              }
            >
              {saveAndBuildMutation.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <Hammer className="h-4 w-4" />
              )}
              {(stack.modulesPendingRebuild?.length ?? 0) > 0
                ? 'Recompile worldserver (required)'
                : dirty
                  ? 'Save & recompile worldserver'
                  : 'Recompile worldserver'}
            </button>
          </div>
        </div>

        {dirty && (
          <div className="mt-4 rounded-md border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
            Module selection has unsaved changes. Save and recompile to apply them to the running server.
          </div>
        )}

        {(stack.modulesPendingRebuild?.length ?? 0) > 0 && (
          <div className="mt-4 rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
            These modules are selected but not compiled into the worldserver yet:{' '}
            <span className="font-mono">{stack.modulesPendingRebuild!.join(', ')}</span>.
            Click <strong>Save &amp; recompile worldserver</strong> and wait for the build to finish, then start the stack.
          </div>
        )}

        {!canBuild && (
          <div className="mt-4 rounded-md border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-800">
            This stack is already building. Wait for the current build to finish before changing modules.
          </div>
        )}

        {error && (
          <div className="mt-4 flex items-center gap-2 rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
            <AlertCircle className="h-4 w-4 shrink-0" />
            {error}
          </div>
        )}

        <div className="mt-5">
          {installedModules.length === 0 ? (
            <p className="rounded-md border border-dashed border-gray-300 px-4 py-6 text-center text-sm text-gray-500">
              No optional modules are installed on this stack.
            </p>
          ) : (
            <div className="grid gap-3 md:grid-cols-2">
              {installedModules.map((module) => {
                const requiredByServerType = isServerTypeRequiredModule(
                  module.id,
                  stack.configuration.serverType,
                  serverTypes,
                )
                const locked =
                  requiredByServerType ||
                  isModuleLocked(module.id, selectedIds, modules)
                const lockReason = requiredByServerType
                  ? 'Required for this server type'
                  : moduleLockReason(module.id, selectedIds, modules)

                return (
                <ModuleCard
                  key={module.id}
                  module={module}
                  selected
                  actionLabel="Remove"
                  disabled={busy || !canBuild || locked}
                  lockReason={lockReason}
                  onToggle={() => toggleModule(module.id)}
                />
                )
              })}
            </div>
          )}
        </div>
      </div>

      <div className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <div className="mb-4">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <h3 className="text-lg font-semibold text-gray-900">Add modules</h3>
              <p className="mt-1 text-sm text-gray-500">
                Add built-in or custom catalog modules to this stack. Custom modules appear here once
                they are added to the catalog and compatible with this stack type.
              </p>
            </div>
            <button
              type="button"
              onClick={() => setShowCatalog((value) => !value)}
              className="rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
            >
              {showCatalog ? 'Hide catalog' : 'Manage custom module catalog'}
            </button>
          </div>
        </div>

        <input
          type="search"
          placeholder="Search available modules..."
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          className="mb-4 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
        />

        {isLoading && (
          <div className="flex items-center justify-center gap-2 py-8 text-sm text-gray-500">
            <Loader2 className="h-5 w-5 animate-spin" />
            Loading modules...
          </div>
        )}

        {isError && (
          <div className="flex items-center gap-2 rounded-md border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800">
            <AlertCircle className="h-4 w-4 shrink-0" />
            Modules could not be loaded.
          </div>
        )}

        {!isLoading && !isError && filteredAvailable.length === 0 && (
          <p className="rounded-md border border-dashed border-gray-300 px-4 py-6 text-center text-sm text-gray-500">
            {search ? 'No available modules match your search.' : 'All available modules are already installed.'}
          </p>
        )}

        {!isLoading && !isError && filteredAvailable.length > 0 && (
          <div className="space-y-6">
            <ModuleGroup title="Custom modules" modules={customAvailable} emptyText="No custom modules available for this stack type.">
              {customAvailable.map((module) => (
                <ModuleCard
                  key={module.id}
                  module={module}
                  disabled={busy || !canBuild}
                  actionLabel="Add"
                  onToggle={() => toggleModule(module.id)}
                />
              ))}
            </ModuleGroup>
            <ModuleGroup title="Built-in modules" modules={builtInAvailable} emptyText="No built-in modules match this filter.">
              {builtInAvailable.map((module) => (
                <ModuleCard
                  key={module.id}
                  module={module}
                  disabled={busy || !canBuild}
                  actionLabel="Add"
                  onToggle={() => toggleModule(module.id)}
                />
              ))}
            </ModuleGroup>
          </div>
        )}
      </div>

      {showCatalog && (
        <div className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <ModuleCatalogPage />
        </div>
      )}
    </div>
  )
}

function ModuleGroup({
  title,
  modules,
  emptyText,
  children,
}: {
  title: string
  modules: ModuleDto[]
  emptyText: string
  children: React.ReactNode
}) {
  return (
    <section>
      <h4 className="mb-2 text-sm font-semibold uppercase tracking-wide text-gray-500">{title}</h4>
      {modules.length === 0 ? (
        <p className="text-sm text-gray-400">{emptyText}</p>
      ) : (
        <div className="grid gap-3 md:grid-cols-2">{children}</div>
      )}
    </section>
  )
}

function ModuleCard({
  module,
  selected = false,
  actionLabel,
  disabled,
  lockReason,
  onToggle,
}: {
  module: ModuleDto
  selected?: boolean
  actionLabel: string
  disabled: boolean
  lockReason?: string | null
  onToggle: () => void
}) {
  const locked = !!lockReason

  return (
    <div className={`rounded-lg border p-4 ${selected ? 'border-blue-200 bg-blue-50' : 'border-gray-200 bg-white'}`}>
      <div className="flex items-start gap-3">
        <div className={`mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded border ${selected ? 'border-blue-600 bg-blue-600' : 'border-gray-300'}`}>
          {locked ? (
            <Lock className="h-3 w-3 text-white" />
          ) : selected ? (
            <Check className="h-3 w-3 text-white" />
          ) : (
            <Package className="h-3 w-3 text-gray-400" />
          )}
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <p className="font-medium text-gray-900">{module.name}</p>
            {module.recommended && <RecommendedBadge />}
            <span className="rounded-full bg-gray-100 px-2 py-0.5 text-[11px] font-medium text-gray-600">
              {module.isBuiltIn ? 'Built-in' : 'Custom'}
            </span>
          </div>
          <p className="mt-0.5 font-mono text-[11px] text-gray-400">{module.id}</p>
          <p className="mt-1 text-sm text-gray-500">{module.description || 'No description provided.'}</p>
          {lockReason && <p className="mt-1 text-xs text-amber-700">{lockReason}</p>}
          {module.repository && (
            <a
              href={module.repository}
              target="_blank"
              rel="noreferrer"
              className="mt-2 inline-flex items-center gap-1 text-xs text-blue-600 hover:text-blue-800"
            >
              Repository <ExternalLink className="h-3 w-3" />
            </a>
          )}
        </div>
        <button
          type="button"
          onClick={onToggle}
          disabled={disabled}
          className={`shrink-0 rounded-md px-3 py-1.5 text-xs font-medium disabled:cursor-not-allowed disabled:opacity-50 ${
            selected
              ? 'border border-red-200 bg-white text-red-700 hover:bg-red-50'
              : 'bg-blue-600 text-white hover:bg-blue-700'
          }`}
        >
          {actionLabel}
        </button>
      </div>
    </div>
  )
}

function sameIds(a: string[], b: string[]) {
  if (a.length !== b.length) return false
  const left = [...a].sort()
  const right = [...b].sort()
  return left.every((id, index) => id === right[index])
}

function missingModule(id: string): ModuleDto {
  return {
    id,
    name: id,
    description: 'This module is installed on the stack but is no longer present in the current catalog.',
    repository: '',
    branch: '',
    isBuiltIn: false,
    recommended: false,
  }
}

function recommendedFirst<T extends { recommended?: boolean; name: string }>(a: T, b: T) {
  if (!!a.recommended !== !!b.recommended) return a.recommended ? -1 : 1
  return a.name.localeCompare(b.name)
}

function RecommendedBadge() {
  return (
    <span className="rounded-full bg-sky-50 px-2 py-0.5 text-[11px] font-medium text-sky-700 ring-1 ring-inset ring-sky-200">
      Recommended
    </span>
  )
}
