import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ChevronDown, ChevronUp, Loader2, Rocket } from 'lucide-react'
import { cloudApi } from '@/services/api'
import {
  CloudLaunchMode,
  CloudProvider,
  type CloudLaunchCatalogOptionDto,
  type CloudLaunchResultDto,
} from '@/types/stack.types'
import { cn } from '@/lib/utils'

interface CloudLaunchPanelProps {
  disabled?: boolean
  sshUser: string
  savedSshKeyId: string
  connectionId?: string
  onConnectionIdChange?: (connectionId: string) => void
  onLaunched: (result: CloudLaunchResultDto) => void
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

function withCurrentOption(
  options: CloudLaunchCatalogOptionDto[] | undefined,
  currentValue: string
): CloudLaunchCatalogOptionDto[] {
  const list = options ?? []
  if (currentValue.trim().length > 0 && !list.some((option) => option.value === currentValue)) {
    return [{ value: currentValue, label: `${currentValue} (custom)` }, ...list]
  }

  return list
}

interface CatalogFieldProps {
  id: string
  label: string
  value: string
  options: CloudLaunchCatalogOptionDto[]
  disabled?: boolean
  loading?: boolean
  onChange: (value: string) => void
  placeholder?: string
}

function CatalogField({
  id,
  label,
  value,
  options,
  disabled = false,
  loading = false,
  onChange,
  placeholder = 'Select…',
}: CatalogFieldProps) {
  if (options.length > 0) {
    return (
      <div>
        <label htmlFor={id} className="block text-xs font-medium text-gray-800">
          {label}
        </label>
        <select
          id={id}
          value={value}
          disabled={disabled || loading}
          onChange={(event) => onChange(event.target.value)}
          className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-emerald-500 disabled:opacity-60"
        >
          <option value="">{loading ? 'Loading…' : placeholder}</option>
          {options.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      </div>
    )
  }

  return (
    <div>
      <label htmlFor={id} className="block text-xs font-medium text-gray-800">
        {label}
      </label>
      <input
        id={id}
        type="text"
        value={value}
        disabled={disabled || loading}
        onChange={(event) => onChange(event.target.value)}
        className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-emerald-500"
      />
    </div>
  )
}

export function CloudLaunchPanel({
  disabled = false,
  sshUser,
  savedSshKeyId,
  connectionId: controlledConnectionId,
  onConnectionIdChange,
  onLaunched,
}: CloudLaunchPanelProps) {
  const [expanded, setExpanded] = useState(false)
  const [internalConnectionId, setInternalConnectionId] = useState('')
  const connectionId = controlledConnectionId ?? internalConnectionId
  const setConnectionId = (id: string) => {
    if (onConnectionIdChange) {
      onConnectionIdChange(id)
    } else {
      setInternalConnectionId(id)
    }
  }

  const [name, setName] = useState('azeroth-vpc')
  const [region, setRegion] = useState('')
  const [size, setSize] = useState('')
  const [image, setImage] = useState('')
  const [instanceId, setInstanceId] = useState('')
  const [awsLaunchMode, setAwsLaunchMode] = useState<'create' | 'bootstrap'>('create')
  const [generateSshKey, setGenerateSshKey] = useState(true)
  const [launchError, setLaunchError] = useState<string | null>(null)
  const [launchMessage, setLaunchMessage] = useState<string | null>(null)

  const { data: connections, isLoading: loadingConnections } = useQuery({
    queryKey: ['cloud-connections'],
    queryFn: async () => (await cloudApi.listConnections()).data,
  })

  const launchableConnections = useMemo(
    () =>
      (connections ?? []).filter(
        (connection) =>
          connection.provider === CloudProvider.DigitalOcean
          || connection.provider === CloudProvider.Hetzner
          || connection.provider === CloudProvider.Vultr
          || connection.provider === CloudProvider.Aws
          || connection.provider === CloudProvider.Gcp
          || connection.provider === CloudProvider.Azure
      ),
    [connections]
  )

  const selectedConnection = useMemo(
    () => launchableConnections.find((connection) => connection.id === connectionId) ?? null,
    [connectionId, launchableConnections]
  )

  const {
    data: defaults,
    isLoading: loadingDefaults,
    isError: defaultsError,
    error: defaultsErrorDetail,
  } = useQuery({
    queryKey: ['cloud-launch-defaults', connectionId],
    queryFn: async () => (await cloudApi.getLaunchDefaults(connectionId)).data,
    enabled: connectionId.length > 0,
  })

  const {
    data: catalog,
    isLoading: loadingCatalog,
    isError: catalogError,
    error: catalogErrorDetail,
  } = useQuery({
    queryKey: ['cloud-launch-catalog', connectionId, region],
    queryFn: async () => (await cloudApi.getLaunchCatalog(connectionId, region || undefined)).data,
    enabled: connectionId.length > 0 && defaults != null,
  })

  const isAwsAccount = selectedConnection?.provider === CloudProvider.Aws
  const isAzureAccount = selectedConnection?.provider === CloudProvider.Azure
  const isBootstrapMode =
    (isAwsAccount && awsLaunchMode === 'bootstrap')
    || (isAzureAccount && defaults?.supportsBootstrapExisting === true)
  const showCreateForm = !isBootstrapMode

  const { data: instances, isLoading: loadingInstances } = useQuery({
    queryKey: ['cloud-instances', connectionId, region],
    queryFn: async () => (await cloudApi.listInstances(connectionId, region || undefined)).data,
    enabled: connectionId.length > 0 && isBootstrapMode,
  })

  useEffect(() => {
    if (!defaults) {
      return
    }

    setRegion(defaults.region)
    setSize(defaults.size)
    setImage(defaults.image)
  }, [defaults])

  useEffect(() => {
    if (!catalog || !showCreateForm) {
      return
    }

    if (
      (isAwsAccount || selectedConnection?.provider === CloudProvider.Vultr)
      && !image.trim()
      && catalog.images.length > 0
    ) {
      setImage(catalog.images[0].value)
    }

    if (!size.trim() && catalog.sizes.length > 0) {
      const preferred = catalog.sizes.find((option) =>
        option.value === 't3.medium' || option.value === 'cx22' || option.value === 'vc2-2c-4gb')
      setSize(preferred?.value ?? catalog.sizes[0].value)
    }
  }, [catalog, image, isAwsAccount, awsLaunchMode, size, showCreateForm, selectedConnection?.provider])

  const regionOptions = useMemo(
    () => withCurrentOption(catalog?.regions, region),
    [catalog?.regions, region]
  )
  const sizeOptions = useMemo(
    () => withCurrentOption(catalog?.sizes, size),
    [catalog?.sizes, size]
  )
  const imageOptions = useMemo(
    () => withCurrentOption(catalog?.images, image),
    [catalog?.images, image]
  )

  const launchMutation = useMutation({
    mutationFn: async () => {
      if (!connectionId) {
        throw new Error('Select a linked cloud account first.')
      }

      const mode = isBootstrapMode
        ? CloudLaunchMode.BootstrapExisting
        : CloudLaunchMode.Create

      return (
        await cloudApi.launch(connectionId, {
          mode,
          name: name.trim(),
          sshUser: sshUser.trim() || defaults?.sshUser || 'ubuntu',
          region: region.trim() || undefined,
          instanceId: instanceId.trim() || undefined,
          size: size.trim() || undefined,
          image: image.trim() || undefined,
          savedSshKeyId: savedSshKeyId.trim() || undefined,
          generateSshKey: generateSshKey && !savedSshKeyId.trim(),
        })
      ).data
    },
    onSuccess: (result) => {
      setLaunchError(null)
      setLaunchMessage(result.message)
      onLaunched(result)
    },
    onError: (error: unknown) => {
      setLaunchMessage(null)
      setLaunchError(extractErrorMessage(error, 'Cloud launch failed.'))
    },
  })

  const regionLabel =
    selectedConnection?.provider === CloudProvider.Gcp
      ? 'Zone'
      : selectedConnection?.provider === CloudProvider.Azure
        || selectedConnection?.provider === CloudProvider.Hetzner
        ? 'Location'
        : 'Region'
  const sizeLabel =
    selectedConnection?.provider === CloudProvider.Gcp
      ? 'Machine type'
      : selectedConnection?.provider === CloudProvider.Aws
        ? 'Instance type'
        : selectedConnection?.provider === CloudProvider.Hetzner
          ? 'Server type'
          : selectedConnection?.provider === CloudProvider.Vultr
            ? 'Plan'
            : 'Size'
  const imageLabel =
    selectedConnection?.provider === CloudProvider.Aws
      ? 'AMI'
      : selectedConnection?.provider === CloudProvider.Vultr
        ? 'Operating system'
        : 'Image'

  const handleRegionChange = (nextRegion: string) => {
    setRegion(nextRegion)
    if (
      selectedConnection?.provider === CloudProvider.Gcp
      || selectedConnection?.provider === CloudProvider.Aws
      || selectedConnection?.provider === CloudProvider.Hetzner
      || selectedConnection?.provider === CloudProvider.Vultr
    ) {
      setSize('')
      if (selectedConnection?.provider === CloudProvider.Aws) {
        setImage('')
      }
    }
    setInstanceId('')
    setLaunchError(null)
    setLaunchMessage(null)
  }

  return (
    <div className="rounded-lg border border-emerald-200 bg-emerald-50/60">
      <button
        type="button"
        disabled={disabled}
        onClick={() => setExpanded((value) => !value)}
        className="flex w-full items-center justify-between gap-3 px-4 py-3 text-left disabled:opacity-60"
      >
        <span className="flex items-center gap-2 text-sm font-medium text-emerald-950">
          <Rocket className="h-4 w-4 shrink-0" aria-hidden="true" />
          Launch via platform — new server (optional)
        </span>
        {expanded ? (
          <ChevronUp className="h-4 w-4 text-emerald-700" aria-hidden="true" />
        ) : (
          <ChevronDown className="h-4 w-4 text-emerald-700" aria-hidden="true" />
        )}
      </button>

      {expanded ? (
        <div className="space-y-3 border-t border-emerald-200 px-4 py-3">
          <p className="text-xs text-emerald-900">
            <span className="font-medium">Different from “Pick from cloud account” above:</span> this creates a{' '}
            <span className="font-medium">new</span> VM on DigitalOcean, Hetzner, Vultr, AWS, or GCP (bootstrap script injected
            automatically), bootstraps an <span className="font-medium">existing</span> AWS instance via SSM, or an
            Azure VM via Run Command. You must link an account first (above) — the same linked account is used here.
          </p>

          {loadingConnections ? (
            <div className="flex items-center gap-2 text-xs text-emerald-900">
              <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
              Loading linked accounts…
            </div>
          ) : launchableConnections.length === 0 ? (
            <p className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-950">
              No linked cloud accounts yet. Expand <span className="font-medium">Pick from cloud account</span>{' '}
              above, choose a provider tab (DigitalOcean, AWS, GCP, or Azure), and click{' '}
              <span className="font-medium">Link …</span> to add your credentials. Then return here to launch.
            </p>
          ) : (
            <div>
              <label htmlFor="launch-connection" className="block text-xs font-medium text-emerald-950">
                Linked account
              </label>
              <select
                id="launch-connection"
                value={connectionId}
                disabled={disabled || launchMutation.isPending}
                onChange={(event) => {
                  setConnectionId(event.target.value)
                  setInstanceId('')
                  setLaunchError(null)
                  setLaunchMessage(null)
                }}
                className="mt-1 block w-full rounded-md border border-emerald-200 bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-emerald-500 disabled:opacity-60"
              >
                <option value="">Select a linked account…</option>
                {launchableConnections.map((connection) => (
                  <option key={connection.id} value={connection.id}>
                    {connection.label} ({connection.provider})
                  </option>
                ))}
              </select>
              {connectionId && controlledConnectionId === connectionId ? (
                <p className="mt-1 text-[11px] text-emerald-800">
                  Using the account selected in Pick from cloud account above.
                </p>
              ) : null}
            </div>
          )}

          {connectionId && loadingDefaults ? (
            <div className="flex items-center gap-2 text-xs text-emerald-900">
              <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
              Loading launch options…
            </div>
          ) : null}

          {connectionId && defaultsError ? (
            <p className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-800">
              Could not load launch options:{' '}
              {extractErrorMessage(defaultsErrorDetail, 'Is the API running with the latest cloud integration?')}
            </p>
          ) : null}

          {connectionId && defaults && catalogError ? (
            <p className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-950">
              Could not load catalog from provider ({extractErrorMessage(catalogErrorDetail, 'check credentials scope')}). You can still type values manually.
            </p>
          ) : null}

          {connectionId && defaults ? (
            <>
              {isAwsAccount && defaults.supportsCreate && defaults.supportsBootstrapExisting ? (
                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    disabled={disabled || launchMutation.isPending}
                    onClick={() => {
                      setAwsLaunchMode('create')
                      setInstanceId('')
                      setLaunchError(null)
                      setLaunchMessage(null)
                    }}
                    className={cn(
                      'rounded-md border px-2.5 py-1 text-[11px] font-medium',
                      awsLaunchMode === 'create'
                        ? 'border-emerald-500 bg-white text-emerald-900'
                        : 'border-emerald-200 bg-emerald-100/50 text-emerald-800 hover:bg-emerald-100'
                    )}
                  >
                    Create new EC2
                  </button>
                  <button
                    type="button"
                    disabled={disabled || launchMutation.isPending}
                    onClick={() => {
                      setAwsLaunchMode('bootstrap')
                      setLaunchError(null)
                      setLaunchMessage(null)
                    }}
                    className={cn(
                      'rounded-md border px-2.5 py-1 text-[11px] font-medium',
                      awsLaunchMode === 'bootstrap'
                        ? 'border-emerald-500 bg-white text-emerald-900'
                        : 'border-emerald-200 bg-emerald-100/50 text-emerald-800 hover:bg-emerald-100'
                    )}
                  >
                    Bootstrap existing (SSM)
                  </button>
                </div>
              ) : null}

              {showCreateForm ? (
                <div className="grid gap-3 sm:grid-cols-2">
                  <div>
                    <label htmlFor="launch-name" className="block text-xs font-medium text-gray-800">
                      Server name
                    </label>
                    <input
                      id="launch-name"
                      type="text"
                      value={name}
                      disabled={disabled || launchMutation.isPending}
                      onChange={(event) => setName(event.target.value)}
                      className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-emerald-500"
                    />
                  </div>
                  <CatalogField
                    id="launch-region"
                    label={regionLabel}
                    value={region}
                    options={regionOptions}
                    disabled={disabled || launchMutation.isPending}
                    loading={loadingCatalog}
                    onChange={handleRegionChange}
                    placeholder={`Select ${regionLabel.toLowerCase()}…`}
                  />
                  <CatalogField
                    id="launch-size"
                    label={sizeLabel}
                    value={size}
                    options={sizeOptions}
                    disabled={disabled || launchMutation.isPending}
                    loading={loadingCatalog}
                    onChange={setSize}
                    placeholder={`Select ${sizeLabel.toLowerCase()}…`}
                  />
                  <CatalogField
                    id="launch-image"
                    label={imageLabel}
                    value={image}
                    options={imageOptions}
                    disabled={disabled || launchMutation.isPending}
                    loading={loadingCatalog}
                    onChange={setImage}
                    placeholder={`Select ${imageLabel.toLowerCase()}…`}
                  />
                </div>
              ) : (
                <div className="space-y-2">
                  <p className="text-xs text-gray-700">
                    {isAzureAccount ? (
                      <>
                        Bootstrap an <span className="font-medium">existing</span> Azure Linux VM via Run Command (does
                        not create a new VM). The service principal needs{' '}
                        <span className="font-mono">Microsoft.Compute/virtualMachines/runCommand/action</span> on the
                        target VM.
                      </>
                    ) : (
                      <>
                        Bootstrap an <span className="font-medium">existing</span> EC2 instance via SSM (does not create a
                        new instance). The instance needs the SSM agent and instance profile{' '}
                        <span className="font-mono">AmazonSSMManagedInstanceCore</span>. Your IAM user also needs{' '}
                        <span className="font-mono">ssm:SendCommand</span>.
                      </>
                    )}
                  </p>
                  <CatalogField
                    id="launch-region-bootstrap"
                    label={isAzureAccount ? 'Azure location' : 'AWS region'}
                    value={region}
                    options={regionOptions}
                    disabled={disabled || launchMutation.isPending}
                    loading={loadingCatalog}
                    onChange={handleRegionChange}
                    placeholder={`Select ${isAzureAccount ? 'location' : 'region'}…`}
                  />
                  <div>
                    <label htmlFor="launch-instance-id" className="block text-xs font-medium text-gray-800">
                      {isAzureAccount ? 'Azure VM' : 'EC2 instance'}
                    </label>
                    <select
                      id="launch-instance-id"
                      value={instanceId}
                      disabled={disabled || launchMutation.isPending || loadingInstances}
                      onChange={(event) => setInstanceId(event.target.value)}
                      className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-emerald-500"
                    >
                      <option value="">
                        {loadingInstances ? 'Loading instances…' : 'Select an instance…'}
                      </option>
                      {(instances ?? []).map((instance) => (
                        <option key={instance.id} value={instance.id}>
                          {instance.name} — {isAzureAccount ? instance.region : instance.id} (
                          {instance.publicHost || 'no public IP'})
                        </option>
                      ))}
                    </select>
                  </div>
                </div>
              )}

              {isAwsAccount && showCreateForm ? (
                <p className="text-xs text-gray-600">
                  Uses the default VPC and creates security group{' '}
                  <span className="font-mono">azeroth-platform-launch</span> (SSH port 22) if needed. Your IAM user
                  needs <span className="font-mono">ec2:RunInstances</span> and related permissions.
                </p>
              ) : null}

              {!savedSshKeyId && showCreateForm ? (
                <label className="flex items-start gap-2 text-sm text-gray-700">
                  <input
                    type="checkbox"
                    checked={generateSshKey}
                    disabled={disabled || launchMutation.isPending}
                    onChange={(event) => setGenerateSshKey(event.target.checked)}
                    className="mt-0.5 rounded border-gray-300"
                  />
                  <span>
                    Generate SSH key and save to vault{' '}
                    <span className="block text-xs text-gray-500">
                      Required for new VMs unless you select a saved key below.
                    </span>
                  </span>
                </label>
              ) : null}

              {launchError ? <p className="text-xs text-red-700">{launchError}</p> : null}
              {launchMessage ? <p className="text-xs text-emerald-800">{launchMessage}</p> : null}

              <button
                type="button"
                disabled={
                  disabled
                  || launchMutation.isPending
                  || (isBootstrapMode
                    ? instanceId.trim().length === 0
                    : name.trim().length === 0
                      || !region.trim()
                      || !size.trim()
                      || ((isAwsAccount || selectedConnection?.provider === CloudProvider.Vultr) && !image.trim()))
                }
                onClick={() => void launchMutation.mutate()}
                className={cn(
                  'inline-flex items-center gap-2 rounded-md bg-emerald-700 px-3 py-1.5 text-xs font-semibold text-white hover:bg-emerald-800 disabled:opacity-60'
                )}
              >
                {launchMutation.isPending ? (
                  <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
                ) : (
                  <Rocket className="h-3.5 w-3.5" aria-hidden="true" />
                )}
                {isBootstrapMode
                  ? isAzureAccount
                    ? 'Bootstrap via Azure Run Command'
                    : 'Bootstrap via AWS SSM'
                  : 'Launch server'}
              </button>
            </>
          ) : connectionId ? null : launchableConnections.length > 0 ? (
            <p className="text-xs text-emerald-800">Select a linked account to see launch options.</p>
          ) : null}
        </div>
      ) : null}
    </div>
  )
}
