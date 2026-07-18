import { useEffect, useState } from 'react'
import { Bot, GitFork, Server, TrendingUp, Users, type LucideIcon } from 'lucide-react'
import { BranchCombobox } from '@/components/wizard/common/BranchCombobox'
import { FormField } from '@/components/wizard/common/FormField'
import type { WizardForm } from '@/components/wizard/types'
import { useRepositoryBranches, useServerTypes } from '@/hooks/useModules'
import { mergeRequiredModuleIds } from '@/lib/server-type-modules'
import { cn } from '@/lib/utils'
import { ServerType } from '@/types/stack.types'

interface ServerConfigStepProps {
  form: WizardForm
}

/** Pulls a human-friendly message out of the axios error returned by the branches endpoint. */
function branchErrorMessage(error: unknown): string | null {
  if (!error) return null
  const message = (error as { response?: { data?: { message?: string } } })?.response?.data?.message
  return message ?? 'Could not load branches for this repository.'
}

// Icon keys returned by the backend server-type catalog, mapped to lucide icons.
const ICONS: Record<string, LucideIcon> = {
  server: Server,
  bot: Bot,
  'trending-up': TrendingUp,
  users: Users,
  'git-fork': GitFork,
}

export function ServerConfigStep({ form }: ServerConfigStepProps) {
  const {
    register,
    watch,
    setValue,
    formState: { errors },
  } = form

  const serverType = watch('serverType')
  const { data: serverTypes, isLoading } = useServerTypes()
  const selectedType = serverTypes?.find((type) => type.id === serverType)
  const allowsCustomRepo = selectedType?.allowCustomRepository ?? false

  const repositoryUrl = watch('customFork.repositoryUrl') ?? ''
  const branch = watch('customFork.branch') ?? ''

  // Debounce the URL so we only hit the backend once the user stops typing.
  const [debouncedUrl, setDebouncedUrl] = useState('')
  useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedUrl(repositoryUrl), 500)
    return () => window.clearTimeout(timer)
  }, [repositoryUrl])

  const branchesQuery = useRepositoryBranches(debouncedUrl, allowsCustomRepo)
  const branches = branchesQuery.data ?? []

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Server Configuration</h2>
        <p className="mt-1 text-sm text-gray-500">
          Give your stack a name and choose the server variant.
        </p>
      </div>

      <FormField
        label="Stack Name"
        htmlFor="stackName"
        error={errors.stackName?.message}
        hint="Lowercase letters, numbers, and hyphens. E.g. my-wotlk-server"
        required
      >
        <input
          id="stackName"
          type="text"
          autoFocus
          autoComplete="off"
          placeholder="my-wotlk-server"
          className={cn(
            'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
            errors.stackName ? 'border-red-400' : 'border-gray-300'
          )}
          {...register('stackName')}
        />
      </FormField>

      <fieldset>
        <legend className="mb-2 text-sm font-medium text-gray-700">
          Server Type <span className="text-red-500" aria-hidden="true">*</span>
        </legend>
        {errors.serverType && (
          <p className="mb-2 text-xs text-red-600" role="alert">{errors.serverType.message}</p>
        )}

        {isLoading && (
          <p className="text-sm text-gray-400">Loading server types…</p>
        )}

        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2" role="radiogroup" aria-label="Server type">
          {(serverTypes ?? []).map(({ id, displayName, description, icon }) => {
            const Icon = ICONS[icon] ?? Server
            const selected = serverType === id

            return (
              <button
                key={id}
                type="button"
                role="radio"
                aria-checked={selected}
                onClick={() => {
                  if (serverType === id) return
                  setValue('serverType', id as ServerType, { shouldDirty: true, shouldValidate: true })
                  setValue(
                    'moduleIds',
                    mergeRequiredModuleIds(watch('moduleIds') ?? [], id as ServerType, serverTypes),
                    { shouldDirty: true },
                  )
                }}
                className={cn(
                  'flex items-start gap-3 rounded-lg border-2 p-4 text-left transition-colors focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2',
                  selected
                    ? 'border-blue-600 bg-blue-50'
                    : 'border-gray-200 bg-white hover:border-gray-300'
                )}
              >
                <Icon
                  className={cn('mt-0.5 h-5 w-5 shrink-0', selected ? 'text-blue-600' : 'text-gray-400')}
                  aria-hidden="true"
                />
                <div>
                  <div className={cn('font-medium', selected ? 'text-blue-700' : 'text-gray-900')}>
                    {displayName}
                  </div>
                  <div className="mt-0.5 text-xs text-gray-500">{description}</div>
                </div>
              </button>
            )
          })}
        </div>

        {selectedType?.allowCustomRepository && (
          <div className="mt-4 space-y-4 rounded-lg border border-gray-200 bg-gray-50 p-4">
            <p className="text-xs text-gray-500">
              Provide the AzerothCore fork to build from. It must be a public git repository compatible
              with the AzerothCore build system.
            </p>
            <FormField
              label="Repository URL"
              htmlFor="customForkRepositoryUrl"
              error={errors.customFork?.repositoryUrl?.message}
              hint="e.g. https://github.com/your-org/azerothcore-wotlk"
              required
            >
              <input
                id="customForkRepositoryUrl"
                type="url"
                autoComplete="off"
                placeholder="https://github.com/your-org/azerothcore-wotlk"
                className={cn(
                  'block w-full rounded-md border px-3 py-2 font-mono text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
                  errors.customFork?.repositoryUrl ? 'border-red-400' : 'border-gray-300'
                )}
                {...register('customFork.repositoryUrl')}
              />
            </FormField>
            <FormField
              label="Branch"
              htmlFor="customForkBranch"
              error={errors.customFork?.branch?.message}
              hint={
                repositoryUrl.trim().length === 0
                  ? 'Enter a repository URL to load its branches. Defaults to master when left blank.'
                  : 'Pick a branch or type to search. Defaults to master when left blank.'
              }
            >
              <BranchCombobox
                id="customForkBranch"
                value={branch}
                onChange={(value) =>
                  setValue('customFork.branch', value, { shouldDirty: true, shouldValidate: true })
                }
                branches={branches}
                isLoading={branchesQuery.isFetching}
                error={branchesQuery.isError ? branchErrorMessage(branchesQuery.error) : null}
                disabled={repositoryUrl.trim().length === 0}
                hasError={Boolean(errors.customFork?.branch)}
              />
            </FormField>
          </div>
        )}
      </fieldset>
    </div>
  )
}
