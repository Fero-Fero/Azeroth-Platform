import type { ReactNode } from 'react'
import { AlertCircle, AlertTriangle, CheckCircle2 } from 'lucide-react'
import type { WizardForm } from '@/components/wizard/types'
import { useServerTypes } from '@/hooks/useModules'
import { ServerTypeReviewNotes } from '@/server-types'
import { ServerType, DeploymentTarget, type PortFieldPath, type SuggestedPorts } from '@/types/stack.types'

interface ReviewStepProps {
  form: WizardForm
  validationErrors?: string[]
  isValidating?: boolean
  suggestedPorts?: SuggestedPorts
  onApplySuggestedPorts?: () => void
}

function ReviewRow({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="flex justify-between gap-4 border-b border-gray-100 py-2 last:border-0">
      <dt className="shrink-0 text-sm text-gray-500">{label}</dt>
      <dd className="text-right text-sm font-medium text-gray-900">{value}</dd>
    </div>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="overflow-hidden rounded-lg border border-gray-200">
      <div className="border-b border-gray-200 bg-gray-50 px-4 py-2.5">
        <h3 className="text-sm font-semibold text-gray-700">{title}</h3>
      </div>
      <dl className="px-4">{children}</dl>
    </div>
  )
}

const PORT_LABELS: Record<PortFieldPath, string> = {
  'database.port': 'MySQL',
  'ports.authServer': 'Auth',
  'ports.worldServer': 'World',
  'ports.soapPort': 'SOAP',
}

