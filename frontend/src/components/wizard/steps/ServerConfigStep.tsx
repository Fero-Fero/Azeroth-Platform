import { useEffect, useState } from 'react'
import { Bot, GitFork, Server, TrendingUp, Users, Zap, type LucideIcon } from 'lucide-react'
import { BranchCombobox } from '@/components/wizard/common/BranchCombobox'
import { FormField } from '@/components/wizard/common/FormField'
import type { WizardForm } from '@/components/wizard/types'
import { useRepositoryBranches, useServerTypes } from '@/hooks/useModules'
import { mergeRequiredModuleIds } from '@/lib/server-type-modules'
import { normalizeStackNameInput } from '@/lib/stack-name'
import { cn } from '@/lib/utils'
import { expressDefaultModuleIds } from '@/setup/constants'
import { DeploymentTarget, ServerType } from '@/types/stack.types'

interface ServerConfigStepProps {
  form: WizardForm
}

function applyExpressLocalDefaults(
  setValue: WizardForm['setValue'],
  watch: WizardForm['watch'],
) {
  setValue('database.rootPassword', 'password', { shouldDirty: true, shouldValidate: true })
  setValue('advanced.realmlistHost', '127.0.0.1', { shouldDirty: false })
  setValue('armoryAccounts.useEmailConfirmation', false, { shouldDirty: true })
  setValue('armoryAccounts.emailConfigured', false, { shouldDirty: true })
  const realmName = watch('advanced.realmName') ?? ''
  if (!realmName.trim() || realmName === 'AzerothCore') {
    setValue('advanced.realmName', 'Express', { shouldDirty: false })
  }
}

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
  zap: Zap,
}

export function ServerConfigStep({ form }: ServerConfigStepProps) {
  const {
    register,
    watch,
    setValue,
    formState: { errors },
  } = form

  const serverType = watch('serverType')
  const deploymentTarget = watch('deployment.target')
  const { data: serverTypes, isLoading } = useServerTypes()
  const visibleServerTypes = (serverTypes ?? []).filter(
    (type) => !type.localOnly || deploymentTarget === DeploymentTarget.Local,
  )
  const selectedType = visibleServerTypes.find((type) => type.id === serverType)

  useEffect(() => {
    if (deploymentTarget === DeploymentTarget.External && serverType === ServerType.Express) {
      setValue('serverType', ServerType.Standard, { shouldDirty: true, shouldValidate: true })
    }
  }, [deploymentTarget, serverType, setValue])

  useEffect(() => {
    if (serverType === ServerType.Express) {
      applyExpressLocalDefaults(setValue, watch)
    }
  }, [serverType, setValue, watch])
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
        hint="Letters and numbers are kept. Words are lowercased and joined with a hyphen. E.g. My Test Server → my-test-server"
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
          {...register('stackName', {
            onChange: (event) => {
              const next = normalizeStackNameInput(event.target.value)
              if (next !== event.target.value) {
                setValue('stackName', next, { shouldValidate: true, shouldDirty: true })
              }
            },
            onBlur: (event) => {
              const next = normalizeStackNameInput(event.target.value, { trimEdges: true })
              if (next !== event.target.value) {
                setValue('stackName', next, { shouldValidate: true, shouldDirty: true })
              }
            },
          })}
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
          {visibleServerTypes.map(({ id, displayName, description, icon }) => {
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
                  if (id === ServerType.Express) {
                    setValue('moduleIds', expressDefaultModuleIds(), { shouldDirty: true })
                    applyExpressLocalDefaults(setValue, watch)
                  } else {
                    setValue(
                      'moduleIds',
                      mergeRequiredModuleIds(watch('moduleIds') ?? [], id as ServerType, serverTypes),
                      { shouldDirty: true },
                    )
                  }
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

      <label className="flex cursor-pointer items-start gap-3 rounded-lg border border-gray-200 p-4">
        <input
          type="checkbox"
          className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
          {...register('includeArmory', {
            onChange: (event) => {
              if (!event.target.checked) {
                setValue('armoryAccounts.useEmailConfirmation', false, { shouldDirty: true })
                setValue('armoryAccounts.emailConfigured', false, { shouldDirty: true })
              }
            },
          })}
        />
        <span>
          <span className="text-sm font-medium text-gray-800">Include Armory</span>
          <span className="mt-0.5 block text-xs text-gray-500">
            When off, this stack never builds or starts the armory. The launcher still downloads client
            files from this stack.
          </span>
        </span>
      </label>
    </div>
  )
}
