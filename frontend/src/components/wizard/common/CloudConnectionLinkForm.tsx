import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ExternalLink, Loader2 } from 'lucide-react'
import { cloudApi } from '@/services/api'
import { CloudProvider, type CloudProviderConnectionDto } from '@/types/stack.types'
import { cn } from '@/lib/utils'
import { CloudProviderLoginButton } from '@/components/wizard/common/CloudProviderLoginButton'
import { ExperimentalVpcProviderWarning } from '@/components/wizard/common/ExperimentalVpcProviderWarning'

/** Opens the signed-in user's IAM security credentials page (Access keys → Create access key). */
const AWS_CREATE_ACCESS_KEY_URL = 'https://console.aws.amazon.com/iam/home#/security_credentials'

/** Opens the IAM create-user wizard if they want a dedicated user instead of root keys. */
const AWS_CREATE_IAM_USER_URL = 'https://console.aws.amazon.com/iamv2/home#/users/create'

const DIGITALOCEAN_TOKENS_URL = 'https://cloud.digitalocean.com/account/api/tokens'
const DIGITALOCEAN_TOKEN_DOCS_URL = 'https://docs.digitalocean.com/reference/api/create-personal-access-token/'

const VULTR_API_KEYS_URL = 'https://my.vultr.com/settings/#settingsapi'
const VULTR_OAUTH_APPS_URL = 'https://my.vultr.com/oauth/'
const VULTR_OAUTH_DOCS_URL = 'https://www.vultr.com/docs/vultr-oauth-2-0/'

const GCP_SERVICE_ACCOUNTS_URL = 'https://console.cloud.google.com/iam-admin/serviceaccounts'
const GCP_CREATE_KEY_DOCS_URL = 'https://cloud.google.com/iam/docs/keys-create-delete#creating'

const AZURE_APP_REGISTRATIONS_URL = 'https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade'
const AZURE_SUBSCRIPTIONS_URL = 'https://portal.azure.com/#view/Microsoft_Azure_Billing/SubscriptionsBlade'
const AZURE_TENANT_OVERVIEW_URL = 'https://portal.azure.com/#view/Microsoft_AAD_IAM/ActiveDirectoryMenuBlade/~/Overview'

type LinkProvider =
  | CloudProvider.DigitalOcean
  | CloudProvider.Aws
  | CloudProvider.Gcp
  | CloudProvider.Azure
  | CloudProvider.Hetzner
  | CloudProvider.Vultr

function usesManualCredentialForm(provider: LinkProvider): boolean {
  return provider === CloudProvider.Aws
    || provider === CloudProvider.DigitalOcean
    || provider === CloudProvider.Vultr
    || provider === CloudProvider.Gcp
    || provider === CloudProvider.Azure
}

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

function CredentialDocLink({ href, children }: { href: string; children: ReactNode }) {
  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      className="inline-flex items-center gap-1 rounded-md border border-blue-200 bg-blue-50 px-2.5 py-1.5 text-[11px] font-medium text-blue-900 hover:bg-blue-100"
    >
      {children}
      <ExternalLink className="h-3 w-3" aria-hidden="true" />
    </a>
  )
}

