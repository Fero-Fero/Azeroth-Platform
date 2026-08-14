import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Loader2 } from 'lucide-react'
import { cloudApi } from '@/services/api'
import { CloudProvider, type CloudProviderConnectionDto } from '@/types/stack.types'
import { cn } from '@/lib/utils'

type LinkProvider =
  | CloudProvider.DigitalOcean
  | CloudProvider.Aws
  | CloudProvider.Gcp
  | CloudProvider.Azure
  | CloudProvider.Hetzner
  | CloudProvider.Vultr

const PROVIDER_OPTIONS: Array<{ id: LinkProvider; label: string }> = [
  { id: CloudProvider.DigitalOcean, label: 'DigitalOcean' },
  { id: CloudProvider.Hetzner, label: 'Hetzner' },
  { id: CloudProvider.Vultr, label: 'Vultr' },
  { id: CloudProvider.Aws, label: 'AWS' },
  { id: CloudProvider.Gcp, label: 'GCP' },
  { id: CloudProvider.Azure, label: 'Azure' },
]

function isApiTokenProvider(provider: LinkProvider): boolean {
  return provider === CloudProvider.DigitalOcean
    || provider === CloudProvider.Hetzner
    || provider === CloudProvider.Vultr
}

function defaultLinkLabel(provider: LinkProvider): string {
  switch (provider) {
    case CloudProvider.Aws:
      return 'AWS'
    case CloudProvider.Gcp:
      return 'Google Cloud'
    case CloudProvider.Azure:
      return 'Azure'
    case CloudProvider.Hetzner:
      return 'Hetzner Cloud'
    case CloudProvider.Vultr:
      return 'Vultr'
    default:
      return 'DigitalOcean'
  }
}

function extractErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object' && 'response' in error) {
    const data = (error as { response?: { data?: unknown } }).response?.data
    if (typeof data === 'string' && data.trim().length > 0) {
      return data
    }
  }

  return fallback
}

interface CloudConnectionLinkFormProps {
  disabled?: boolean
  /** When set, hides provider tabs and locks to this provider. */
  provider?: LinkProvider
  onLinked?: (connection: CloudProviderConnectionDto) => void
  className?: string
  idPrefix?: string
}

