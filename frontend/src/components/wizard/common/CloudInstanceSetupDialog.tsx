import { useEffect, useId, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Check, Loader2, RefreshCw, X } from 'lucide-react'
import { CloudLaunchPanel } from '@/components/wizard/common/CloudLaunchPanel'
import { cloudApi } from '@/services/api'
import { connectionStatusLine, providerDisplayName } from '@/lib/cloud-auth'
import { resolvePublicAdminSourceCidr } from '@/lib/public-ip'
import type { CloudInstanceDto, CloudLaunchResultDto, CloudProviderConnectionDto } from '@/types/stack.types'
import { cn } from '@/lib/utils'

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

  const { data: setup, isLoading: loadingSetup } = useQuery({
    queryKey: ['cloud-setup-dialog', connectionId],
    queryFn: async () => (await cloudApi.getSetupDialog(connectionId)).data,
    enabled: open && connectionId.length > 0,
  })

  const {
    data: instances,
    isLoading: loadingInstances,
    isFetching: fetchingInstances,
    error: instancesError,
    refetch: refetchInstances,
  } = useQuery({
    queryKey: ['cloud-instances', connectionId],
    queryFn: async () => (await cloudApi.listInstances(connectionId)).data,
    enabled: open && connectionId.length > 0 && tab === 'existing',
  })

  useEffect(() => {
    if (!open) {
      return
    }

    setSelectedInstanceId('')
    setTab('create')
    setAutoFirewall(setup?.autoFirewallDefault ?? true)
    setAdminCidr(setup?.suggestedAdminCidr ?? '')
  }, [open, connectionId, setup?.autoFirewallDefault, setup?.suggestedAdminCidr])

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

  if (!open || !connection) {
    return null
  }

  const handleSelectExisting = () => {
    if (!selectedInstance) {
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
              Create new VM
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
                  then Refresh — or create a new VM.
                </p>
              ) : (
                <p className="mt-1 text-xs text-gray-500">
                  Stopped instances are omitted (they have no public SSH yet). After you select, the next
                  wizard steps install Docker and apply host security over SSH.
                </p>
              )}
            </div>
          ) : (
            <CloudLaunchPanel
              disabled={disabled}
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
              disabled={disabled || !selectedInstance}
              onClick={handleSelectExisting}
              className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-60"
            >
              <Check className="h-4 w-4" aria-hidden="true" />
              Select
            </button>
          ) : null}
        </div>
      </div>
    </div>
  )
}
