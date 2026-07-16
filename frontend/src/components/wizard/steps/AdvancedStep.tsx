import { useCallback, useEffect, useState } from 'react'
import { Loader2, CheckCircle2, XCircle, Settings2 } from 'lucide-react'
import { FormField } from '@/components/wizard/common/FormField'
import { ModuleConfigModal } from '@/components/wizard/ModuleConfigModal'
import { ServiceEnvVarsEditor } from '@/components/wizard/ServiceEnvVarsEditor'
import type { WizardForm } from '@/components/wizard/types'
import { useServiceEnvTemplates } from '@/hooks/useModules'
import { cn } from '@/lib/utils'
import { systemApi } from '@/services/api'
import { DeploymentTarget, type RemoteConnectionTestResultDto } from '@/types/stack.types'
import type { ServiceEnvVars } from '@/types/serviceEnv'
import { browserLanHost, detectManagerLanHost } from '@/lib/network'

interface AdvancedStepProps {
  form: WizardForm
}

// The worldserver bucket holds AzerothCore AC_* overrides, which the module config modal also edits.
const WORLDSERVER = 'worldserver'

export function AdvancedStep({ form }: AdvancedStepProps) {
  const {
    register,
    watch,
    setValue,
    formState: { errors },
  } = form
  const serviceEnvVars = (watch('advanced.serviceEnvVars') ?? {}) as ServiceEnvVars
  const selectedModules = (watch('moduleIds') ?? []) as string[]
  const { data: envTemplates } = useServiceEnvTemplates()
  const [showModuleConfig, setShowModuleConfig] = useState(false)

  const deploymentTarget = watch('deployment.target')
  const realmlistHost = watch('advanced.realmlistHost')
  const [suggestedHost, setSuggestedHost] = useState<string>('')
  const [testing, setTesting] = useState(false)
  const [testResult, setTestResult] = useState<RemoteConnectionTestResultDto | null>(null)

  // Suggest the host's LAN IP for the realmlist host so LAN clients can connect out of the box.
  useEffect(() => {
    let cancelled = false
    systemApi
      .network()
      .then(async (res) => {
        if (cancelled) return
        const suggested =
          res.data.suggestedRealmlistHost?.trim()
          || browserLanHost()
          || await detectManagerLanHost()
          || ''
        if (cancelled) return
        setSuggestedHost(suggested)
        // Prefill only when the field is still empty and we're deploying locally.
        if (!realmlistHost && suggested && deploymentTarget === DeploymentTarget.Local) {
          setValue('advanced.realmlistHost', suggested, { shouldDirty: false })
        }
      })
      .catch(() => undefined)
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const handleTestConnection = useCallback(async () => {
    setTesting(true)
    setTestResult(null)
    try {
      const res = await systemApi.testRemoteConnection({
        target: DeploymentTarget.External,
        externalHost: watch('deployment.externalHost') ?? '',
        externalSshPort: Number(watch('deployment.externalSshPort')) || 22,
        externalSshUser: watch('deployment.externalSshUser') ?? '',
        externalSshPrivateKey: watch('deployment.externalSshPrivateKey') ?? '',
      })
      setTestResult(res.data)
    } catch {
      setTestResult({ success: false, message: 'Failed to reach the platform to run the test.' })
    } finally {
      setTesting(false)
    }
  }, [watch])

  // Build module name mapping
  const moduleNames: Record<string, string> = {
    'mod-autobalance': 'Auto Balance',
    'mod-playerbots': 'Playerbots',
    'mod-transmog': 'Transmogrification',
    'mod-ah-bot': 'Auction House Bot',
  }

  const setServiceEnvVars = useCallback((next: ServiceEnvVars) => {
    setValue('advanced.serviceEnvVars', next, { shouldDirty: true })
  }, [setValue])

  // The module config modal edits AC_* worldserver overrides; keep it pointed at the worldserver bucket.
  const worldserverBucket = serviceEnvVars[WORLDSERVER] ?? {}

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Advanced Settings</h2>
        <p className="mt-1 text-sm text-gray-500">
          Fine-tune server behaviour. These can be changed after creation.
        </p>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <FormField
          label="Max Players"
          htmlFor="max-players"
          error={errors.advanced?.maxPlayers?.message}
          hint="Concurrent player cap (1–10,000)"
          required
        >
          <input
            id="max-players"
            type="number"
            min={1}
            max={10000}
            className={cn(
              'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
              errors.advanced?.maxPlayers ? 'border-red-400' : 'border-gray-300'
            )}
            {...register('advanced.maxPlayers', { valueAsNumber: true })}
          />
        </FormField>

        <FormField
          label="Realm Name"
          htmlFor="realm-name"
          error={errors.advanced?.realmName?.message}
          hint="Displayed in the realm selection screen"
          required
        >
          <input
            id="realm-name"
            type="text"
            maxLength={64}
            className={cn(
              'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
              errors.advanced?.realmName ? 'border-red-400' : 'border-gray-300'
            )}
            {...register('advanced.realmName')}
          />
        </FormField>
      </div>

      <FormField
        label="Realmlist Host"
        htmlFor="realmlist-host"
        error={errors.advanced?.realmlistHost?.message}
        hint={
          deploymentTarget === DeploymentTarget.External
            ? 'Address clients use to reach this realm. Defaults to the remote host for external stacks.'
            : suggestedHost
              ? `Address clients use to reach this realm. Your detected LAN IP is ${suggestedHost} — use it so other machines on your network can connect (not 127.0.0.1).`
              : 'Address clients use to reach this realm (your machine\u2019s LAN IP, not 127.0.0.1).'
        }
      >
        <input
          id="realmlist-host"
          type="text"
          maxLength={255}
          placeholder={suggestedHost || 'e.g. 192.168.1.50'}
          className={cn(
            'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
            errors.advanced?.realmlistHost ? 'border-red-400' : 'border-gray-300'
          )}
          {...register('advanced.realmlistHost')}
        />
      </FormField>

      <div className="rounded-lg border border-gray-200 p-4">
        <div className="mb-3">
          <span className="text-sm font-medium text-gray-700">Armory Accounts</span>
          <p className="text-xs text-gray-500">
            Player registration is enabled on the armory by default. Require email verification before
            new accounts can log in.
          </p>
        </div>
        <label className="flex cursor-pointer items-start gap-3">
          <input
            type="checkbox"
            className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            {...register('armoryAccounts.useEmailConfirmation', {
              onChange: (event) => {
                const enabled = event.target.checked
                if (!enabled) {
                  setValue('armoryAccounts.emailConfigured', false, { shouldDirty: true })
                }
              },
            })}
          />
          <span>
            <span className="text-sm font-medium text-gray-800">Require email confirmation before account activation</span>
            <span className="mt-0.5 block text-xs text-gray-500">
              When enabled, players register with email and must verify before choosing a WoW username.
            </span>
          </span>
        </label>
      </div>

      <div className="rounded-lg border border-gray-200 p-4">
        <div className="mb-3">
          <span className="text-sm font-medium text-gray-700">Deployment</span>
          <p className="text-xs text-gray-500">
            Where this stack&rsquo;s containers run. External stacks are built locally and shipped to a
            remote Docker host over SSH.
          </p>
        </div>

        <div className="flex gap-3">
          {[
            { value: DeploymentTarget.Local, label: 'Local', desc: 'Runs on this machine' },
            { value: DeploymentTarget.External, label: 'External', desc: 'Runs on a remote host' },
          ].map((option) => (
            <label
              key={option.value}
              className={cn(
                'flex flex-1 cursor-pointer flex-col rounded-md border px-3 py-2 text-sm',
                deploymentTarget === option.value ? 'border-blue-500 bg-blue-50' : 'border-gray-300'
              )}
            >
              <span className="flex items-center gap-2 font-medium text-gray-800">
                <input
                  type="radio"
                  value={option.value}
                  checked={deploymentTarget === option.value}
                  onChange={() => setValue('deployment.target', option.value, { shouldDirty: true, shouldValidate: true })}
                />
                {option.label}
              </span>
              <span className="ml-5 text-xs text-gray-500">{option.desc}</span>
            </label>
          ))}
        </div>

        {deploymentTarget === DeploymentTarget.External && (
          <div className="mt-4 space-y-4">
            <div className="grid gap-4 sm:grid-cols-3">
              <div className="sm:col-span-2">
                <FormField
                  label="Remote Host"
                  htmlFor="external-host"
                  error={errors.deployment?.externalHost?.message}
                  hint="Public IP or DNS of the droplet/instance"
                  required
                >
                  <input
                    id="external-host"
                    type="text"
                    className={cn(
                      'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
                      errors.deployment?.externalHost ? 'border-red-400' : 'border-gray-300'
                    )}
                    {...register('deployment.externalHost')}
                  />
                </FormField>
              </div>
              <FormField
                label="SSH Port"
                htmlFor="external-ssh-port"
                error={errors.deployment?.externalSshPort?.message}
              >
                <input
                  id="external-ssh-port"
                  type="number"
                  min={1}
                  max={65535}
                  className={cn(
                    'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
                    errors.deployment?.externalSshPort ? 'border-red-400' : 'border-gray-300'
                  )}
                  {...register('deployment.externalSshPort', { valueAsNumber: true })}
                />
              </FormField>
            </div>

            <FormField
              label="SSH User"
              htmlFor="external-ssh-user"
              error={errors.deployment?.externalSshUser?.message}
              required
            >
              <input
                id="external-ssh-user"
                type="text"
                className={cn(
                  'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
                  errors.deployment?.externalSshUser ? 'border-red-400' : 'border-gray-300'
                )}
                {...register('deployment.externalSshUser')}
              />
            </FormField>

            <FormField
              label="SSH Private Key"
              htmlFor="external-ssh-key"
              error={errors.deployment?.externalSshPrivateKey?.message}
              hint="PEM-encoded private key with access to the remote host. Stored on the platform at rest."
              required
            >
              <textarea
                id="external-ssh-key"
                rows={5}
                placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
                className={cn(
                  'block w-full rounded-md border px-3 py-2 font-mono text-xs shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
                  errors.deployment?.externalSshPrivateKey ? 'border-red-400' : 'border-gray-300'
                )}
                {...register('deployment.externalSshPrivateKey')}
              />
            </FormField>

            <div className="flex items-center gap-3">
              <button
                type="button"
                onClick={handleTestConnection}
                disabled={testing}
                className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-60"
              >
                {testing && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
                Test connection
              </button>
              {testResult && (
                <span
                  className={cn(
                    'inline-flex items-center gap-1.5 text-xs',
                    testResult.success ? 'text-green-700' : 'text-red-700'
                  )}
                >
                  {testResult.success ? (
                    <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
                  ) : (
                    <XCircle className="h-3.5 w-3.5" aria-hidden="true" />
                  )}
                  {testResult.message}
                </span>
              )}
            </div>
          </div>
        )}
      </div>

      <div>
        <div className="mb-2 flex items-center justify-between">
          <div>
            <span className="text-sm font-medium text-gray-700">Environment Variables</span>
            <p className="text-xs text-gray-500">
              Environment variables are per-container. Configure the variables each service accepts
              below; anything not listed can be added as a custom variable.
            </p>
          </div>
          {selectedModules.length > 0 && (
            <button
              type="button"
              onClick={() => setShowModuleConfig(true)}
              className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              <Settings2 className="h-3.5 w-3.5" aria-hidden="true" />
              Configure modules
            </button>
          )}
        </div>

        <ServiceEnvVarsEditor
          templates={envTemplates ?? []}
          value={serviceEnvVars}
          onChange={setServiceEnvVars}
        />
      </div>

      {showModuleConfig && (
        <ModuleConfigModal
          selectedModuleIds={selectedModules}
          moduleNames={moduleNames}
          envVars={worldserverBucket}
          onSave={(newEnvVars) => {
            setServiceEnvVars({ ...serviceEnvVars, [WORLDSERVER]: newEnvVars })
            setShowModuleConfig(false)
          }}
          onClose={() => setShowModuleConfig(false)}
        />
      )}
    </div>
  )
}