export function CloudConnectionLinkForm({
  disabled = false,
  provider: fixedProvider,
  onLinked,
  className,
  idPrefix = 'cloud-link',
}: CloudConnectionLinkFormProps) {
  const queryClient = useQueryClient()
  const [provider, setProvider] = useState<LinkProvider>(fixedProvider ?? CloudProvider.DigitalOcean)
  const [linkLabel, setLinkLabel] = useState(defaultLinkLabel(provider))
  const [linkToken, setLinkToken] = useState('')
  const [linkAccessKeyId, setLinkAccessKeyId] = useState('')
  const [linkSecretAccessKey, setLinkSecretAccessKey] = useState('')
  const [linkServiceAccountJson, setLinkServiceAccountJson] = useState('')
  const [linkAzureTenantId, setLinkAzureTenantId] = useState('')
  const [linkAzureClientId, setLinkAzureClientId] = useState('')
  const [linkAzureClientSecret, setLinkAzureClientSecret] = useState('')
  const [linkAzureSubscriptionId, setLinkAzureSubscriptionId] = useState('')
  const [linkDefaultRegion, setLinkDefaultRegion] = useState('')
  const [linkError, setLinkError] = useState<string | null>(null)

  useEffect(() => {
    if (fixedProvider) {
      setProvider(fixedProvider)
    }
  }, [fixedProvider])

  useEffect(() => {
    setLinkLabel(defaultLinkLabel(provider))
    setLinkError(null)
  }, [provider])

  const linkMutation = useMutation({
    mutationFn: async () => {
      if (provider === CloudProvider.Aws) {
        return (
          await cloudApi.createConnection({
            provider: CloudProvider.Aws,
            label: linkLabel.trim() || 'AWS',
            accessKeyId: linkAccessKeyId.trim(),
            secretAccessKey: linkSecretAccessKey.trim(),
            defaultRegion: linkDefaultRegion.trim() || undefined,
          })
        ).data
      }

      if (provider === CloudProvider.Gcp) {
        return (
          await cloudApi.createConnection({
            provider: CloudProvider.Gcp,
            label: linkLabel.trim() || 'Google Cloud',
            serviceAccountJson: linkServiceAccountJson.trim(),
            defaultRegion: linkDefaultRegion.trim() || undefined,
          })
        ).data
      }

      if (provider === CloudProvider.Azure) {
        return (
          await cloudApi.createConnection({
            provider: CloudProvider.Azure,
            label: linkLabel.trim() || 'Azure',
            azureTenantId: linkAzureTenantId.trim(),
            azureClientId: linkAzureClientId.trim(),
            azureClientSecret: linkAzureClientSecret.trim(),
            azureSubscriptionId: linkAzureSubscriptionId.trim(),
            defaultRegion: linkDefaultRegion.trim() || undefined,
          })
        ).data
      }

      if (isApiTokenProvider(provider)) {
        const label =
          provider === CloudProvider.Hetzner
            ? linkLabel.trim() || 'Hetzner Cloud'
            : provider === CloudProvider.Vultr
              ? linkLabel.trim() || 'Vultr'
              : linkLabel.trim() || 'DigitalOcean'

        return (
          await cloudApi.createConnection({
            provider,
            label,
            accessToken: linkToken.trim(),
            defaultRegion: linkDefaultRegion.trim() || undefined,
          })
        ).data
      }

      throw new Error('Unsupported cloud provider.')
    },
    onSuccess: async (created) => {
      setLinkToken('')
      setLinkAccessKeyId('')
      setLinkSecretAccessKey('')
      setLinkServiceAccountJson('')
      setLinkAzureTenantId('')
      setLinkAzureClientId('')
      setLinkAzureClientSecret('')
      setLinkAzureSubscriptionId('')
      setLinkDefaultRegion('')
      setLinkError(null)
      await queryClient.invalidateQueries({ queryKey: ['cloud-connections'] })
      onLinked?.(created)
    },
    onError: (error: unknown) => {
      setLinkError(extractErrorMessage(error, 'Failed to link cloud account.'))
    },
  })

  const canSaveLink =
    provider === CloudProvider.Aws
      ? linkAccessKeyId.trim().length > 0 && linkSecretAccessKey.trim().length > 0
      : provider === CloudProvider.Gcp
        ? linkServiceAccountJson.trim().length > 0
        : provider === CloudProvider.Azure
          ? linkAzureTenantId.trim().length > 0
            && linkAzureClientId.trim().length > 0
            && linkAzureClientSecret.trim().length > 0
            && linkAzureSubscriptionId.trim().length > 0
          : linkToken.trim().length > 0

  const regionFilterLabel =
    provider === CloudProvider.Gcp
      ? 'Default zone (optional)'
      : provider === CloudProvider.Azure
        ? 'Default location (optional)'
        : provider === CloudProvider.Hetzner
          ? 'Default location (optional)'
          : 'Default region (optional)'

  const regionFilterPlaceholder =
    provider === CloudProvider.Gcp
      ? 'e.g. us-central1-a'
      : provider === CloudProvider.Azure
        ? 'e.g. eastus'
        : provider === CloudProvider.Hetzner
          ? 'e.g. nbg1'
          : provider === CloudProvider.Vultr
            ? 'e.g. ewr'
            : 'e.g. us-east-1'

  const regionFilterHint =
    provider === CloudProvider.Gcp
      ? 'Limits instance listing to one zone or region prefix. Leave blank to scan all zones.'
      : provider === CloudProvider.Azure
        ? 'Limits instance listing to one Azure location. Leave blank to scan all locations.'
        : provider === CloudProvider.Hetzner
          ? 'Limits instance listing to one Hetzner location. Leave blank to scan all locations.'
          : provider === CloudProvider.Vultr
            ? 'Limits instance listing to one Vultr region. Leave blank to scan all regions.'
            : 'Limits instance listing to one region for faster results. Leave blank to scan all regions.'

  return (
    <div className={cn('space-y-3', className)}>
      {!fixedProvider ? (
        <div className="flex flex-wrap gap-2">
          {PROVIDER_OPTIONS.map((option) => (
            <button
              key={option.id}
              type="button"
              disabled={disabled || linkMutation.isPending}
              onClick={() => setProvider(option.id)}
              className={cn(
                'rounded-md border px-2.5 py-1 text-[11px] font-medium',
                provider === option.id
                  ? 'border-blue-500 bg-white text-blue-900'
                  : 'border-gray-200 bg-gray-50 text-gray-700 hover:bg-gray-100'
              )}
            >
              {option.label}
            </button>
          ))}
        </div>
      ) : null}

      {provider === CloudProvider.DigitalOcean ? (
        <p className="text-xs text-gray-700">
          Personal access token from DigitalOcean → API → Tokens/Keys. Read scope lists droplets; write
          scope is required for Launch via platform.
        </p>
      ) : provider === CloudProvider.Hetzner ? (
        <p className="text-xs text-gray-700">
          API token from Hetzner Cloud Console → Security → API tokens. Read permission lists servers;
          read/write is required for Launch via platform.
        </p>
      ) : provider === CloudProvider.Vultr ? (
        <p className="text-xs text-gray-700">
          API key from Vultr → Account → API. Read permission lists instances; read/write is required for
          Launch via platform.
        </p>
      ) : provider === CloudProvider.Aws ? (
        <p className="text-xs text-gray-700">
          IAM user with EC2 read permissions to list instances. For launch, add{' '}
          <span className="font-mono">ec2:RunInstances</span> and related permissions, or{' '}
          <span className="font-mono">ssm:SendCommand</span> for bootstrap on existing instances.
        </p>
      ) : provider === CloudProvider.Azure ? (
        <p className="text-xs text-gray-700">
          Azure AD app registration (service principal) with read access to list VMs. For Run Command
          bootstrap, grant <span className="font-mono">Microsoft.Compute/virtualMachines/runCommand/action</span>{' '}
          (Virtual Machine Contributor on the VM or resource group).
        </p>
      ) : (
        <p className="text-xs text-gray-700">
          Service account JSON with Compute Engine access. Read scope lists VMs; create scope is required
          for Launch via platform.
        </p>
      )}

      <div>
        <label htmlFor={`${idPrefix}-label`} className="block text-xs font-medium text-gray-800">
          Label
        </label>
        <input
          id={`${idPrefix}-label`}
          type="text"
          value={linkLabel}
          disabled={disabled || linkMutation.isPending}
          onChange={(event) => setLinkLabel(event.target.value)}
          className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
        />
      </div>

      {isApiTokenProvider(provider) ? (
        <>
          <div>
            <label htmlFor={`${idPrefix}-token`} className="block text-xs font-medium text-gray-800">
              {provider === CloudProvider.Vultr ? 'API key' : 'API token'}
            </label>
            <input
              id={`${idPrefix}-token`}
              type="password"
              autoComplete="off"
              value={linkToken}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkToken(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          {(provider === CloudProvider.Hetzner || provider === CloudProvider.Vultr) && (
            <div>
              <label htmlFor={`${idPrefix}-default-region-token`} className="block text-xs font-medium text-gray-800">
                {regionFilterLabel}
              </label>
              <input
                id={`${idPrefix}-default-region-token`}
                type="text"
                placeholder={regionFilterPlaceholder}
                value={linkDefaultRegion}
                disabled={disabled || linkMutation.isPending}
                onChange={(event) => setLinkDefaultRegion(event.target.value)}
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
              <p className="mt-1 text-[11px] text-gray-500">{regionFilterHint}</p>
            </div>
          )}
        </>
      ) : provider === CloudProvider.Aws ? (
        <>
          <div>
            <label htmlFor={`${idPrefix}-access-key-id`} className="block text-xs font-medium text-gray-800">
              Access key ID
            </label>
            <input
              id={`${idPrefix}-access-key-id`}
              type="text"
              autoComplete="off"
              value={linkAccessKeyId}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkAccessKeyId(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label htmlFor={`${idPrefix}-secret-access-key`} className="block text-xs font-medium text-gray-800">
              Secret access key
            </label>
            <input
              id={`${idPrefix}-secret-access-key`}
              type="password"
              autoComplete="off"
              value={linkSecretAccessKey}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkSecretAccessKey(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label htmlFor={`${idPrefix}-default-region`} className="block text-xs font-medium text-gray-800">
              {regionFilterLabel}
            </label>
            <input
              id={`${idPrefix}-default-region`}
              type="text"
              placeholder={regionFilterPlaceholder}
              value={linkDefaultRegion}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkDefaultRegion(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
            <p className="mt-1 text-[11px] text-gray-500">{regionFilterHint}</p>
          </div>
        </>
      ) : provider === CloudProvider.Azure ? (
        <>
          <div>
            <label htmlFor={`${idPrefix}-azure-tenant-id`} className="block text-xs font-medium text-gray-800">
              Tenant ID
            </label>
            <input
              id={`${idPrefix}-azure-tenant-id`}
              type="text"
              autoComplete="off"
              value={linkAzureTenantId}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkAzureTenantId(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label htmlFor={`${idPrefix}-azure-client-id`} className="block text-xs font-medium text-gray-800">
              Application (client) ID
            </label>
            <input
              id={`${idPrefix}-azure-client-id`}
              type="text"
              autoComplete="off"
              value={linkAzureClientId}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkAzureClientId(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label htmlFor={`${idPrefix}-azure-client-secret`} className="block text-xs font-medium text-gray-800">
              Client secret
            </label>
            <input
              id={`${idPrefix}-azure-client-secret`}
              type="password"
              autoComplete="off"
              value={linkAzureClientSecret}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkAzureClientSecret(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label htmlFor={`${idPrefix}-azure-subscription-id`} className="block text-xs font-medium text-gray-800">
              Subscription ID
            </label>
            <input
              id={`${idPrefix}-azure-subscription-id`}
              type="text"
              autoComplete="off"
              value={linkAzureSubscriptionId}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkAzureSubscriptionId(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label htmlFor={`${idPrefix}-default-location`} className="block text-xs font-medium text-gray-800">
              {regionFilterLabel}
            </label>
            <input
              id={`${idPrefix}-default-location`}
              type="text"
              placeholder={regionFilterPlaceholder}
              value={linkDefaultRegion}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkDefaultRegion(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
            <p className="mt-1 text-[11px] text-gray-500">{regionFilterHint}</p>
          </div>
        </>
      ) : (
        <>
          <div>
            <label htmlFor={`${idPrefix}-service-account-json`} className="block text-xs font-medium text-gray-800">
              Service account JSON key
            </label>
            <textarea
              id={`${idPrefix}-service-account-json`}
              rows={6}
              spellCheck={false}
              value={linkServiceAccountJson}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkServiceAccountJson(event.target.value)}
              placeholder='{"type":"service_account","project_id":"...","private_key":"..."}'
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-xs shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label htmlFor={`${idPrefix}-default-zone`} className="block text-xs font-medium text-gray-800">
              {regionFilterLabel}
            </label>
            <input
              id={`${idPrefix}-default-zone`}
              type="text"
              placeholder={regionFilterPlaceholder}
              value={linkDefaultRegion}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkDefaultRegion(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
            <p className="mt-1 text-[11px] text-gray-500">{regionFilterHint}</p>
          </div>
        </>
      )}

      {linkError ? <p className="text-xs text-red-700">{linkError}</p> : null}

      <button
        type="button"
        disabled={disabled || linkMutation.isPending || !canSaveLink}
        onClick={() => void linkMutation.mutate()}
        className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-blue-700 disabled:opacity-60"
      >
        {linkMutation.isPending ? (
          <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
        ) : null}
        Link account
      </button>
    </div>
  )
}

export type { LinkProvider as CloudLinkProvider }
