import { useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Loader2, Save, Search } from 'lucide-react'
import type { StackDetailsDto } from '@/types/stack.types'
import type { ServiceEnvVars } from '@/types/serviceEnv'
import { stackApi } from '@/services/api'
import { stackKeys } from '@/hooks/useStacks'
import { useServiceEnvTemplates } from '@/hooks/useModules'
import { ServiceEnvVarsEditor } from '@/components/wizard/ServiceEnvVarsEditor'

// The worldserver bucket doubles as the legacy flat customEnvVars mirror the backend still reads.
const WORLDSERVER = 'worldserver'

interface EnvironmentVariablesTabProps {
  stack: StackDetailsDto
}

/**
 * Editable, searchable overview of every container's environment variables. Reuses the per-service
 * env-var model (template options + custom escape hatch) as a collapsible list, one section per
 * container, with a search box that narrows to matching variables across all services.
 */
export default function EnvironmentVariablesTab({ stack }: EnvironmentVariablesTabProps) {
  const queryClient = useQueryClient()
  const { data: envTemplates, isLoading: templatesLoading } = useServiceEnvTemplates()

  const initial = useMemo<ServiceEnvVars>(
    () => stack.configuration.advanced.serviceEnvVars ?? {},
    [stack.configuration.advanced.serviceEnvVars],
  )

  const [serviceEnvVars, setServiceEnvVars] = useState<ServiceEnvVars>(initial)
  const [search, setSearch] = useState('')

  const dirty = useMemo(
    () => JSON.stringify(serviceEnvVars) !== JSON.stringify(initial),
    [serviceEnvVars, initial],
  )

  const setCount = useMemo(
    () =>
      Object.values(serviceEnvVars).reduce(
        (sum, bucket) => sum + Object.keys(bucket ?? {}).length,
        0,
      ),
    [serviceEnvVars],
  )

  const saveMutation = useMutation({
    // Keep the legacy flat mirror in sync with the worldserver bucket so the backend never re-seeds
    // cleared worldserver vars from a stale customEnvVars value (matches EditStackConfigModal).
    mutationFn: () =>
      stackApi.updateConfig(stack.stackId, {
        ...stack.configuration,
        advanced: {
          ...stack.configuration.advanced,
          serviceEnvVars,
          customEnvVars: serviceEnvVars[WORLDSERVER] ?? {},
        },
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stack.stackId) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    },
  })

  return (
    <div className="mb-8">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-xl font-semibold">Environment Variables</h2>
          <p className="mt-1 text-sm text-gray-500">
            Per-container environment variables. Changes take effect after the affected container restarts.
          </p>
        </div>
        <button
          onClick={() => saveMutation.mutate()}
          disabled={!dirty || saveMutation.isPending}
          className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {saveMutation.isPending ? (
            <>
              <Loader2 className="h-4 w-4 animate-spin" />
              Saving...
            </>
          ) : (
            <>
              <Save className="h-4 w-4" />
              Save Changes
            </>
          )}
        </button>
      </div>

      <div className="rounded-lg border border-gray-200 bg-white p-4">
        <div className="mb-4 flex items-center justify-between gap-3">
          <div className="relative flex-1 max-w-md">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
            <input
              type="search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search variables (name, description)..."
              className="w-full rounded-md border border-gray-300 py-2 pl-9 pr-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>
          <span className="shrink-0 text-xs text-gray-500">{setCount} set</span>
        </div>

        {templatesLoading ? (
          <div className="flex items-center justify-center gap-2 py-8 text-sm text-gray-500">
            <Loader2 className="h-4 w-4 animate-spin" /> Loading templates...
          </div>
        ) : (
          <ServiceEnvVarsEditor
            templates={envTemplates ?? []}
            value={serviceEnvVars}
            onChange={setServiceEnvVars}
            filter={search}
          />
        )}

        {saveMutation.isError && (
          <p className="mt-3 text-sm text-red-600">
            Failed to save environment variables. Please try again.
          </p>
        )}
      </div>
    </div>
  )
}
