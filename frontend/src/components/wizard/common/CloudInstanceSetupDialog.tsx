import { useEffect, useId, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, Loader2, RefreshCw, X } from 'lucide-react'
import { CloudLaunchPanel } from '@/components/wizard/common/CloudLaunchPanel'
import { cloudApi } from '@/services/api'
import { connectionStatusLine, providerDisplayName } from '@/lib/cloud-auth'
import { resolvePublicAdminSourceCidr } from '@/lib/public-ip'
import { apiErrorMessage, cn } from '@/lib/utils'
import type { CloudInstanceDto, CloudLaunchResultDto, CloudProviderConnectionDto } from '@/types/stack.types'
import { CloudLaunchMode, CloudProvider } from '@/types/stack.types'

interface CloudInstanceSetupDialogProps {
  open: boolean
  onClose: () => void
  connection: CloudProviderConnectionDto | null
  disabled?: boolean
  sshUser: string
  savedSshKeyId: string
  onSelectInstance: (instance: CloudInstanceDto) => void
  onLaunched: (result: CloudLaunchResultDto) => void
}

type SetupTab = 'existing' | 'create'

export function CloudInstanceSetupDialog({
  open,
  onClose,
  connection,
  disabled = false,
  sshUser,
  savedSshKeyId,
  onSelectInstance,
  onLaunched,
}: CloudInstanceSetupDialogProps) {
  const titleId = useId()
  const [tab, setTab] = useState<SetupTab>('create')
  const [selectedInstanceId, setSelectedInstanceId] = useState('')
  const [autoFirewall, setAutoFirewall] = useState(true)
  const [adminCidr, setAdminCidr] = useState('')
  const [cidrHint, setCidrHint] = useState<string | null>(null)

  const connectionId = connection?.id ?? ''
  const queryClient = useQueryClient()

  const { data: setup, isLoading: loadingSetup } = useQuery({
    queryKey: ['cloud-setup-dialog', connectionId],
    queryFn: async () => (await cloudApi.getSetupDialog(connectionId)).data,
    enabled: open && connectionId.length > 0,
  })

  const selectedProjectId = setup?.defaultProjectId ?? connection?.defaultProjectId ?? ''
  const scopedResources = setup?.projects ?? []
  const isScopedProvider =
    connection?.provider === CloudProvider.Gcp || connection?.provider === CloudProvider.Azure
  const needsScope = isScopedProvider && (scopedResources.length > 1 || selectedProjectId.length === 0)
  const scopeReady = !isScopedProvider || selectedProjectId.length > 0
  const isAzure = connection?.provider === CloudProvider.Azure
  const isAws = connection?.provider === CloudProvider.Aws
  const isHetzner = connection?.provider === CloudProvider.Hetzner
  const applyFirewallOnSelect = Boolean(setup?.canBootstrapExisting)
  const hostBootstrapOnSelect = isAzure || isAws

  const {
    data: instances,
    isLoading: loadingInstances,
    isFetching: fetchingInstances,
    error: instancesError,
    refetch: refetchInstances,
  } = useQuery({
    queryKey: ['cloud-instances', connectionId, selectedProjectId],
    queryFn: async () => (await cloudApi.listInstances(connectionId)).data,
    enabled: open && connectionId.length > 0 && tab === 'existing' && scopeReady,
  })

  const selectProject = useMutation({
    mutationFn: async (projectId: string) =>
      (
        await cloudApi.completeCloudAuth(connection!.provider, {
          reconnectConnectionId: connectionId,
          defaultProjectId: projectId,
        })
      ).data,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['cloud-setup-dialog', connectionId] })
      await queryClient.invalidateQueries({ queryKey: ['cloud-instances', connectionId] })
      await queryClient.invalidateQueries({ queryKey: ['cloud-connections'] })
      await queryClient.invalidateQueries({ queryKey: ['cloud-launch-catalog', connectionId] })
    },
  })

  useEffect(() => {
    if (!open) {
      return
    }

    setSelectedInstanceId('')
    setTab(setup?.canCreate === false ? 'existing' : 'create')
    setAutoFirewall(setup?.autoFirewallDefault ?? true)
    setAdminCidr(setup?.suggestedAdminCidr ?? '')
  }, [open, connectionId, setup?.autoFirewallDefault, setup?.canCreate, setup?.suggestedAdminCidr])

  useEffect(() => {
    if (!open) {
      return
    }

    let cancelled = false
    void resolvePublicAdminSourceCidr(setup?.suggestedAdminCidr).then((cidr) => {
      if (cancelled || !cidr) {
        return
      }

      setAdminCidr((current) => current.trim() || cidr)
      setCidrHint(cidr)
    })
    return () => {
      cancelled = true
    }
  }, [open, setup?.suggestedAdminCidr])

  const selectedInstance = useMemo(
    () => (instances ?? []).find((instance) => instance.id === selectedInstanceId) ?? null,
    [instances, selectedInstanceId]
  )

  const bootstrapExisting = useMutation({
    mutationFn: async () => {
      if (!selectedInstance) {
        throw new Error('Select an instance first.')
      }

      return (
        await cloudApi.launch(connectionId, {
          mode: CloudLaunchMode.BootstrapExisting,
          name: selectedInstance.name,
          sshUser,
          region: selectedInstance.region,
          instanceId: selectedInstance.id,
          savedSshKeyId: savedSshKeyId.trim() || undefined,
          applyNetworkProfile: autoFirewall,
          adminSourceCidr: adminCidr.trim() || undefined,
        })
      ).data
    },
    onSuccess: (result) => {
      onLaunched(result)
      onClose()
    },
  })

  if (!open || !connection) {
    return null
  }

  const handleSelectExisting = () => {
    if (!selectedInstance) {
      return
    }

    if (applyFirewallOnSelect) {
      bootstrapExisting.mutate()
      return
    }

    onSelectInstance(selectedInstance)
    onClose()
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
      role="presentation"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          onClose()
        }
      }}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className="flex max-h-[90vh] w-full max-w-3xl flex-col overflow-hidden rounded-lg bg-white shadow-xl"
      >
        <div className="flex items-start justify-between border-b border-gray-200 px-5 py-4">
          <div>
            <h2 id={titleId} className="text-lg font-semibold text-gray-900">
              Set up VPC instance
            </h2>
            <p className="mt-1 text-sm text-gray-600">
              Linked account: <span className="font-medium">{connection.label}</span>
              {' '}
              ({providerDisplayName(connection.provider)}, {connectionStatusLine(connection)})
            </p>
            <p className="mt-1 text-xs text-gray-500">
              Creating a VM generates an SSH key and saves it on this platform. You do not paste a private
              key.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-md p-1 text-gray-500 hover:bg-gray-100 hover:text-gray-700"
            aria-label="Close"
          >
            <X className="h-5 w-5" aria-hidden="true" />
          </button>
        </div>

        <div className="flex-1 space-y-4 overflow-y-auto px-5 py-4">
          {loadingSetup ? (
            <div className="flex items-center gap-2 text-sm text-gray-500">
              <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
              Loading account capabilities…
            </div>
          ) : null}

          {isScopedProvider ? (
            <div>
              <label htmlFor="setup-cloud-scope" className="block text-xs font-medium text-gray-800">
                {connection.provider === CloudProvider.Azure ? 'Azure subscription' : 'Google Cloud project'}
              </label>
              <select
                id="setup-cloud-scope"
                value={selectedProjectId}
                disabled={disabled || selectProject.isPending || scopedResources.length === 0}
                onChange={(event) => {
                  const value = event.target.value
                  if (value) {
                    selectProject.mutate(value)
                  }
                }}
                className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              >
                <option value="">
                  {scopedResources.length === 0
                    ? connection.provider === CloudProvider.Azure
                      ? 'No subscriptions available'
                      : 'No projects available'
                    : connection.provider === CloudProvider.Azure
                      ? 'Select a subscription…'
                      : 'Select a project…'}
                </option>
                {scopedResources.map((project) => (
                  <option key={project.value} value={project.value}>
                    {project.label}
                  </option>
                ))}
              </select>
              {needsScope ? (
                <p className="mt-1 text-xs text-amber-800">
                  {connection.provider === CloudProvider.Azure
                    ? 'Choose the subscription that contains the Linux VM. Listing and Run Command use this subscription.'
                    : 'Choose the project that has Compute Engine enabled. Launch and instance listing use this project.'}
                </p>
              ) : null}
              {selectProject.isError ? (
                <p className="mt-1 text-xs text-red-700">
                  {connection.provider === CloudProvider.Azure
                    ? 'Could not select that subscription. Confirm the signed-in account can read it, then try again.'
                    : 'Could not select that project. Enable Compute Engine API and grant compute access, then try again.'}
                </p>
              ) : null}
            </div>
          ) : null}

          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              disabled={disabled || (setup != null && !setup.canCreate && !setup.canBootstrapExisting)}
              onClick={() => setTab('create')}
              className={cn(
                'rounded-md border px-3 py-1.5 text-xs font-medium',
                tab === 'create'
                  ? 'border-blue-500 bg-blue-50 text-blue-900'
                  : 'border-gray-300 text-gray-700 hover:bg-gray-50'
              )}
            >
              {setup?.canCreate === false ? 'Create new VM (Coming soon)' : 'Create new VM'}
            </button>
            <button
              type="button"
              disabled={disabled || setup?.canList === false}
              onClick={() => setTab('existing')}
              className={cn(
                'rounded-md border px-3 py-1.5 text-xs font-medium',
                tab === 'existing'
                  ? 'border-blue-500 bg-blue-50 text-blue-900'
                  : 'border-gray-300 text-gray-700 hover:bg-gray-50'
              )}
            >
              Use existing VM
            </button>
          </div>

          {tab === 'existing' ? (
            <div>
              <div className="flex items-center justify-between gap-2">
                <label htmlFor="setup-instance" className="block text-xs font-medium text-gray-800">
                  Running instances with a public IP
                </label>
                <button
                  type="button"
                  disabled={disabled || loadingInstances || fetchingInstances}
                  onClick={() => void refetchInstances()}
                  className="inline-flex items-center gap-1 rounded-md border border-gray-300 bg-white px-2 py-1 text-[11px] font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-60"
                >
                  <RefreshCw
                    className={cn('h-3 w-3', fetchingInstances && 'animate-spin')}
                    aria-hidden="true"
                  />
                  Refresh
                </button>
              </div>
              <select
                id="setup-instance"
                value={selectedInstanceId}
                disabled={disabled || loadingInstances}
                onChange={(event) => setSelectedInstanceId(event.target.value)}
                className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              >
                <option value="">{loadingInstances ? 'Loading instances…' : 'Select an instance…'}</option>
                {(instances ?? []).map((instance) => (
                  <option key={instance.id} value={instance.id}>
                    {instance.name} — {instance.publicHost} ({instance.region}, {instance.state})
                  </option>
                ))}
              </select>
              {instancesError ? (
                <p className="mt-1 text-xs text-red-700">Could not load instances for this account.</p>
              ) : null}
                  {!loadingInstances && (instances?.length ?? 0) === 0 ? (
                <p className="mt-1 text-xs text-gray-600">
                  No running instances with a public address were found. Start the VM in the cloud console,
                  then Refresh{setup?.canCreate ? ' — or create a new VM.' : '.'}
                </p>
              ) : (
                <p className="mt-1 text-xs text-gray-500">
                  {isAzure
                    ? 'Select bootstraps the VM via Run Command (operator user, Docker, ufw) and applies NSG inbound rules. Create VM from the platform is not available yet.'
                    : isAws
                      ? 'Select bootstraps the instance via SSM (operator user, Docker, ufw) and applies the security group. Host setup still runs over SSH on Verify / Repair if SSM is unavailable.'
                    : isHetzner
                      ? 'Select applies Hetzner Cloud Firewall inbound rules to this running server. Host Docker and ufw are installed on Create (cloud-init), or later via Verify / Repair over SSH.'
                      : applyFirewallOnSelect
                        ? 'Select applies cloud firewall / security group inbound rules to this running VM. Host Docker and ufw are installed on Create, or later via Verify / Repair over SSH.'
                        : 'Stopped instances are omitted (they have no public SSH yet). After you select, the next wizard steps install Docker and apply host security over SSH.'}
                </p>
              )}
              {bootstrapExisting.isError ? (
                <p className="mt-1 text-xs text-red-700">
                  {apiErrorMessage(
                    bootstrapExisting.error,
                    hostBootstrapOnSelect
                      ? 'Could not bootstrap this instance from the platform.'
                      : 'Could not apply cloud firewall rules on this instance.',
                  )}
                </p>
              ) : null}
            </div>
          ) : setup?.canCreate === false ? (
            <div className="rounded-md border border-gray-200 bg-gray-50 p-4">
              <p className="text-sm font-medium text-gray-900">Coming soon</p>
              <p className="mt-1 text-xs text-gray-600">
                Creating a new Azure VM from this platform is not implemented yet. Use an existing Linux VM
                with a public IP, then Select to bootstrap it with Run Command and apply NSG rules. You can
                still create the VM in Azure Portal first.
              </p>
            </div>
          ) : (
            <CloudLaunchPanel
              disabled={disabled || !scopeReady}
              sshUser={sshUser}
              savedSshKeyId={savedSshKeyId}
              connectionId={connection.id}
              embedded
              hideAccountSelect
              applyNetworkProfile={autoFirewall}
              adminSourceCidr={adminCidr}
              onLaunched={(result) => {
                onLaunched(result)
                onClose()
              }}
            />
          )}

          <div className="space-y-2 rounded-md border border-gray-200 bg-gray-50 p-3">
            <label className="flex items-start gap-2 text-sm text-gray-800">
              <input
                type="checkbox"
                checked={autoFirewall}
                disabled={disabled || setup?.canSyncFirewall === false}
                onChange={(event) => setAutoFirewall(event.target.checked)}
                className="mt-0.5 rounded border-gray-300"
              />
              <span>
                Apply network profile automatically
                <span className="mt-0.5 block text-xs text-gray-500">
                  {setup?.canSyncFirewall
                    ? 'Creates or updates cloud firewall / security group rules (SSH admin-only; game and web ports from the stack profile).'
                    : 'Automatic cloud firewall sync for this provider is not enabled yet. Rules can still be applied later from the stack overview.'}
                </span>
              </span>
            </label>
            <div>
              <label htmlFor="setup-admin-cidr" className="block text-xs font-medium text-gray-800">
                Admin SSH CIDR
              </label>
              <div className="mt-1 flex flex-wrap gap-2">
                <input
                  id="setup-admin-cidr"
                  type="text"
                  value={adminCidr}
                  disabled={disabled}
                  placeholder="203.0.113.10/32"
                  onChange={(event) => setAdminCidr(event.target.value)}
                  className="min-w-[12rem] flex-1 rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
                <button
                  type="button"
                  disabled={disabled || !cidrHint}
                  onClick={() => {
                    if (cidrHint) {
                      setAdminCidr(cidrHint)
                    }
                  }}
                  className="rounded-md border border-gray-300 bg-white px-2.5 py-1.5 text-xs font-medium text-gray-800 hover:bg-gray-50 disabled:opacity-60"
                >
                  Use my IP
                </button>
              </div>
            </div>
          </div>
        </div>

        <div className="flex flex-wrap justify-end gap-3 border-t border-gray-200 px-5 py-4">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md border border-gray-300 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50"
          >
            Cancel
          </button>
          {tab === 'existing' ? (
            <button
              type="button"
              disabled={disabled || !selectedInstance || bootstrapExisting.isPending}
              onClick={handleSelectExisting}
              className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-60"
            >
              {bootstrapExisting.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
              ) : (
                <Check className="h-4 w-4" aria-hidden="true" />
              )}
              {hostBootstrapOnSelect && setup?.canBootstrapExisting ? 'Bootstrap and select' : 'Select'}
            </button>
          ) : null}
        </div>
      </div>
    </div>
  )
}
