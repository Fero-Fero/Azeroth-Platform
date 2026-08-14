import { useState } from 'react'
import { Cloud, Server } from 'lucide-react'
import { FormField } from '@/components/wizard/common/FormField'
import { CloudInstancePicker } from '@/components/wizard/common/CloudInstancePicker'
import { CloudLaunchPanel } from '@/components/wizard/common/CloudLaunchPanel'
import { VpcConnectionTestFooter } from '@/components/wizard/common/VpcConnectionTestFooter'
import type { CloudLaunchResultDto, CloudInstanceDto, RemoteConnectionTestResultDto } from '@/types/stack.types'
import { cn } from '@/lib/utils'
import type { FieldErrors, UseFormRegister } from 'react-hook-form'
import type { ReactNode } from 'react'
import type { WizardFormData } from '@/schemas/wizard.schemas'

type ConnectionMethod = 'manual' | 'cloud'

interface VpcConnectionMethodTabsProps {
  disabled?: boolean
  cloudConnectionId: string
  onCloudConnectionIdChange: (id: string) => void
  externalHost: string
  externalSshUser: string
  savedSshKeyId: string
  register: UseFormRegister<WizardFormData>
  errors: FieldErrors<WizardFormData>
  onSelectInstance: (instance: CloudInstanceDto) => void
  onLaunched: (result: CloudLaunchResultDto) => void
  connectionFieldsReady: boolean
  credentialsReady: boolean
  sshTesting: boolean
  onTestConnection: () => void
  sshTestResult: RemoteConnectionTestResultDto | null
  children?: ReactNode
}

export function VpcConnectionMethodTabs({
  disabled = false,
  cloudConnectionId,
  onCloudConnectionIdChange,
  externalHost,
  externalSshUser,
  savedSshKeyId,
  register,
  errors,
  onSelectInstance,
  onLaunched,
  connectionFieldsReady,
  credentialsReady,
  sshTesting,
  onTestConnection,
  sshTestResult,
  children,
}: VpcConnectionMethodTabsProps) {
  const [method, setMethod] = useState<ConnectionMethod>('manual')

  return (
    <div className="rounded-lg border border-gray-200 bg-gray-50/80">
      <div className="border-b border-gray-200 px-3 pt-3">
        <p className="text-xs font-semibold text-gray-900">How will you reach this server?</p>
        <p className="mt-0.5 text-[11px] text-gray-600">
          Enter a host you already have, or link a cloud account to pick or launch a VM.
        </p>
        <div
          role="tablist"
          aria-label="Connection method"
          className="mt-3 flex flex-wrap gap-2"
        >
          {(
            [
              { id: 'manual' as const, label: 'Remote host', icon: Server },
              { id: 'cloud' as const, label: 'Cloud account', icon: Cloud },
            ] as const
          ).map((tab) => (
            <button
              key={tab.id}
              type="button"
              role="tab"
              aria-selected={method === tab.id}
              onClick={() => setMethod(tab.id)}
              disabled={disabled}
              className={cn(
                'inline-flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-xs font-medium transition-colors',
                method === tab.id
                  ? 'border-blue-500 bg-white text-blue-900 shadow-sm'
                  : 'border-transparent bg-transparent text-gray-700 hover:bg-white/80'
              )}
            >
              <tab.icon className="h-3.5 w-3.5" aria-hidden="true" />
              {tab.label}
            </button>
          ))}
        </div>
      </div>

      <div className="p-3">
        {method === 'manual' ? (
          <div
            role="tabpanel"
            className="space-y-4"
          >
            <div className="grid gap-4 sm:grid-cols-3">
              <div className="sm:col-span-2">
                <FormField
                  label="Remote host"
                  htmlFor="external-host"
                  error={errors.deployment?.externalHost?.message}
                  hint="Public IP or DNS of your VPS"
                  required
                >
                  <input
                    id="external-host"
                    type="text"
                    placeholder="e.g. 203.0.113.10 or vpc.example.com"
                    className={cn(
                      'block w-full rounded-md border bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
                      errors.deployment?.externalHost ? 'border-red-400' : 'border-gray-300'
                    )}
                    {...register('deployment.externalHost')}
                  />
                </FormField>
              </div>
              <FormField
                label="SSH port"
                htmlFor="external-ssh-port"
                error={errors.deployment?.externalSshPort?.message}
              >
                <input
                  id="external-ssh-port"
                  type="number"
                  min={1}
                  max={65535}
                  className={cn(
                    'block w-full rounded-md border bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
                    errors.deployment?.externalSshPort ? 'border-red-400' : 'border-gray-300'
                  )}
                  {...register('deployment.externalSshPort', { valueAsNumber: true })}
                />
              </FormField>
            </div>
            <FormField
              label="SSH user"
              htmlFor="external-ssh-user"
              error={errors.deployment?.externalSshUser?.message}
              hint="Often ubuntu, root, azureuser, or ec2-user"
              required
            >
              <input
                id="external-ssh-user"
                type="text"
                placeholder="e.g. ubuntu"
                className={cn(
                  'block w-full rounded-md border bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
                  errors.deployment?.externalSshUser ? 'border-red-400' : 'border-gray-300'
                )}
                {...register('deployment.externalSshUser')}
              />
            </FormField>
          </div>
        ) : (
          <div role="tabpanel" className="space-y-3">
            {externalHost.trim() ? (
              <p className="rounded-md border border-green-200 bg-green-50 px-3 py-2 text-xs text-green-900">
                Selected host:{' '}
                <span className="font-mono font-medium">{externalHost.trim()}</span>
                {externalSshUser.trim() ? (
                  <>
                    {' '}
                    · SSH user <span className="font-mono">{externalSshUser.trim()}</span>
                  </>
                ) : null}
              </p>
            ) : (
              <p className="text-xs text-gray-600">
                Link an account, pick a running server, or launch a new one — host and SSH user fill in
                automatically.
              </p>
            )}
            <CloudInstancePicker
              disabled={disabled}
              connectionId={cloudConnectionId}
              onConnectionIdChange={onCloudConnectionIdChange}
              onSelectInstance={onSelectInstance}
            />
            <CloudLaunchPanel
              disabled={disabled}
              connectionId={cloudConnectionId}
              onConnectionIdChange={onCloudConnectionIdChange}
              sshUser={externalSshUser}
              savedSshKeyId={savedSshKeyId}
              onLaunched={onLaunched}
            />
          </div>
        )}

        {children ? <div className="mt-4 border-t border-gray-200 pt-4">{children}</div> : null}

        <VpcConnectionTestFooter
          method={method}
          connectionFieldsReady={connectionFieldsReady}
          credentialsReady={credentialsReady}
          testing={sshTesting}
          disabled={disabled}
          onTestConnection={onTestConnection}
          testResult={sshTestResult}
        />
      </div>
    </div>
  )
}