function CredentialFieldLabel({
  htmlFor,
  children,
  href,
  linkLabel,
}: {
  htmlFor: string
  children: ReactNode
  href: string
  linkLabel: string
}) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-2">
      <label htmlFor={htmlFor} className="block text-xs font-medium text-gray-800">
        {children}
      </label>
      <CredentialDocLink href={href}>{linkLabel}</CredentialDocLink>
    </div>
  )
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
  /** Wizard connect step: hide the label field. Credentials still show like AWS. */
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
  const [linkVultrClientId, setLinkVultrClientId] = useState('')
  const [linkVultrProviderId, setLinkVultrProviderId] = useState('')
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
      setLinkVultrClientId('')
      setLinkVultrProviderId('')
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
          : provider === CloudProvider.Vultr
            ? linkVultrClientId.trim().length > 0
              && linkToken.trim().length > 0
              && linkVultrProviderId.trim().length > 0
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
            : provider === CloudProvider.DigitalOcean
              ? 'e.g. nyc1'
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

  const oauthSignInAvailable = Boolean(providerStatus?.isConfigured)
  const showLoginButton =
    Boolean(linkedConnection)
    || provider === CloudProvider.Hetzner
    || (oauthSignInAvailable && !usesManualCredentialForm(provider))
  const showManualForm = usesManualCredentialForm(provider) && !linkedConnection

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

      {!fixedProvider ? <ExperimentalVpcProviderWarning provider={provider} /> : null}

      {provider === CloudProvider.DigitalOcean ? (
        <p className="text-xs text-gray-700">
          Log in to DigitalOcean, create an API token, then paste it below. Read lists droplets; write is
          required to launch and attach a Cloud Firewall.
        </p>
      ) : provider === CloudProvider.Hetzner ? (
        <p className="text-xs text-gray-700">
          Connect Hetzner with a <span className="font-medium">Read & Write</span> project token from
          Console → Security → API tokens. This is an API token connection, not OAuth.
        </p>
      ) : provider === CloudProvider.Vultr ? (
        <p className="text-xs text-gray-700">
          Log in to Vultr, copy Client ID, Client secret, and Provider ID, then paste them below. Use each
          field's link to open the matching page.
        </p>
      ) : provider === CloudProvider.Aws ? (
        <p className="text-xs text-gray-700">
          Log in to AWS, create an access key, then paste it below. Use each field's link to open the
          matching page.
        </p>
      ) : provider === CloudProvider.Azure ? (
        <p className="text-xs text-gray-700">
          Log in to Azure, create a service principal, then paste tenant, client, secret, and subscription
          below. Network Contributor (or equivalent) on the VM resource group is required to apply NSG rules.
        </p>
      ) : (
        <p className="text-xs text-gray-700">
          Log in to Google Cloud, download a service account JSON key, then paste it below. Compute Engine
          must be enabled on the project.
        </p>
      )}

      {showLoginButton ? (
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

      {showManualForm && provider === CloudProvider.Aws ? (
        <>
          <p className="text-[11px] text-gray-500">
            Grant at least EC2 read (<span className="font-mono">ec2:Describe*</span>). Add launch and SSM
            permissions if you will create a VM from the wizard.
          </p>
          <div>
            <CredentialFieldLabel
              htmlFor={`${idPrefix}-access-key-id`}
              href={AWS_CREATE_ACCESS_KEY_URL}
              linkLabel="Where to find this"
            >
              Access key ID
            </CredentialFieldLabel>
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
            <CredentialFieldLabel
              htmlFor={`${idPrefix}-secret-access-key`}
              href={AWS_CREATE_IAM_USER_URL}
              linkLabel="Where to find this"
            >
              Secret access key
            </CredentialFieldLabel>
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
            <label htmlFor={`${idPrefix}-default-region-aws`} className="block text-xs font-medium text-gray-800">
              {regionFilterLabel}
            </label>
            <input
              id={`${idPrefix}-default-region-aws`}
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
      ) : null}

      {showManualForm && provider === CloudProvider.DigitalOcean ? (
        <>
          <div>
            <CredentialFieldLabel
              htmlFor={`${idPrefix}-token`}
              href={DIGITALOCEAN_TOKENS_URL}
              linkLabel="Where to find this"
            >
              API token
            </CredentialFieldLabel>
            <input
              id={`${idPrefix}-token`}
              type="password"
              autoComplete="off"
              value={linkToken}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkToken(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
            <p className="mt-1 text-[11px] text-gray-500">
              Create a personal access token with read to list droplets and write to launch and attach a Cloud
              Firewall.{' '}
              <a
                href={DIGITALOCEAN_TOKEN_DOCS_URL}
                target="_blank"
                rel="noopener noreferrer"
                className="font-medium text-blue-800 underline-offset-2 hover:underline"
              >
                Token docs
              </a>
            </p>
          </div>
          <div>
            <label htmlFor={`${idPrefix}-default-region-do`} className="block text-xs font-medium text-gray-800">
              {regionFilterLabel}
            </label>
            <input
              id={`${idPrefix}-default-region-do`}
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
      ) : null}

      {showManualForm && provider === CloudProvider.Vultr ? (
        <>
          <div>
            <CredentialFieldLabel
              htmlFor={`${idPrefix}-vultr-client-id`}
              href={VULTR_OAUTH_APPS_URL}
              linkLabel="Where to find this"
            >
              Client ID
            </CredentialFieldLabel>
            <input
              id={`${idPrefix}-vultr-client-id`}
              type="text"
              autoComplete="off"
              value={linkVultrClientId}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkVultrClientId(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <CredentialFieldLabel
              htmlFor={`${idPrefix}-vultr-client-secret`}
              href={VULTR_API_KEYS_URL}
              linkLabel="Where to find this"
            >
              Client secret
            </CredentialFieldLabel>
            <input
              id={`${idPrefix}-vultr-client-secret`}
              type="password"
              autoComplete="off"
              value={linkToken}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkToken(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
            <p className="mt-1 text-[11px] text-gray-500">
              Paste the API key from Account → API. That key is what the platform uses to list and launch
              instances.
            </p>
          </div>
          <div>
            <CredentialFieldLabel
              htmlFor={`${idPrefix}-vultr-provider-id`}
              href={VULTR_OAUTH_DOCS_URL}
              linkLabel="Where to find this"
            >
              Provider ID
            </CredentialFieldLabel>
            <input
              id={`${idPrefix}-vultr-provider-id`}
              type="text"
              autoComplete="off"
              value={linkVultrProviderId}
              disabled={disabled || linkMutation.isPending}
              onChange={(event) => setLinkVultrProviderId(event.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label htmlFor={`${idPrefix}-default-region-vultr`} className="block text-xs font-medium text-gray-800">
              {regionFilterLabel}
            </label>
            <input
              id={`${idPrefix}-default-region-vultr`}
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
      ) : null}

      {showManualForm && provider === CloudProvider.Gcp ? (
        <>
          <div>
            <CredentialFieldLabel
              htmlFor={`${idPrefix}-service-account-json`}
              href={GCP_SERVICE_ACCOUNTS_URL}
              linkLabel="Where to find this"
            >
              Service account JSON key
            </CredentialFieldLabel>
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
            <p className="mt-1 text-[11px] text-gray-500">
              Create a service account, download a JSON key, then paste the file contents.{' '}
              <a
                href={GCP_CREATE_KEY_DOCS_URL}
                target="_blank"
                rel="noopener noreferrer"
                className="font-medium text-blue-800 underline-offset-2 hover:underline"
              >
                Create JSON key
              </a>
            </p>
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
      ) : null}

      {showManualForm && provider === CloudProvider.Azure ? (
        <>
          <div>
            <CredentialFieldLabel
              htmlFor={`${idPrefix}-azure-tenant-id`}
              href={AZURE_TENANT_OVERVIEW_URL}
              linkLabel="Where to find this"
            >
              Tenant ID
            </CredentialFieldLabel>
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
            <CredentialFieldLabel
              htmlFor={`${idPrefix}-azure-client-id`}
              href={AZURE_APP_REGISTRATIONS_URL}
              linkLabel="Where to find this"
            >
              Application (client) ID
            </CredentialFieldLabel>
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
            <CredentialFieldLabel
              htmlFor={`${idPrefix}-azure-client-secret`}
              href={AZURE_APP_REGISTRATIONS_URL}
              linkLabel="Where to find this"
            >
              Client secret
            </CredentialFieldLabel>
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
            <CredentialFieldLabel
              htmlFor={`${idPrefix}-azure-subscription-id`}
              href={AZURE_SUBSCRIPTIONS_URL}
              linkLabel="Where to find this"
            >
              Subscription ID
            </CredentialFieldLabel>
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
      ) : null}

      {showManualForm ? (
        <>
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
            {provider === CloudProvider.Aws
              ? 'Connect AWS account'
              : provider === CloudProvider.DigitalOcean
                ? 'Connect DigitalOcean account'
                : provider === CloudProvider.Vultr
                  ? 'Connect Vultr account'
                  : provider === CloudProvider.Gcp
                    ? 'Connect Google Cloud account'
                    : 'Connect Azure account'}
          </button>
        </>
      ) : null}

      {provider === CloudProvider.Hetzner && !linkedConnection ? (
        <button
          type="button"
          disabled={disabled}
          onClick={() => setShowAdvanced((value) => !value)}
          className="text-left text-xs font-medium text-gray-700 underline-offset-2 hover:underline"
        >
          {showAdvanced ? 'Hide advanced credentials' : 'Advanced: paste credentials'}
        </button>
      ) : null}

      {showAdvanced && provider === CloudProvider.Hetzner ? (
        <>
          <div>
            <label htmlFor={`${idPrefix}-token`} className="block text-xs font-medium text-gray-800">
              API token
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
