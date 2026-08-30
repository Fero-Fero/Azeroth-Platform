import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  AlertCircle,
  CheckCircle2,
  ExternalLink,
  GitBranch,
  GitFork,
  Hammer,
  Loader2,
  Plus,
  RefreshCw,
  Trash2,
} from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import {
  useModuleExtraDataChoices,
  useSaveModuleExtraDataChoices,
} from '@/hooks/useModuleExtraData'
import { useModules, useRepositoryBranches, useServerTypes } from '@/hooks/useModules'
import { stackKeys } from '@/hooks/useStacks'
import { applyModuleToggle, isModuleLocked } from '@/lib/module-dependencies'
import {
  isServerTypeRequiredModule,
  mergeRequiredModuleIds,
  sortModulesForDisplay,
} from '@/lib/server-type-modules'
import { mergeModuleEnvDefaults } from '@/setup/steps/modules/envDefaults'
import { apiErrorMessage } from '@/lib/utils'
import { buildApi, stackApi } from '@/services/api'
import { MODULE_IDS } from '@/setup/constants'
import {
  ModuleContentChoicesForm,
  buildApplyRequest,
  defaultSelectionsByModule,
} from '@/setup/steps/modules/moduleContentChoices'
import type { IpContentMode, ModuleInstallSelections } from '@/types/module-extra-data.types'
import {
  BuildPhase,
  ServerType,
  StackStatus,
  type ModuleCheckItemDto,
  type ModuleDto,
  type StackDetailsDto,
  type SyncStackModuleItemDto,
} from '@/types/stack.types'

interface InitialBuildRequiredPanelProps {
  stack: StackDetailsDto
  stackId: string
  onRetryBuild: (options?: { skipModuleCheck?: boolean }) => void
  isRetrying: boolean
  onDelete: () => void
  isDeleting: boolean
}

