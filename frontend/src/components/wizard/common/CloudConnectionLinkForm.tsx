import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ExternalLink, Loader2 } from 'lucide-react'
import { cloudApi } from '@/services/api'
import { CloudLoginMode, CloudProvider, type CloudProviderConnectionDto } from '@/types/stack.types'
import { cn } from '@/lib/utils'
import { CloudProviderLoginButton } from '@/components/wizard/common/CloudProviderLoginButton'

/** Opens the signed-in user's IAM security credentials page (Access keys → Create access key). */
const AWS_CREATE_ACCESS_KEY_URL = 'https://console.aws.amazon.com/iam/home#/security_credentials'

/** Opens the IAM create-user wizard if they want a dedicated user instead of root keys. */
const AWS_CREATE_IAM_USER_URL = 'https://console.aws.amazon.com/iamv2/home#/users/create'

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
  onDisconnected?: () => void
  className?: string
  idPrefix?: string
  linkedConnection?: CloudProviderConnectionDto | null
  /** Wizard connect step: login first, hide label and credential paste until Advanced. */
  simple?: boolean
}

export function CloudConnectionLinkForm({
  disabled = false,
  provider: fixedProvider,
  onLinked,
  onDisconnected,
  className,
  idPrefix = 'cloud-link',
  linkedConnection = null,
  simple = false,
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
  const [showAdvanced, setShowAdvanced] = useState(false)

  const { data: authProviders } = useQuery({
    queryKey: ['cloud-auth-providers'],
    queryFn: async () => (await cloudApi.listAuthProviders()).data,
  })

  const providerStatus = useMemo(
    () => authProviders?.find((item) => item.provider === provider),
    [authProviders, provider]
  )

  useEffect(() => {
    if (fixedProvider) {
      setProvider(fixedProvider)
    }
  }, [fixedProvider])

  useEffect(() => {
    setLinkLabel(defaultLinkLabel(provider))
    setLinkError(null)
    setShowAdvanced(false)
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

      {!simple ? (
        provider === CloudProvider.DigitalOcean ? (
        <p className="text-xs text-gray-700">
          Personal access token from DigitalOcean → API → Tokens/Keys. Prefer Sign in with DigitalOcean
          when an OAuth app is configured. If you paste a token, use a <span className="font-medium">dedicated</span>{' '}
          token (not the team owner&apos;s unrestricted key). Read lists droplets; write is required to
          launch and attach a Cloud Firewall.
        </p>
      ) : provider === CloudProvider.Hetzner ? (
        <p className="text-xs text-gray-700">
          Connect Hetzner project with a <span className="font-medium">Read & Write</span> token from
          Console → Security → API tokens. Read-only tokens cannot manage Cloud Firewalls. Use a{' '}
          <span className="font-medium">dedicated project token</span> — not the Hetzner account password.
          Launch and pick apply firewall <span className="font-mono">azeroth-platform-{'{id}'}</span>{' '}
          (never MySQL 3306 or SOAP 7878).
        </p>
      ) : provider === CloudProvider.Vultr ? (
        <p className="text-xs text-gray-700">
          API key from Vultr → Account → API. Prefer Sign in with Vultr when an OAuth app is configured.
          If you paste a key, use a <span className="font-medium">dedicated</span> key (not the account
          root key). Read lists instances; write is required to launch and attach a firewall group.
        </p>
      ) : provider === CloudProvider.Aws ? (
        <p className="text-xs text-gray-700">
          Paste an IAM access key. AWS has no one-click login that grants EC2 access to a third-party app.
        </p>
      ) : provider === CloudProvider.Azure ? (
        <p className="text-xs text-gray-700">
          Prefer Sign in with Microsoft when an Entra app is configured. Use a dedicated service principal
          for Advanced — not the tenant Global Admin. NSG write needs Network Contributor (or equivalent)
          on the VM resource group. Create VM from the platform is coming soon; pick an existing Linux VM
          to Run Command bootstrap and apply NSG rules (never MySQL 3306 or SOAP 7878).
        </p>
      ) : (
        <p className="text-xs text-gray-700">
          Prefer Sign in with Google Cloud when an OAuth client is configured. Use a dedicated service
          account JSON for Advanced — not your org owner key. Compute Engine must be enabled on the
          customer project. Launch tags the VM <span className="font-mono">azeroth-platform</span> and
          applies VPC firewall rules.
        </p>
      )
      ) : provider === CloudProvider.Aws ? (
        <p className="text-xs text-gray-700">
          Log in to AWS, create an access key, then paste it below. Nothing is stored in appsettings.
        </p>
      ) : provider === CloudProvider.Hetzner ? (
        <p className="text-xs text-gray-700">
          Click <span className="font-medium">Connect Hetzner project</span> and paste a Read & Write
          project token. This is an API token connection, not OAuth.
        </p>
      ) : (
        <p className="text-xs text-gray-700">
          Click the button below to connect this provider. Credentials are stored encrypted on the platform.
        </p>
      )}

      {provider !== CloudProvider.Aws || linkedConnection ? (
      <CloudProviderLoginButton
        provider={provider}
        status={providerStatus}
        disabled={disabled}
        label={linkLabel}
        linkedConnection={linkedConnection}
        onLinked={onLinked}
        onDisconnected={onDisconnected}
        onRequiresManualCredentials={() => setShowAdvanced(true)}
      />
      ) : null}

      {provider === CloudProvider.Aws && !linkedConnection ? (
        <div className="space-y-3 rounded-md border border-gray-200 bg-white p-3">
          <p className="text-[11px] text-gray-600">
            Sign in to AWS first, then open one of these pages to create the key pair:
          </p>
          <div className="flex flex-wrap gap-2">
            <a
              href={AWS_CREATE_ACCESS_KEY_URL}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1 rounded-md border border-blue-200 bg-blue-50 px-2.5 py-1.5 text-[11px] font-medium text-blue-900 hover:bg-blue-100"
            >
              Create access key
              <ExternalLink className="h-3 w-3" aria-hidden="true" />
            </a>
            <a
              href={AWS_CREATE_IAM_USER_URL}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1 rounded-md border border-gray-200 bg-white px-2.5 py-1.5 text-[11px] font-medium text-gray-800 hover:bg-gray-50"
            >
              Create IAM user
              <ExternalLink className="h-3 w-3" aria-hidden="true" />
            </a>
          </div>
          <p className="text-[11px] text-gray-500">
            Grant at least EC2 read (<span className="font-mono">ec2:Describe*</span>). Add launch and SSM
            permissions if you will create a VM from the wizard.
          </p>
        </div>
      ) : null}

      {!simple ? (
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
      ) : null}

      {provider === CloudProvider.Aws && !linkedConnection ? (
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
              placeholder="AKIA…"
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
            Connect AWS account
          </button>
          {providerStatus?.loginMode === CloudLoginMode.AssumedRole && providerStatus.isConfigured ? (
            <CloudProviderLoginButton
              provider={provider}
              status={providerStatus}
              disabled={disabled}
              label={linkLabel}
              onLinked={onLinked}
            />
          ) : null}
        </>
      ) : null}

      {!(simple && linkedConnection) && provider !== CloudProvider.Aws ? (
      <button
        type="button"
        disabled={disabled}
        onClick={() => setShowAdvanced((value) => !value)}
        className="text-left text-xs font-medium text-gray-700 underline-offset-2 hover:underline"
      >
        {showAdvanced ? 'Hide advanced credentials' : 'Advanced: paste credentials'}
      </button>
      ) : null}

      {showAdvanced && provider !== CloudProvider.Aws ? (
        <>
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
        </>
      ) : null}
    </div>
  )
}

export type { LinkProvider as CloudLinkProvider }