export function ReviewStep({
  form,
  validationErrors = [],
  isValidating,
  suggestedPorts = {},
  onApplySuggestedPorts,
}: ReviewStepProps) {
  const values = form.getValues()
  const { data: serverTypes } = useServerTypes()
  const selectedServerType = serverTypes?.find((type) => type.id === values.serverType)
  const serverTypeLabel = selectedServerType?.displayName ?? values.serverType
  // Flatten per-service env vars into [service, key, value] rows for the review summary.
  const serviceEnvRows = Object.entries(values.advanced.serviceEnvVars ?? {}).flatMap(
    ([service, bucket]) =>
      Object.entries(bucket ?? {})
        .filter(([key]) => key.trim())
        .map(([key, value]) => ({ service, key, value })),
  )
  const suggestedPortEntries = Object.entries(suggestedPorts) as Array<[PortFieldPath, number]>
  const armoryAccounts = values.armoryAccounts
  const email = armoryAccounts?.email

  return (
    <div className="space-y-5">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Review &amp; Create</h2>
        <p className="mt-1 text-sm text-gray-500">
          Check your configuration before creating the stack.
        </p>
      </div>

      {isValidating && (
        <div className="flex items-center gap-2 py-2 text-sm text-gray-500">
          <span className="inline-block h-3 w-3 animate-spin rounded-full border-2 border-blue-600 border-t-transparent" aria-hidden="true" />
          Validating configuration…
        </div>
      )}

      {validationErrors.length > 0 && (
        <div className="rounded-md border border-red-200 bg-red-50 p-4" role="alert" aria-label="Validation errors">
          <div className="mb-2 flex items-center gap-2">
            <AlertCircle className="h-4 w-4 shrink-0 text-red-500" aria-hidden="true" />
            <span className="text-sm font-medium text-red-700">Please fix these issues before creating:</span>
          </div>
          <ul className="list-inside list-disc space-y-1">
            {validationErrors.map((error, index) => (
              <li key={index} className="text-sm text-red-600">{error}</li>
            ))}
          </ul>
          {suggestedPortEntries.length > 0 && onApplySuggestedPorts && (
            <div className="mt-3 flex flex-wrap items-center gap-3">
              <button
                type="button"
                onClick={onApplySuggestedPorts}
                className="rounded-md border border-red-300 bg-white px-3 py-1.5 text-sm font-medium text-red-700 hover:bg-red-100 focus:outline-none focus:ring-2 focus:ring-red-500"
              >
                Use available ports
              </button>
              <span className="text-xs text-red-700">
                {suggestedPortEntries.map(([field, port]) => `${PORT_LABELS[field]} ${port}`).join(' · ')}
              </span>
            </div>
          )}
        </div>
      )}

      {validationErrors.length === 0 && !isValidating && (
        <div className="flex items-center gap-2 rounded-md border border-green-200 bg-green-50 px-4 py-2.5 text-sm text-green-700">
          <CheckCircle2 className="h-4 w-4 shrink-0" aria-hidden="true" />
          Configuration looks good!
        </div>
      )}

      <Section title="Deployment">
        <ReviewRow
          label="Target"
          value={
            values.deployment?.target === DeploymentTarget.External
              ? 'External VPC'
              : 'Local'
          }
        />
        {values.deployment?.target === DeploymentTarget.External && (
          <>
            <ReviewRow label="Remote Host" value={values.deployment.externalHost || '—'} />
            <ReviewRow label="SSH Port" value={values.deployment.externalSshPort ?? 22} />
            <ReviewRow label="SSH User" value={values.deployment.externalSshUser || '—'} />
            <ReviewRow label="Connection Verified" value={values.deployment.connectionVerified ? 'Yes' : 'No'} />
            <ReviewRow
              label="First Time Setup"
              value={values.deployment.firstTimeSetupCompleted ? 'Completed' : 'Not completed'}
            />
            <ReviewRow
              label="Cloud SG Acknowledged"
              value={values.deployment.cloudSecurityGroupAcknowledged ? 'Yes' : 'No'}
            />
          </>
        )}
      </Section>

      <Section title="Server">
        <ReviewRow label="Stack Name" value={values.stackName || '—'} />
        <ReviewRow
          label="Server Type"
          value={
            <span className={values.serverType !== ServerType.Standard ? 'text-amber-600' : undefined}>
              {serverTypeLabel}
            </span>
          }
        />
        {selectedServerType?.allowCustomRepository && (
          <ReviewRow
            label="Custom Fork"
            value={
              <span className="break-all font-mono text-xs">
                {values.customFork?.repositoryUrl || '—'}
                {values.customFork?.branch ? ` @ ${values.customFork.branch}` : ''}
              </span>
            }
          />
        )}
      </Section>

      <ServerTypeReviewNotes
        serverType={values.serverType}
        selectedModuleIds={values.moduleIds}
        browseTab="curated"
      />

      <Section title="Modules">
        <ReviewRow
          label="Selected Modules"
          value={
            values.moduleIds.length > 0
              ? `${values.moduleIds.length} module${values.moduleIds.length !== 1 ? 's' : ''}`
              : 'None'
          }
        />
      </Section>

      <Section title="Database">
        <ReviewRow label="Root Password" value="••••••••" />
        <ReviewRow label="MySQL Port" value={values.database.port} />
      </Section>

      <Section title="Ports">
        <ReviewRow label="Auth Server" value={values.ports.authServer} />
        <ReviewRow label="World Server" value={values.ports.worldServer} />
        <ReviewRow label="SOAP Port" value={values.ports.soapPort} />
      </Section>

      {armoryAccounts?.useEmailConfirmation && (
        <Section title="Email Confirmation">
          {armoryAccounts.emailConfigured ? (
            <>
              <ReviewRow label="SMTP Host" value={email?.smtpHost || '—'} />
              <ReviewRow label="SMTP Port" value={email?.smtpPort ?? '—'} />
              <ReviewRow label="Security" value={email?.smtpSecurity || '—'} />
              <ReviewRow label="From Address" value={email?.fromAddress || '—'} />
              <ReviewRow label="From Name" value={email?.fromName || values.advanced.realmName || '—'} />
            </>
          ) : (
            <div className="flex items-start gap-2 border-b border-gray-100 py-3 text-sm text-amber-800">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
              <p>
                Email confirmation is enabled but not configured — armory registration is disabled until
                email is set up.
              </p>
            </div>
          )}
        </Section>
      )}

      <Section title="Advanced">
        <ReviewRow label="Max Players" value={values.advanced.maxPlayers} />
        <ReviewRow label="Realm Name" value={values.advanced.realmName} />
        {serviceEnvRows.length > 0 && (
          <div className="border-b border-gray-100 py-2 last:border-0">
            <dt className="mb-2 text-sm text-gray-500">Environment Variables</dt>
            <dd className="space-y-1">
              {serviceEnvRows.map(({ service, key, value }) => (
                <div key={`${service}.${key}`} className="flex items-start gap-2 font-mono text-xs">
                  <span className="shrink-0 rounded bg-gray-100 px-1.5 py-0.5 text-[10px] uppercase text-gray-500">
                    {service}
                  </span>
                  <span className="shrink-0 font-semibold text-gray-700">{key}</span>
                  <span className="text-gray-400">=</span>
                  <span className="break-all text-gray-600">{value}</span>
                </div>
              ))}
            </dd>
          </div>
        )}
      </Section>
    </div>
  )
}