export default function InitialBuildRequiredPanel({
  stack,
  stackId,
  onRetryBuild,
  isRetrying,
  onDelete,
  isDeleting,
}: InitialBuildRequiredPanelProps) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const isExpress = stack.configuration.serverType === ServerType.Express
  const hasIp = !isExpress && (stack.configuration.moduleIds?.includes(MODULE_IDS.individualProgression) ?? false)
  const choicesQuery = useModuleExtraDataChoices(stackId, stack.status !== StackStatus.Building)
  const saveChoices = useSaveModuleExtraDataChoices(stackId)
  const modules = choicesQuery.data?.modules ?? []
  const saved = choicesQuery.data?.saved
  const moduleIds = stack.configuration.moduleIds ?? []
  const catalogQuery = useModules(stack.configuration.serverType)
  const serverTypesQuery = useServerTypes()
  const catalogById = useMemo(
    () => new Map((catalogQuery.data ?? []).map((module) => [module.id, module])),
    [catalogQuery.data],
  )
  const catalogModules = catalogQuery.data ?? []

  const [ipContentMode, setIpContentMode] = useState<IpContentMode>('Unset')
  const [byModule, setByModule] = useState<Record<string, ModuleInstallSelections>>({})
  const [syncResults, setSyncResults] = useState<Record<string, SyncStackModuleItemDto>>({})
  const [branchEditorId, setBranchEditorId] = useState<string | null>(null)
  const [addPickerOpen, setAddPickerOpen] = useState(true)
  const [addSearch, setAddSearch] = useState('')

  const defaultByModule = useMemo(() => defaultSelectionsByModule(modules), [modules])

  useEffect(() => {
    if (!choicesQuery.data) return
    setIpContentMode(
      isExpress
        ? 'ServerWideProgression'
        : saved?.ipContentMode && saved.ipContentMode !== 'Unset'
          ? saved.ipContentMode
          : hasIp
            ? 'Standard'
            : 'Unset',
    )
    setByModule(
      Object.keys(saved?.selectionsByModuleId ?? {}).length > 0
        ? saved!.selectionsByModuleId
        : defaultByModule,
    )
  }, [choicesQuery.data, defaultByModule, hasIp, isExpress, saved])

  const buildStatusQuery = useQuery({
    queryKey: ['build-status', stackId],
    queryFn: async () => (await buildApi.status(stackId)).data,
    enabled: true,
    retry: false,
    refetchInterval: stack.status === StackStatus.Building ? 4000 : false,
  })

  const syncModules = useMutation({
    mutationFn: async (moduleId?: string) => (await buildApi.syncModules(stackId, moduleId)).data,
    onSuccess: (result, moduleId) => {
      setSyncResults((prev) => {
        const next = moduleId ? { ...prev } : {}
        for (const item of result.items) {
          next[item.moduleId] = item
        }
        return next
      })
      void queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
      const failed = result.items.filter((item) => !item.ok)
      const updated = result.items.filter((item) => item.ok && !item.skipped)
      if (failed.length > 0) {
        toast.error(
          failed.length === 1
            ? failed[0].message
            : `${failed.length} module(s) failed to update. See the list below.`,
        )
      } else if (updated.length > 0) {
        toast.success(
          updated.length === 1
            ? updated[0].message
            : `Updated ${updated.length} module(s) from GitHub.`,
        )
      } else {
        toast.message('No git modules needed an update.')
      }
    },
    onError: (error) => {
      toast.error(apiErrorMessage(error, 'The module update timed out or could not reach GitHub.'))
    },
  })

  const checkModules = useMutation({
    mutationFn: async () => {
      persistChoices()
      return (await buildApi.checkModules(stackId)).data
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
      void queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
      navigate(`/stacks/${stackId}/build`)
    },
    onError: (error) => {
      toast.error(apiErrorMessage(error, 'Could not start the module compile check.'))
    },
  })

  const updateConfig = useMutation({
    mutationFn: (next: StackDetailsDto['configuration']) => stackApi.updateConfig(stackId, next),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
    },
    onError: (error) => {
      toast.error(apiErrorMessage(error, 'Could not update the stack configuration.'))
    },
  })

  const buildStatus = buildStatusQuery.data
  const checkPassed = stack.moduleCheck?.passed === true
  const checkSkipped = stack.moduleCheck?.skipped === true
  const lastFailed = buildStatus?.currentPhase === BuildPhase.Failed
  const errorMessage =
    (lastFailed ? buildStatus?.errorMessage : null) ??
    (buildStatusQuery.isError && !checkPassed
      ? 'No build record found - the initial build may not have started.'
      : null)
  const checkItemsById = useMemo(() => {
    const map = new Map<string, ModuleCheckItemDto>()
    for (const item of stack.moduleCheck?.items ?? buildStatus?.moduleResults ?? []) {
      map.set(item.moduleId, item)
    }
    return map
  }, [buildStatus?.moduleResults, stack.moduleCheck?.items])
  const hasPerModuleErrors = [...checkItemsById.values()].some((item) => Boolean(item.error))

  const persistChoices = () =>
    saveChoices.mutate(
      buildApplyRequest(hasIp && ipContentMode === 'Unset' ? 'Standard' : ipContentMode, byModule),
    )

  const busy =
    isRetrying ||
    isDeleting ||
    syncModules.isPending ||
    checkModules.isPending ||
    updateConfig.isPending
  const gitModuleCount = moduleIds.filter((id) => catalogById.get(id)?.sourceType !== 'package').length
  const availableCatalogModules = useMemo(() => {
    const selected = new Set(moduleIds)
    const query = addSearch.trim().toLowerCase()
    const matches = catalogModules.filter((module) => {
      if (selected.has(module.id) || module.sourceType === 'package') {
        return false
      }
      if (!query) {
        return true
      }
      return (
        module.name.toLowerCase().includes(query) ||
        module.id.toLowerCase().includes(query) ||
        module.description.toLowerCase().includes(query)
      )
    })
    return sortModulesForDisplay(matches, {
      serverType: stack.configuration.serverType,
      serverTypes: serverTypesQuery.data,
      selectedIds: moduleIds,
    })
  }, [addSearch, catalogModules, moduleIds, serverTypesQuery.data, stack.configuration.serverType])

  const persistModuleIds = async (nextIds: string[]) => {
    const merged = mergeRequiredModuleIds(
      nextIds,
      stack.configuration.serverType,
      serverTypesQuery.data,
    )
    let env = stack.configuration.advanced.serviceEnvVars ?? {}
    for (const id of merged) {
      if (!moduleIds.includes(id)) {
        env = mergeModuleEnvDefaults(id, env)
      }
    }
    const dropped = moduleIds.filter((id) => !merged.includes(id))
    const nextBranches = { ...(stack.configuration.moduleBranches ?? {}) }
    for (const id of dropped) {
      delete nextBranches[id]
    }
    await updateConfig.mutateAsync({
      ...stack.configuration,
      moduleIds: merged,
      moduleBranches: nextBranches,
      advanced: {
        ...stack.configuration.advanced,
        serviceEnvVars: env,
      },
    })
    return { merged, dropped }
  }

  const addFromCatalog = async (moduleId: string) => {
    const nextIds = applyModuleToggle(moduleId, moduleIds, catalogModules)
    if (!nextIds) {
      return
    }
    try {
      const { merged, dropped } = await persistModuleIds(nextIds)
      const added = merged.filter((id) => !moduleIds.includes(id))
      const names = added.map((id) => catalogById.get(id)?.name ?? id)
      toast.success(
        names.length === 1 ? `Added ${names[0]}.` : `Added ${names.join(', ')}.`,
      )
      if (dropped.length > 0) {
        toast.message(
          `Removed conflicting module${dropped.length === 1 ? '' : 's'}: ${dropped
            .map((id) => catalogById.get(id)?.name ?? id)
            .join(', ')}.`,
        )
      }
      setAddSearch('')
    } catch {
      // updateConfig onError already toasted a string message.
    }
  }

  const removeInstalledModule = async (moduleId: string) => {
    const nextIds =
      applyModuleToggle(moduleId, moduleIds, catalogModules) ??
      moduleIds.filter((id) => id !== moduleId)
    try {
      await persistModuleIds(nextIds)
    } catch {
      // updateConfig onError already toasted a string message.
    }
  }
  const checkingNow =
    stack.status === StackStatus.Building &&
    (buildStatus?.currentPhase === BuildPhase.CheckingModules ||
      buildStatus?.currentPhase === BuildPhase.Cloning ||
      buildStatus?.currentPhase === BuildPhase.PreparingModules)

  if (stack.status === StackStatus.Building) {
    return (
      <div className="mx-auto max-w-2xl mt-12">
        <div className="rounded-xl border border-blue-200 bg-blue-50 p-8 text-center">
          <Loader2 className="mx-auto h-10 w-10 animate-spin text-blue-600" />
          <h2 className="mt-4 text-xl font-semibold text-gray-900">
            {checkingNow ? 'Checking modules' : 'Initial build in progress'}
          </h2>
          <p className="mt-2 text-sm text-gray-600">
            {checkingNow
              ? 'The first check compiles core libraries, selected modules, then links worldserver so missing symbols between modules are caught. This can take a while; later checks are much faster. It is still shorter than a failed full image build.'
              : 'Docker images are being compiled. This usually takes 15–30 minutes.'}
          </p>
          <button
            type="button"
            onClick={() => navigate(`/stacks/${stackId}/build`)}
            className="mt-6 inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
          >
            View build progress
          </button>
        </div>
      </div>
    )
  }

  return (
    <div className="mx-auto max-w-2xl mt-12">
      <button
        type="button"
        onClick={() => navigate('/stacks')}
        className="mb-4 text-sm text-gray-600 hover:text-gray-800"
      >
        ← Back to Stacks
      </button>

      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-100 bg-gray-50 px-6 py-5">
          <h1 className="text-2xl font-bold text-gray-900">{stack.stackName}</h1>
          <p className="mt-1 text-sm text-gray-500">
            Created {new Date(stack.createdAt).toLocaleDateString()} • {stack.serverType}
          </p>
        </div>

        <div className="space-y-5 px-6 py-6">
          <div
            className={`rounded-lg border p-4 ${
              lastFailed
                ? 'border-red-200 bg-red-50'
                : checkPassed
                  ? 'border-green-200 bg-green-50'
                  : 'border-amber-200 bg-amber-50'
            }`}
          >
            <div className="flex gap-3">
              {checkPassed && !lastFailed ? (
                <CheckCircle2 className="h-5 w-5 shrink-0 text-green-600" />
              ) : (
                <AlertCircle
                  className={`h-5 w-5 shrink-0 ${lastFailed ? 'text-red-600' : 'text-amber-600'}`}
                />
              )}
              <div>
                <h2
                  className={`font-semibold ${
                    lastFailed ? 'text-red-900' : checkPassed ? 'text-green-900' : 'text-amber-900'
                  }`}
                >
                  {lastFailed
                    ? checkPassed
                      ? 'Docker image build failed'
                      : 'Module check failed'
                    : checkPassed
                      ? 'Modules compiled'
                      : checkSkipped
                        ? 'Module check skipped'
                        : 'Setup not complete'}
                </h2>
                <p
                  className={`mt-1 text-sm ${
                    lastFailed ? 'text-red-800' : checkPassed ? 'text-green-800' : 'text-amber-800'
                  }`}
                >
                  {lastFailed
                    ? checkPassed
                      ? 'The Docker image build did not finish. You can retry it, or re-check modules first.'
                      : 'A selected module did not compile. Pull the latest commit, switch branch, or remove it, then re-check.'
                    : checkPassed
                      ? 'Every selected module compiled against this core. You can now build Docker images.'
                      : checkSkipped
                        ? 'The compile check was skipped. You can still run it later, or continue with the Docker image build.'
                        : 'Optionally check that selected modules compile before the 15–30 minute Docker image build. You can skip the check and build images directly.'}
                </p>
                {errorMessage && !hasPerModuleErrors && (
                  <p className="mt-2 rounded-md bg-white/70 px-3 py-2 font-mono text-xs text-gray-800">
                    {errorMessage}
                  </p>
                )}
              </div>
            </div>
          </div>

          {buildStatus?.recentLogs && buildStatus.recentLogs.length > 0 && (
            <div>
              <h3 className="mb-2 text-sm font-medium text-gray-700">Recent build logs</h3>
              <div className="max-h-48 overflow-y-auto rounded-lg bg-gray-900 p-3 font-mono text-xs text-green-400">
                {buildStatus.recentLogs.slice(-12).map((line, index) => (
                  <div key={index}>{line}</div>
                ))}
              </div>
            </div>
          )}

          <div className="space-y-3 rounded-lg border border-gray-200 p-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h3 className="text-sm font-medium text-gray-800">Installed modules</h3>
                <p className="mt-1 text-xs text-gray-600">
                  Add a catalog module, pull, change branch, or remove one that failed to compile, then
                  re-check. These actions do not start a Docker image build.
                </p>
              </div>
              <div className="flex flex-wrap gap-2">
                <button
                  type="button"
                  onClick={() => setAddPickerOpen((open) => !open)}
                  disabled={busy}
                  className="inline-flex items-center gap-2 rounded-lg border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-800 hover:bg-gray-50 disabled:opacity-50"
                >
                  <Plus className="h-4 w-4" />
                  {addPickerOpen ? 'Hide catalog' : 'Add from catalog'}
                </button>
                {gitModuleCount > 0 && (
                  <button
                    type="button"
                    onClick={() => syncModules.mutate(undefined)}
                    disabled={busy}
                    className="inline-flex items-center gap-2 rounded-lg border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-800 hover:bg-gray-50 disabled:opacity-50"
                  >
                    {syncModules.isPending && !syncModules.variables ? (
                      <>
                        <Loader2 className="h-4 w-4 animate-spin" />
                        Updating…
                      </>
                    ) : (
                      <>
                        <RefreshCw className="h-4 w-4" />
                        Update all from GitHub
                      </>
                    )}
                  </button>
                )}
              </div>
            </div>

            {catalogQuery.isLoading ? (
              <p className="text-sm text-gray-500">Loading modules…</p>
            ) : moduleIds.length === 0 ? (
              <p className="text-sm text-gray-500">No modules are selected on this stack.</p>
            ) : (
              <ul className="divide-y divide-gray-100 rounded-lg border border-gray-100">
                {moduleIds.map((id) => {
                  const catalog = catalogById.get(id) ?? missingModule(id)
                  const missingFromCatalog = !catalogById.has(id)
                  const result = syncResults[id]
                  const checkItem = checkItemsById.get(id)
                  const isPackage = catalog.sourceType === 'package'
                  const pullingThis = syncModules.isPending && syncModules.variables === id
                  const required =
                    !missingFromCatalog &&
                    (isServerTypeRequiredModule(
                      id,
                      stack.configuration.serverType,
                      serverTypesQuery.data,
                    ) || isModuleLocked(id, moduleIds, catalogModules))
                  const effectiveBranch =
                    stack.configuration.moduleBranches?.[id] || catalog.branch || 'master'
                  return (
                    <li key={id} className="space-y-2 px-3 py-3">
                      <div className="flex flex-wrap items-start justify-between gap-3">
                        <div className="min-w-0">
                          <div className="flex flex-wrap items-center gap-2">
                            <p className="text-sm font-medium text-gray-900">{catalog.name}</p>
                            <ModuleStatusBadge item={checkItem} checked={checkPassed || lastFailed} />
                          </div>
                          <p className="mt-0.5 font-mono text-xs text-gray-500">
                            {id}
                            {effectiveBranch ? ` · ${effectiveBranch}` : ''}
                            {isPackage ? ' · uploaded package' : ''}
                          </p>
                          {catalog.repository && (
                            <a
                              href={catalog.repository}
                              target="_blank"
                              rel="noreferrer"
                              className="mt-1 inline-flex items-center gap-1 text-xs text-blue-700 hover:underline"
                            >
                              <GitFork className="h-3 w-3" />
                              {githubRepoLabel(catalog.repository)}
                              <ExternalLink className="h-3 w-3" />
                            </a>
                          )}
                          {checkItem?.error && (
                            <pre className="mt-2 max-h-28 overflow-auto whitespace-pre-wrap rounded-md bg-red-50 px-2 py-1 font-mono text-[11px] text-red-800">
                              {checkItem.error}
                            </pre>
                          )}
                          {result && (
                            <p className={`mt-1 text-xs ${result.ok ? 'text-green-700' : 'text-red-700'}`}>
                              {result.message}
                            </p>
                          )}
                        </div>
                        {isPackage && !missingFromCatalog ? (
                          <span className="shrink-0 rounded-full bg-gray-100 px-2 py-0.5 text-[11px] font-medium text-gray-600">
                            Local package
                          </span>
                        ) : missingFromCatalog ? (
                          <button
                            type="button"
                            onClick={() => void removeInstalledModule(id)}
                            disabled={busy}
                            className="inline-flex items-center gap-1.5 rounded-lg border border-red-200 bg-white px-2.5 py-1 text-xs font-medium text-red-700 hover:bg-red-50 disabled:opacity-50"
                          >
                            Remove
                          </button>
                        ) : (
                          <div className="flex shrink-0 flex-wrap gap-1.5">
                            <button
                              type="button"
                              onClick={() => syncModules.mutate(id)}
                              disabled={busy}
                              className="inline-flex items-center gap-1.5 rounded-lg border border-gray-300 bg-white px-2.5 py-1 text-xs font-medium text-gray-800 hover:bg-gray-50 disabled:opacity-50"
                            >
                              {pullingThis ? (
                                <>
                                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                                  Pulling…
                                </>
                              ) : (
                                'Pull latest'
                              )}
                            </button>
                            <button
                              type="button"
                              onClick={() =>
                                setBranchEditorId((current) => (current === id ? null : id))
                              }
                              disabled={busy}
                              className="inline-flex items-center gap-1.5 rounded-lg border border-gray-300 bg-white px-2.5 py-1 text-xs font-medium text-gray-800 hover:bg-gray-50 disabled:opacity-50"
                            >
                              <GitBranch className="h-3.5 w-3.5" />
                              Branch
                            </button>
                            {!required && (
                              <button
                                type="button"
                                onClick={() => void removeInstalledModule(id)}
                                disabled={busy}
                                className="inline-flex items-center gap-1.5 rounded-lg border border-red-200 bg-white px-2.5 py-1 text-xs font-medium text-red-700 hover:bg-red-50 disabled:opacity-50"
                              >
                                Remove
                              </button>
                            )}
                          </div>
                        )}
                      </div>
                      {branchEditorId === id && catalog.repository && (
                        <ModuleBranchPicker
                          repositoryUrl={catalog.repository}
                          currentBranch={effectiveBranch}
                          disabled={busy}
                          onSelect={async (branch) => {
                            const nextBranches = {
                              ...(stack.configuration.moduleBranches ?? {}),
                              [id]: branch,
                            }
                            await updateConfig.mutateAsync({
                              ...stack.configuration,
                              moduleBranches: nextBranches,
                            })
                            setBranchEditorId(null)
                            syncModules.mutate(id)
                          }}
                        />
                      )}
                    </li>
                  )
                })}
              </ul>
            )}

            {addPickerOpen && (
              <div className="space-y-2 rounded-lg border border-blue-100 bg-slate-50 p-3">
                <p className="text-xs font-medium text-slate-700">Catalog modules for this server type</p>
                <input
                  type="search"
                  placeholder="Search catalog…"
                  value={addSearch}
                  onChange={(event) => setAddSearch(event.target.value)}
                  disabled={busy}
                  className="block w-full rounded-md border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-800 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
                />
                {availableCatalogModules.length === 0 ? (
                  <p className="text-xs text-slate-500">
                    {addSearch
                      ? 'No catalog modules match that search.'
                      : 'All catalog modules for this server type are already on the stack.'}
                  </p>
                ) : (
                  <ul className="max-h-64 divide-y divide-slate-100 overflow-y-auto rounded-md border border-slate-200 bg-white">
                    {availableCatalogModules.map((module) => (
                      <li key={module.id} className="flex items-start justify-between gap-3 px-3 py-2">
                        <div className="min-w-0">
                          <p className="text-sm font-medium text-slate-900">{module.name}</p>
                          <p className="truncate font-mono text-[11px] text-slate-400">{module.id}</p>
                          {module.description && (
                            <p className="mt-0.5 line-clamp-2 text-xs text-slate-500">{module.description}</p>
                          )}
                        </div>
                        <button
                          type="button"
                          onClick={() => void addFromCatalog(module.id)}
                          disabled={busy}
                          className="shrink-0 rounded-md bg-blue-600 px-2.5 py-1 text-xs font-medium text-white hover:bg-blue-700 disabled:opacity-50"
                        >
                          Add
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            )}
          </div>

          {(hasIp || modules.length > 0) && (
            <div className="space-y-3 rounded-lg border border-gray-200 p-4">
              <h3 className="text-sm font-medium text-gray-800">Module extra content</h3>
              <p className="text-xs text-gray-600">
                These choices are saved now and prepared after the build. They are applied later from Setup
                module content, after SOAP.
              </p>
              {choicesQuery.isLoading ? (
                <p className="text-sm text-gray-500">Loading extra-data options…</p>
              ) : (
                <ModuleContentChoicesForm
                  modules={modules}
                  hasIpModule={hasIp}
                  ipContentMode={ipContentMode === 'Unset' && hasIp ? 'Standard' : ipContentMode}
                  onIpContentModeChange={setIpContentMode}
                  byModule={Object.keys(byModule).length > 0 ? byModule : defaultByModule}
                  onChange={setByModule}
                />
              )}
              <button
                type="button"
                onClick={persistChoices}
                disabled={saveChoices.isPending || choicesQuery.isLoading}
                className="rounded-lg border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-800 hover:bg-gray-50 disabled:opacity-50"
              >
                {saveChoices.isPending ? 'Saving…' : saveChoices.isSuccess ? 'Choices saved' : 'Save choices'}
              </button>
            </div>
          )}

          <div className="flex flex-wrap gap-3">
            <button
              type="button"
              onClick={() => checkModules.mutate()}
              disabled={busy}
              className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
            >
              {checkModules.isPending ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Starting check…
                </>
              ) : (
                <>
                  <Hammer className="h-4 w-4" />
                  {lastFailed || checkPassed ? 'Re-check modules' : 'Check modules'}
                </>
              )}
            </button>
            <button
              type="button"
              onClick={() => {
                persistChoices()
                onRetryBuild({ skipModuleCheck: !checkPassed })
              }}
              disabled={busy}
              title={
                checkPassed
                  ? undefined
                  : 'Skip the compile check and start the Docker image build.'
              }
              className="inline-flex items-center gap-2 rounded-lg border border-blue-300 bg-white px-4 py-2 text-sm font-medium text-blue-800 hover:bg-blue-50 disabled:opacity-50"
            >
              {isRetrying ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Starting build…
                </>
              ) : checkPassed ? (
                'Build Docker images'
              ) : (
                'Skip check and build images'
              )}
            </button>
            <button
              type="button"
              onClick={onDelete}
              disabled={busy}
              className="inline-flex items-center gap-2 rounded-lg border border-red-300 px-4 py-2 text-sm font-medium text-red-700 hover:bg-red-50 disabled:opacity-50"
            >
              {isDeleting ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" />
                  Deleting…
                </>
              ) : (
                <>
                  <Trash2 className="h-4 w-4" />
                  Delete stack
                </>
              )}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}

function ModuleStatusBadge({
  item,
  checked,
}: {
  item: ModuleCheckItemDto | undefined
  checked: boolean
}) {
  const status = item?.status ?? (checked ? 'pending' : 'pending')
  const label =
    !item && !checked
      ? 'Not checked'
      : status === 'passed'
        ? 'Passed'
        : status === 'failed'
          ? 'Failed'
          : status === 'compiling'
            ? 'Compiling'
            : status === 'skipped'
              ? 'Skipped'
              : 'Not checked'
  const classes =
    status === 'passed'
      ? 'bg-green-100 text-green-800'
      : status === 'failed'
        ? 'bg-red-100 text-red-800'
        : status === 'compiling'
          ? 'bg-blue-100 text-blue-800'
          : 'bg-gray-100 text-gray-600'
  return (
    <span className={`rounded-full px-2 py-0.5 text-[11px] font-medium ${classes}`}>{label}</span>
  )
}

function ModuleBranchPicker({
  repositoryUrl,
  currentBranch,
  disabled,
  onSelect,
}: {
  repositoryUrl: string
  currentBranch: string
  disabled: boolean
  onSelect: (branch: string) => void | Promise<void>
}) {
  const branchesQuery = useRepositoryBranches(repositoryUrl, true)
  const branches = branchesQuery.data ?? []
  return (
    <div className="rounded-md border border-gray-200 bg-gray-50 p-2">
      {branchesQuery.isLoading ? (
        <p className="text-xs text-gray-500">Loading branches…</p>
      ) : branchesQuery.isError || branches.length === 0 ? (
        <p className="text-xs text-gray-500">Could not list branches for this repository.</p>
      ) : (
        <label className="flex items-center gap-2 text-xs text-gray-700">
          Branch
          <select
            className="rounded border border-gray-300 bg-white px-2 py-1 text-xs"
            value={branches.includes(currentBranch) ? currentBranch : branches[0]}
            disabled={disabled}
            onChange={(event) => void onSelect(event.target.value)}
          >
            {branches.map((branch) => (
              <option key={branch} value={branch}>
                {branch}
              </option>
            ))}
          </select>
        </label>
      )}
    </div>
  )
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

function githubRepoLabel(repository: string): string {
  try {
    const url = new URL(repository)
    return url.pathname.replace(/^\//, '').replace(/\.git$/, '') || url.host
  } catch {
    return repository
  }
}
