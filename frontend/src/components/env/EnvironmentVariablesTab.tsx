import { useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Loader2, Save, Search, Variable } from 'lucide-react'
import type { StackDetailsDto } from '@/types/stack.types'
import type { ServiceEnvVars } from '@/types/serviceEnv'
import { stackApi } from '@/services/api'
import { stackKeys } from '@/hooks/useStacks'
import { useServiceEnvTemplates } from '@/hooks/useModules'
import { ServiceEnvVarsEditor } from '@/components/wizard/ServiceEnvVarsEditor'
import {
  StackTabHeader,
  StackTabInfoDetails,
  StackTabPanel,
  StackTabPanelHeader,
  StackTabSideCard,
} from '@/components/layout/StackTabChrome'

const WORLDSERVER = 'worldserver'

interface EnvironmentVariablesTabProps {
  stack: StackDetailsDto
}

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
    () => Object.values(serviceEnvVars).reduce((sum, bucket) => sum + Object.keys(bucket ?? {}).length, 0),
    [serviceEnvVars],
  )

  const serviceCount = useMemo(
    () => Object.keys(serviceEnvVars).filter((key) => Object.keys(serviceEnvVars[key] ?? {}).length > 0).length,
    [serviceEnvVars],
  )

  const saveMutation = useMutation({
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
    <div className="space-y-5">
      <StackTabHeader
        title="Environment variables"
        subtitle="Per-container environment overrides. Changes apply after the affected container restarts."
      />

      <div className="grid gap-4 lg:grid-cols-5">
        <StackTabSideCard
          className="lg:col-span-3"
          title="Search variables"
          description="Filter by name or description across all containers."
          icon={<Search className="h-5 w-5" />}
          variant="light"
        >
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input
              type="search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search variables (name, description)…"
              className="w-full rounded-lg border border-slate-300 bg-white py-2.5 pl-9 pr-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>
          <p className="mt-3 text-sm text-slate-600">
            <strong className="text-slate-900">{setCount}</strong> variable{setCount === 1 ? '' : 's'} set across{' '}
            <strong className="text-slate-900">{serviceCount || 'no'}</strong> service
            {serviceCount === 1 ? '' : 's'}.
          </p>
        </StackTabSideCard>

        <StackTabSideCard
          className="lg:col-span-2"
          title="Save changes"
          description={dirty ? 'You have unsaved environment changes.' : 'All changes are saved.'}
          icon={<Save className="h-5 w-5" />}
        >
          <button
            type="button"
            onClick={() => saveMutation.mutate()}
            disabled={!dirty || saveMutation.isPending}
            className="inline-flex w-full items-center justify-center gap-2 rounded-lg bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {saveMutation.isPending ? (
              <>
                <Loader2 className="h-4 w-4 animate-spin" />
                Saving…
              </>
            ) : (
              <>
                <Save className="h-4 w-4" />
                Save changes
              </>
            )}
          </button>
          {saveMutation.isError && (
            <p className="mt-3 text-sm text-red-300">Failed to save. Please try again.</p>
          )}
        </StackTabSideCard>
      </div>

      <StackTabInfoDetails summary="When changes take effect">
        Environment variable updates are written to the stack configuration immediately on save, but running
        containers keep their previous values until you restart the affected service or the whole stack.
      </StackTabInfoDetails>

      <StackTabPanel>
        <StackTabPanelHeader
          title="Container variables"
          subtitle="Expand a service to edit its template options or add custom keys."
          actions={<Variable className="h-4 w-4 text-slate-400" aria-hidden="true" />}
        />

        <div className="px-4 py-4 sm:px-5">
          {templatesLoading ? (
            <div className="flex items-center justify-center gap-2 py-10 text-sm text-slate-500">
              <Loader2 className="h-4 w-4 animate-spin" />
              Loading templates…
            </div>
          ) : (
            <ServiceEnvVarsEditor
              templates={envTemplates ?? []}
              value={serviceEnvVars}
              onChange={setServiceEnvVars}
              filter={search}
            />
          )}
        </div>
      </StackTabPanel>
    </div>
  )
}
