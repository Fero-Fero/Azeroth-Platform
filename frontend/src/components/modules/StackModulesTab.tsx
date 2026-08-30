import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { AlertCircle, Check, ChevronDown, ExternalLink, Hammer, Loader2, Lock, Package, Save } from 'lucide-react'
import { buildApi, stackApi } from '@/services/api'
import { stackKeys } from '@/hooks/useStacks'
import { useCommunityModules, useModules, useServerTypes } from '@/hooks/useModules'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import { applyModuleToggle, isModuleLocked, moduleLockReason } from '@/lib/module-dependencies'
import {
  isAiChatModuleId,
  selectAiChatModule,
  selectedAiChatModuleId,
  toAiChatOptions,
} from '@/lib/ai-chat-modules'
import {
  isServerTypeRequiredModule,
  mergeRequiredModuleIds,
  sortModulesForDisplay,
} from '@/lib/server-type-modules'
import type { ModuleDto, StackConfigurationDto, StackDetailsDto } from '@/types/stack.types'
import AiBotChattingGroup from '@/components/modules/AiBotChattingGroup'
import CommunityModulesBrowser from '@/components/modules/CommunityModulesBrowser'
import ModuleBrowseTabs, { type ModuleBrowseTab } from '@/components/modules/ModuleBrowseTabs'
import StackModuleSectionTabs, { type StackModulesSectionTab } from '@/components/modules/StackModuleSectionTabs'
import ModuleCatalogPage from '@/pages/ModuleCatalogPage'
import { ServerTypeSlot } from '@/server-types'
import { mergeModuleEnvDefaults } from '@/setup/steps/modules/envDefaults'
import { cn } from '@/lib/utils'

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
  const [sectionTab, setSectionTab] = useState<StackModulesSectionTab>('add')
  const [browseTab, setBrowseTab] = useState<ModuleBrowseTab>('curated')

  const { data: modules = [], isLoading, isError } = useModules(stack.configuration.serverType)
  const { data: serverTypes } = useServerTypes()
  const { data: communityPreview } = useCommunityModules({
    page: 1,
    pageSize: 1,
    enabled: sectionTab === 'add' && browseTab === 'community',
  })

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

  const installedModules = sortModulesForDisplay(
    selectedIds.map((id) => moduleById.get(id) ?? missingModule(id)),
    {
      serverType: stack.configuration.serverType,
      serverTypes,
      selectedIds,
    },
  )
  const availableModules = modules.filter((module) => !selectedSet.has(module.id))
  const filteredAvailable = sortModulesForDisplay(
    availableModules.filter((module) => {
      const q = search.trim().toLowerCase()
      if (!q) return true
      return (
        module.name.toLowerCase().includes(q) ||
        module.id.toLowerCase().includes(q) ||
        module.description.toLowerCase().includes(q)
      )
    }),
    {
      serverType: stack.configuration.serverType,
      serverTypes,
      selectedIds,
    },
  )

  // The mutually exclusive AI chat modules get their own single-select group instead of appearing as
  // separate "Add" cards; the group covers switching between them and removing them.
  const aiChatOptions = toAiChatOptions(modules)
  const browsable = filteredAvailable.filter((module) => !isAiChatModuleId(module.id))
  const builtInAvailable = browsable.filter((module) => module.isBuiltIn)
  const customAvailable = browsable.filter((module) => !module.isBuiltIn)

  const selectAiChat = (moduleId: string | null) => {
    setError(null)
    if (moduleId && !selectedIds.includes(moduleId)) {
      setServiceEnvVars((currentEnv) => mergeModuleEnvDefaults(moduleId, currentEnv))
    }
    setSelectedIds(selectAiChatModule(selectedIds, moduleId, modules))
  }

  const dirty = !sameIds(selectedIds, stack.configuration.moduleIds ?? [])
  const canBuild = stack.status !== 'Building'
  const pendingRebuildCount = stack.modulesPendingRebuild?.length ?? 0

  const buildConfig = (moduleIds: string[]): StackConfigurationDto => {
    return {
      ...stack.configuration,
      moduleIds,
      advanced: {
        ...stack.configuration.advanced,
        serviceEnvVars,
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

    if (!selectedIds.includes(moduleId)) {
      setServiceEnvVars((currentEnv) => mergeModuleEnvDefaults(moduleId, currentEnv))
    }
    setSelectedIds(next)
  }

  const addCommunityModule = (moduleId: string) => {
    setError(null)
    if (selectedIds.includes(moduleId)) {
      return
    }

    const next = applyModuleToggle(moduleId, selectedIds, modules) ?? [...selectedIds, moduleId]
    if (!selectedIds.includes(moduleId)) {
      setServiceEnvVars((currentEnv) => mergeModuleEnvDefaults(moduleId, currentEnv))
    }
    setSelectedIds(next)
  }

  return (
    <div className="space-y-4">
      <div className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm sm:p-6">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Modules</h2>
            <p className="mt-1 text-sm text-gray-500">
              Manage modules compiled into this stack&apos;s worldserver. Changes require a rebuild.
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => saveMutation.mutate()}
              disabled={!dirty || busy || !canBuild}
              title={
                !dirty
                  ? 'No changes to save - use Recompile worldserver to rebuild with the current selection'
                  : 'Save module selection without rebuilding'
              }
              className="inline-flex items-center gap-2 rounded-md border border-blue-300 px-4 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {saveMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              Save selection
            </button>
            <button
              type="button"
              onClick={() => saveAndBuildMutation.mutate()}
              disabled={busy || !canBuild}
              className={cn(
                'inline-flex items-center gap-2 rounded-md px-4 py-2 text-sm font-medium text-white hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50',
                pendingRebuildCount > 0 ? 'bg-red-600 hover:bg-red-700' : 'bg-amber-600 hover:bg-amber-700',
              )}
              title={
                pendingRebuildCount > 0
                  ? 'Recompile required - selected modules are not yet built into the worldserver'
                  : 'Rebuild the worldserver with the current module selection'
              }
            >
              {saveAndBuildMutation.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <Hammer className="h-4 w-4" />
              )}
              {pendingRebuildCount > 0
                ? 'Recompile worldserver (required)'
                : dirty
                  ? 'Save & recompile worldserver'
                  : 'Recompile worldserver'}
            </button>
          </div>
        </div>

        <div className="mt-4 space-y-3">
          {dirty && (
            <div className="rounded-md border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
              Module selection has unsaved changes. Save and recompile to apply them to the running server.
            </div>
          )}

          {pendingRebuildCount > 0 && (
            <div className="rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
              These modules are selected but not compiled into the worldserver yet:{' '}
              <span className="font-mono">{stack.modulesPendingRebuild!.join(', ')}</span>.
              Click <strong>Save &amp; recompile worldserver</strong> and wait for the build to finish, then start
              the stack.
            </div>
          )}

          {!canBuild && (
            <div className="rounded-md border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-800">
              This stack is already building. Wait for the current build to finish before changing modules.
            </div>
          )}

          {error && (
            <div className="flex items-center gap-2 rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
              <AlertCircle className="h-4 w-4 shrink-0" />
              {error}
            </div>
          )}
        </div>

        <div className="mt-5">
          <StackModuleSectionTabs
            active={sectionTab}
            onChange={setSectionTab}
            installedCount={installedModules.length}
            availableCount={availableModules.length}
          />
        </div>

        <div className="mt-5" role="tabpanel">
          {sectionTab === 'installed' ? (
            <div>
              <p className="mb-3 text-sm text-slate-600">
                Expand a module to see its description. All rows start collapsed to keep the list compact.
              </p>
              {installedModules.length === 0 ? (
                <p className="rounded-md border border-dashed border-gray-300 px-4 py-6 text-center text-sm text-gray-500">
                  No optional modules are installed on this stack. Switch to <strong>Add modules</strong> to
                  browse the catalog.
                </p>
              ) : (
                <ul className="space-y-2">
                  {installedModules.map((module) => {
                    const requiredByServerType = isServerTypeRequiredModule(
                      module.id,
                      stack.configuration.serverType,
                      serverTypes,
                    )
                    const locked =
                      requiredByServerType || isModuleLocked(module.id, selectedIds, modules)
                    const lockReason = requiredByServerType
                      ? 'Required for this server type'
                      : moduleLockReason(module.id, selectedIds, modules)

                    return (
                      <InstalledModuleRow
                        key={module.id}
                        module={module}
                        disabled={busy || !canBuild || locked}
                        lockReason={lockReason}
                        onRemove={() => toggleModule(module.id)}
                      />
                    )
                  })}
                </ul>
              )}
            </div>
          ) : (
            <div>
              <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
                <p className="text-sm text-slate-600">
                  Browse curated platform modules or the AzerothCore community catalogue.
                </p>
                <button
                  type="button"
                  onClick={() => setShowCatalog((value) => !value)}
                  className="rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
                >
                  {showCatalog ? 'Hide catalog admin' : 'Manage custom catalog'}
                </button>
              </div>

              <ModuleBrowseTabs
                active={browseTab}
                onChange={setBrowseTab}
                curatedCount={availableModules.length}
                communityCount={communityPreview?.total}
                className="mb-4"
              />

              <ServerTypeSlot
                serverType={stack.configuration.serverType}
                selectedModuleIds={selectedIds}
                browseTab={browseTab}
              />

              {browseTab === 'community' ? (
                <CommunityModulesBrowser
                  selectedIds={selectedIds}
                  onAdd={addCommunityModule}
                  disabled={busy || !canBuild}
                />
              ) : (
                <div className="rounded-xl border border-blue-100 bg-slate-50/80 p-4">
                  <input
                    type="search"
                    placeholder="Search curated modules..."
                    value={search}
                    onChange={(event) => setSearch(event.target.value)}
                    className="mb-4 block w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />

                  {aiChatOptions.length > 0 && (
                    <div className="mb-4">
                      <AiBotChattingGroup
                        options={aiChatOptions}
                        selectedId={selectedAiChatModuleId(selectedIds)}
                        onSelect={selectAiChat}
                        disabled={busy || !canBuild}
                        noneDescription="Playerbots keep their normal chatter. Changing this needs a worldserver recompile."
                      />
                    </div>
                  )}

                  {isLoading && (
                    <div className="flex items-center justify-center gap-2 py-8 text-sm text-slate-500">
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

                  {!isLoading && !isError && browsable.length === 0 && (
                    <p className="rounded-md border border-dashed border-slate-300 px-4 py-6 text-center text-sm text-slate-500">
                      {search
                        ? 'No available modules match your search.'
                        : 'All available modules are already installed.'}
                    </p>
                  )}

                  {!isLoading && !isError && browsable.length > 0 && (
                    <div className="space-y-4">
                      {customAvailable.length > 0 && (
                        <ModuleGroup
                          title="Your custom modules"
                          modules={customAvailable}
                          emptyText="No custom modules available for this stack type."
                        >
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
                      )}

                      {builtInAvailable.length > 0 && (
                        <details open className="rounded-lg border border-slate-200 bg-white/70">
                          <summary className="cursor-pointer list-none px-4 py-3 text-sm font-medium text-slate-700 marker:content-none">
                            <span className="inline-flex items-center gap-2">
                              Show curated built-in modules
                              <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-normal text-slate-500">
                                {builtInAvailable.length}
                              </span>
                            </span>
                          </summary>
                          <div className="border-t border-slate-100 p-3">
                            <ModuleGroup
                              title="Built-in modules"
                              modules={builtInAvailable}
                              emptyText="No built-in modules match this filter."
                            >
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
                        </details>
                      )}
                    </div>
                  )}
                </div>
              )}

              {showCatalog && (
                <div className="mt-4 rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
                  <ModuleCatalogPage />
                </div>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

function InstalledModuleRow({
  module,
  disabled,
  lockReason,
  onRemove,
}: {
  module: ModuleDto
  disabled: boolean
  lockReason?: string | null
  onRemove: () => void
}) {
  const locked = !!lockReason

  return (
    <li>
      <details className="group rounded-lg border border-slate-200 bg-slate-50/50 open:bg-white open:shadow-sm">
        <summary className="flex cursor-pointer list-none items-center gap-3 px-3 py-2.5 marker:content-none sm:px-4">
          <ChevronDown className="h-4 w-4 shrink-0 text-slate-400 transition-transform group-open:rotate-180" />
          <div
            className={cn(
              'flex h-5 w-5 shrink-0 items-center justify-center rounded border',
              locked ? 'border-amber-500 bg-amber-500' : 'border-blue-600 bg-blue-600',
            )}
          >
            {locked ? <Lock className="h-3 w-3 text-white" /> : <Check className="h-3 w-3 text-white" />}
          </div>
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
              <span className="truncate text-sm font-medium text-slate-900">{module.name}</span>
              {module.recommended && <RecommendedBadge />}
              <span className="rounded-full bg-slate-200/80 px-2 py-0.5 text-[10px] font-medium text-slate-600">
                {module.isBuiltIn ? 'Built-in' : 'Custom'}
              </span>
            </div>
            <p className="truncate font-mono text-[11px] text-slate-400">{module.id}</p>
          </div>
          <button
            type="button"
            onClick={(event) => {
              event.preventDefault()
              onRemove()
            }}
            disabled={disabled}
            className="shrink-0 rounded-md border border-red-200 bg-white px-3 py-1.5 text-xs font-medium text-red-700 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-50"
          >
            Remove
          </button>
        </summary>
        <div className="border-t border-slate-100 px-4 pb-4 pt-3 pl-11 sm:pl-12">
          <p className="text-sm text-slate-600">{module.description || 'No description provided.'}</p>
          {lockReason && <p className="mt-2 text-xs text-amber-700">{lockReason}</p>}
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
      </details>
    </li>
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
    <div className={`rounded-lg border p-4 ${selected ? 'border-blue-200 bg-blue-50/80' : 'border-slate-200 bg-white'}`}>
      <div className="flex items-start gap-3">
        <div
          className={`mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded border ${selected ? 'border-blue-600 bg-blue-600' : 'border-gray-300'}`}
        >
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

function RecommendedBadge() {
  return (
    <span className="rounded-full bg-sky-50 px-2 py-0.5 text-[11px] font-medium text-sky-700 ring-1 ring-inset ring-sky-200">
      Recommended
    </span>
  )
}
