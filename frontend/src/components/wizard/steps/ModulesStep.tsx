import { useEffect, useState } from 'react'
import { AlertCircle, Check, ExternalLink, Loader2, Lock, Package } from 'lucide-react'
import type { WizardForm } from '@/types/wizard.types'
import CommunityModulesBrowser from '@/components/modules/CommunityModulesBrowser'
import ModuleBrowseTabs, { type ModuleBrowseTab } from '@/components/modules/ModuleBrowseTabs'
import { useCommunityModules, useModules, useServerTypes } from '@/hooks/useModules'
import AiBotChattingGroup from '@/components/modules/AiBotChattingGroup'
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
import { ServerTypeSlot } from '@/server-types'
import { mergeModuleEnvDefaults } from '@/setup/steps/modules/envDefaults'
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
  const [browseTab, setBrowseTab] = useState<ModuleBrowseTab>('curated')

  const { data: modules, isLoading, isError } = useModules(serverType)
  const { data: serverTypes } = useServerTypes()
  const { data: communityPreview } = useCommunityModules({
    page: 1,
    pageSize: 1,
    enabled: browseTab === 'community',
  })

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

  // AI chat modules are mutually exclusive, so they are lifted out of the flat list into their own
  // single-select group rather than rendered as three independently checkable rows.
  const aiChatOptions = toAiChatOptions(modules ?? [])
  const curated = filtered.filter((module) => !isAiChatModuleId(module.id))
  const recommendedModules = curated.filter((module) => module.recommended)
  const otherCuratedModules = curated.filter((module) => !module.recommended)

  const selectAiChat = (id: string | null) => {
    setValue('moduleIds', selectAiChatModule(selectedIds, id, modules ?? []), { shouldDirty: true })
    if (!id) {
      return
    }

    const services = (form.getValues('advanced.serviceEnvVars') as Record<string, Record<string, string>>) ?? {}
    const merged = mergeModuleEnvDefaults(id, services)
    if (merged !== services) {
      form.setValue('advanced.serviceEnvVars', merged, { shouldDirty: true })
    }
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

    if (!isRemoving) {
      const services = (form.getValues('advanced.serviceEnvVars') as Record<string, Record<string, string>>) ?? {}
      const merged = mergeModuleEnvDefaults(id, services)
      if (merged !== services) {
        form.setValue('advanced.serviceEnvVars', merged, { shouldDirty: true })
      }
    }
  }

  const addCommunityModule = (moduleId: string) => {
    if (selectedIds.includes(moduleId)) {
      return
    }

    const next = applyModuleToggle(moduleId, selectedIds, modules ?? []) ?? [...selectedIds, moduleId]
    setValue('moduleIds', next, { shouldDirty: true })

    const services = (form.getValues('advanced.serviceEnvVars') as Record<string, Record<string, string>>) ?? {}
    const merged = mergeModuleEnvDefaults(moduleId, services)
    if (merged !== services) {
      form.setValue('advanced.serviceEnvVars', merged, { shouldDirty: true })
    }
  }

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Modules</h2>
        <p className="mt-1 text-sm text-gray-500">
          Pick curated platform modules or browse the wider AzerothCore community catalogue.
        </p>
      </div>

      {selectedIds.length > 0 && (
        <div className="rounded-lg border border-blue-200 bg-blue-50/70 px-4 py-3 text-sm text-blue-900">
          <span className="font-medium">{selectedIds.length}</span> module{selectedIds.length !== 1 ? 's' : ''}{' '}
          selected for this stack.
        </div>
      )}

      <ModuleBrowseTabs
        active={browseTab}
        onChange={setBrowseTab}
        curatedCount={modules?.length}
        communityCount={communityPreview?.total}
      />

      <ServerTypeSlot serverType={serverType} selectedModuleIds={selectedIds} browseTab={browseTab} />

      {browseTab === 'community' ? (
        <CommunityModulesBrowser selectedIds={selectedIds} onAdd={addCommunityModule} />
      ) : (
        <div className="rounded-xl border border-blue-100 bg-slate-50/80 p-4">
          <div className="mb-3 flex flex-wrap items-end justify-between gap-3">
            <div>
              <p className="text-sm font-medium text-slate-800">Curated modules</p>
              <p className="text-xs text-slate-500">
                Tested and filtered for your server type ({modules?.length ?? 0} available).
              </p>
            </div>
            <input
              type="search"
              placeholder="Search curated modules…"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              className="block w-full max-w-xs rounded-md border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 focus:outline-none focus:ring-2 focus:ring-blue-500"
              aria-label="Search curated modules"
            />
          </div>

          {aiChatOptions.length > 0 && (
            <div className="mb-4">
              <AiBotChattingGroup
                options={aiChatOptions}
                selectedId={selectedAiChatModuleId(selectedIds)}
                onSelect={selectAiChat}
              />
            </div>
          )}

          {isLoading && (
            <div className="flex items-center justify-center gap-2 py-8 text-sm text-slate-500">
              <Loader2 className="h-5 w-5 animate-spin" aria-hidden="true" />
              Loading modules…
            </div>
          )}

          {isError && (
            <div className="flex items-center gap-2 rounded-md border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800">
              <AlertCircle className="h-4 w-4 shrink-0" aria-hidden="true" />
              <span>
                Modules could not be loaded. You can continue - the module list will be available once the backend is running.
              </span>
            </div>
          )}

          {!isLoading && !isError && curated.length === 0 && (
            <p className="py-6 text-center text-sm text-slate-400">
              {search ? 'No modules match your search.' : 'No modules available.'}
            </p>
          )}

          {!isLoading && !isError && curated.length > 0 && (
            <div className="space-y-4">
              {recommendedModules.length > 0 && (
                <CuratedModuleSection
                  title="Recommended"
                  items={recommendedModules}
                  selectedIds={selectedIds}
                  serverType={serverType}
                  serverTypes={serverTypes}
                  catalogModules={modules ?? []}
                  onToggle={toggle}
                />
              )}

              {otherCuratedModules.length > 0 &&
                (recommendedModules.length > 0 ? (
                  <details className="group rounded-lg border border-slate-200 bg-white/70">
                    <summary className="cursor-pointer list-none px-4 py-3 text-sm font-medium text-slate-700 marker:content-none">
                      <span className="inline-flex items-center gap-2">
                        Show all curated modules
                        <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-normal text-slate-500">
                          {otherCuratedModules.length}
                        </span>
                      </span>
                    </summary>
                    <div className="border-t border-slate-100 px-2 pb-2 pt-1">
                      <CuratedModuleSection
                        title="All curated"
                        items={otherCuratedModules}
                        compact
                        selectedIds={selectedIds}
                        serverType={serverType}
                        serverTypes={serverTypes}
                        catalogModules={modules ?? []}
                        onToggle={toggle}
                      />
                    </div>
                  </details>
                ) : (
                  <CuratedModuleSection
                    title="Curated modules"
                    items={otherCuratedModules}
                    selectedIds={selectedIds}
                    serverType={serverType}
                    serverTypes={serverTypes}
                    catalogModules={modules ?? []}
                    onToggle={toggle}
                  />
                ))}
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function CuratedModuleSection({
  title,
  items,
  compact = false,
  selectedIds,
  serverType,
  serverTypes,
  catalogModules,
  onToggle,
}: {
  title: string
  items: NonNullable<ReturnType<typeof useModules>['data']>
  compact?: boolean
  selectedIds: string[]
  serverType: ServerType
  serverTypes: ReturnType<typeof useServerTypes>['data']
  catalogModules: NonNullable<ReturnType<typeof useModules>['data']>
  onToggle: (id: string) => void
}) {
  if (items.length === 0) {
    return null
  }

  return (
    <section>
      {!compact && <h4 className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">{title}</h4>}
      <ul className="grid gap-2" role="list" aria-label={title}>
        {items.map((module) => {
          const isSelected = selectedIds.includes(module.id)
          const requiredByServerType = isServerTypeRequiredModule(module.id, serverType, serverTypes)
          const locked =
            (isSelected && requiredByServerType) ||
            isModuleLocked(module.id, selectedIds, catalogModules)
          const lockReason = requiredByServerType
            ? 'Required for this server type'
            : moduleLockReason(module.id, selectedIds, catalogModules)

          return (
            <li key={module.id} className="min-w-0">
              <button
                type="button"
                role="checkbox"
                aria-checked={isSelected}
                aria-disabled={locked}
                onClick={() => onToggle(module.id)}
                className={cn(
                  'flex w-full items-start gap-3 rounded-lg border p-3 text-left transition-colors focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-1',
                  isSelected
                    ? 'border-blue-500 bg-blue-50/80 shadow-sm'
                    : 'border-slate-200 bg-white hover:border-blue-200 hover:bg-blue-50/30',
                  locked && 'cursor-not-allowed opacity-90',
                )}
              >
                <div
                  className={cn(
                    'mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded border-2',
                    isSelected ? 'border-blue-600 bg-blue-600' : 'border-slate-300 bg-white',
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
                    <Package className="h-3.5 w-3.5 shrink-0 text-slate-400" aria-hidden="true" />
                    <span className="text-sm font-medium text-slate-900">{module.name}</span>
                    {module.recommended && <RecommendedBadge />}
                  </div>
                  <p className="mt-0.5 wrap-break-word text-xs text-slate-500">{module.description}</p>
                  {lockReason && <p className="mt-1 text-xs text-amber-700">{lockReason}</p>}
                </div>
                {module.repository && (
                  <a
                    href={module.repository}
                    target="_blank"
                    rel="noopener noreferrer"
                    onClick={(event) => event.stopPropagation()}
                    className="mt-0.5 shrink-0 text-slate-400 hover:text-blue-600"
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
    </section>
  )
}

function RecommendedBadge() {
  return (
    <span className="rounded-full bg-sky-50 px-2 py-0.5 text-[11px] font-medium text-sky-700 ring-1 ring-inset ring-sky-200">
      Recommended
    </span>
  )
}
