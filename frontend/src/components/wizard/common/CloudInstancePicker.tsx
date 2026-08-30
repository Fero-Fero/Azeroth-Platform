import { useEffect, useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronDown, ChevronUp, Cloud, Loader2, Trash2 } from 'lucide-react'
import { cloudApi } from '@/services/api'
import { CloudProvider, type CloudInstanceDto } from '@/types/stack.types'
import { cn } from '@/lib/utils'
import { CloudConnectionLinkForm } from '@/components/wizard/common/CloudConnectionLinkForm'
import { ExperimentalVpcProviderWarning } from '@/components/wizard/common/ExperimentalVpcProviderWarning'

interface CloudInstancePickerProps {
  disabled?: boolean
  connectionId?: string
  onConnectionIdChange?: (connectionId: string) => void
  onSelectInstance: (instance: CloudInstanceDto) => void
}

type PickerProvider =
  | CloudProvider.DigitalOcean
  | CloudProvider.Hetzner
  | CloudProvider.Vultr
  | CloudProvider.Aws
  | CloudProvider.Gcp
  | CloudProvider.Azure

const PROVIDER_OPTIONS: Array<{ id: PickerProvider; label: string; instanceLabel: string }> = [
  { id: CloudProvider.DigitalOcean, label: 'DigitalOcean', instanceLabel: 'Droplet' },
  { id: CloudProvider.Hetzner, label: 'Hetzner', instanceLabel: 'Server' },
  { id: CloudProvider.Vultr, label: 'Vultr', instanceLabel: 'Instance' },
  { id: CloudProvider.Aws, label: 'AWS', instanceLabel: 'EC2 instance' },
  { id: CloudProvider.Gcp, label: 'GCP', instanceLabel: 'VM instance' },
  { id: CloudProvider.Azure, label: 'Azure', instanceLabel: 'VM' },
]

function providerLabel(provider: CloudProvider): string {
  switch (provider) {
    case CloudProvider.DigitalOcean:
      return 'DigitalOcean'
    case CloudProvider.Hetzner:
      return 'Hetzner'
    case CloudProvider.Vultr:
      return 'Vultr'
    case CloudProvider.Aws:
      return 'AWS'
    case CloudProvider.Gcp:
      return 'GCP'
    case CloudProvider.Azure:
      return 'Azure'
    default:
      return provider
  }
}

export function CloudInstancePicker({
  disabled = false,
  connectionId: controlledConnectionId,
  onConnectionIdChange,
  onSelectInstance,
}: CloudInstancePickerProps) {
  const queryClient = useQueryClient()
  const [expanded, setExpanded] = useState(false)
  const [pickerProvider, setPickerProvider] = useState<PickerProvider>(CloudProvider.DigitalOcean)
  const [internalConnectionId, setInternalConnectionId] = useState('')
  const selectedConnectionId = controlledConnectionId ?? internalConnectionId
  const setSelectedConnectionId = (id: string) => {
    if (onConnectionIdChange) {
      onConnectionIdChange(id)
    } else {
      setInternalConnectionId(id)
    }
  }
  const [selectedInstanceId, setSelectedInstanceId] = useState('')
  const [showLinkForm, setShowLinkForm] = useState(false)

  const providerConfig = PROVIDER_OPTIONS.find((option) => option.id === pickerProvider) ?? PROVIDER_OPTIONS[0]

  const { data: connections, isLoading: loadingConnections } = useQuery({
    queryKey: ['cloud-connections'],
    queryFn: async () => (await cloudApi.listConnections()).data,
  })

  const providerConnections = useMemo(
    () => (connections ?? []).filter((connection) => connection.provider === pickerProvider),
    [connections, pickerProvider]
  )

  const selectedConnection = useMemo(
    () => providerConnections.find((connection) => connection.id === selectedConnectionId) ?? null,
    [providerConnections, selectedConnectionId]
  )

  const { data: instances, isLoading: loadingInstances, error: instancesError } = useQuery({
    queryKey: ['cloud-instances', selectedConnectionId],
    queryFn: async () => (await cloudApi.listInstances(selectedConnectionId)).data,
    enabled: selectedConnectionId.length > 0,
  })

  useEffect(() => {
    setSelectedConnectionId('')
    setSelectedInstanceId('')
    setShowLinkForm(false)
  }, [pickerProvider])

  const handleConnectionChange = (connectionId: string) => {
    setSelectedConnectionId(connectionId)
    setSelectedInstanceId('')
  }

  const handleInstanceChange = (instanceId: string) => {
    setSelectedInstanceId(instanceId)
    const match = instances?.find((instance) => instance.id === instanceId)
    if (match) {
      onSelectInstance(match)
    }
  }

  const handleDeleteConnection = async (id: string) => {
    await cloudApi.deleteConnection(id)
    if (selectedConnectionId === id) {
      setSelectedConnectionId('')
      setSelectedInstanceId('')
    }
    await queryClient.invalidateQueries({ queryKey: ['cloud-connections'] })
  }

  return (
    <div className="rounded-lg border border-indigo-200 bg-indigo-50/60">
      <button
        type="button"
        disabled={disabled}
        onClick={() => setExpanded((value) => !value)}
        className="flex w-full items-center justify-between gap-3 px-4 py-3 text-left disabled:opacity-60"
      >
        <span className="flex items-center gap-2 text-sm font-medium text-indigo-950">
          <Cloud className="h-4 w-4 shrink-0" aria-hidden="true" />
          Pick from cloud account - existing servers (optional)
        </span>
        {expanded ? (
          <ChevronUp className="h-4 w-4 text-indigo-700" aria-hidden="true" />
        ) : (
          <ChevronDown className="h-4 w-4 text-indigo-700" aria-hidden="true" />
        )}
      </button>

      {expanded ? (
        <div className="space-y-3 border-t border-indigo-200 px-4 py-3">
          <p className="text-xs text-indigo-900">
            Link DigitalOcean, Hetzner, Vultr, AWS, GCP, or Azure, then choose a{' '}
            <span className="font-medium">running server you already have</span> to auto-fill the host and SSH user.
            To create a new VM instead, use{' '}
            <span className="font-medium">Launch via platform</span> below (uses the same linked account).
          </p>

          <div className="flex flex-wrap gap-2">
            {PROVIDER_OPTIONS.map((option) => (
              <button
                key={option.id}
                type="button"
                disabled={disabled}
                onClick={() => setPickerProvider(option.id)}
                className={cn(
                  'rounded-md border px-2.5 py-1 text-[11px] font-medium',
                  pickerProvider === option.id
                    ? 'border-indigo-500 bg-white text-indigo-900'
                    : 'border-indigo-200 bg-indigo-100/50 text-indigo-800 hover:bg-indigo-100'
                )}
              >
                {option.label}
              </button>
            ))}
          </div>

          <ExperimentalVpcProviderWarning provider={pickerProvider} />

          <div className="flex flex-wrap items-end gap-2">
            <div className="min-w-[14rem] flex-1">
              <label htmlFor="cloud-connection" className="block text-xs font-medium text-indigo-950">
                Linked {providerLabel(pickerProvider)} account
              </label>
              <select
                id="cloud-connection"
                value={selectedConnectionId}
                disabled={disabled || loadingConnections}
                onChange={(event) => handleConnectionChange(event.target.value)}
                className="mt-1 block w-full rounded-md border border-indigo-200 bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 disabled:opacity-60"
              >
                <option value="">Select a linked account…</option>
                {providerConnections.map((connection) => (
                  <option key={connection.id} value={connection.id}>
                    {connection.label}
                    {connection.defaultRegion ? ` (${connection.defaultRegion})` : ''}
                  </option>
                ))}
              </select>
            </div>

            {loadingConnections ? (
              <Loader2 className="mb-2 h-4 w-4 animate-spin text-indigo-600" aria-hidden="true" />
            ) : null}

            {selectedConnectionId ? (
              <button
                type="button"
                disabled={disabled}
                onClick={() => void handleDeleteConnection(selectedConnectionId)}
                className="mb-0.5 inline-flex items-center gap-1 rounded-md border border-indigo-200 bg-white px-2 py-1.5 text-xs text-indigo-900 hover:bg-indigo-100 disabled:opacity-60"
              >
                <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
                Unlink
              </button>
            ) : null}

            <button
              type="button"
              disabled={disabled}
              onClick={() => setShowLinkForm((value) => !value)}
              className="mb-0.5 rounded-md border border-indigo-300 bg-white px-2.5 py-1.5 text-xs font-medium text-indigo-900 hover:bg-indigo-100 disabled:opacity-60"
            >
              {showLinkForm ? 'Cancel' : `Link ${providerLabel(pickerProvider)}`}
            </button>
          </div>

          {showLinkForm ? (
            <div className="rounded-md border border-indigo-200 bg-white p-3">
              <CloudConnectionLinkForm
                disabled={disabled}
                provider={pickerProvider}
                idPrefix="cloud-picker-link"
                onLinked={(created) => {
                  setShowLinkForm(false)
                  setSelectedConnectionId(created.id)
                  setSelectedInstanceId('')
                }}
              />
            </div>
          ) : null}

          {selectedConnectionId ? (
            <div>
              <label htmlFor="cloud-instance" className="block text-xs font-medium text-indigo-950">
                {providerConfig.instanceLabel}
              </label>
              <select
                id="cloud-instance"
                value={selectedInstanceId}
                disabled={disabled || loadingInstances}
                onChange={(event) => handleInstanceChange(event.target.value)}
                className={cn(
                  'mt-1 block w-full rounded-md border border-indigo-200 bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-indigo-500',
                  disabled && 'opacity-60'
                )}
              >
                <option value="">
                  {loadingInstances
                    ? `Loading ${providerConfig.instanceLabel.toLowerCase()}s…`
                    : `Select a ${providerConfig.instanceLabel.toLowerCase()}…`}
                </option>
                {(instances ?? []).map((instance) => (
                  <option key={instance.id} value={instance.id}>
                    {instance.name} - {instance.publicHost} ({instance.region}, {instance.state})
                  </option>
                ))}
              </select>
              {selectedConnection?.defaultRegion ? (
                <p className="mt-1 text-[11px] text-indigo-800">
                  Listing limited to{' '}
                  {pickerProvider === CloudProvider.Gcp
                    ? 'zone/region'
                    : pickerProvider === CloudProvider.Azure || pickerProvider === CloudProvider.Hetzner
                      ? 'location'
                      : 'region'}{' '}
                  <span className="font-mono">{selectedConnection.defaultRegion}</span>.
                </p>
              ) : null}
              {instancesError ? (
                <p className="mt-1 text-xs text-red-700">
                  Could not load {providerConfig.instanceLabel.toLowerCase()}s for this account.
                </p>
              ) : null}
              {!loadingInstances && selectedConnectionId && (instances?.length ?? 0) === 0 ? (
                <p className="mt-1 text-xs text-indigo-900">
                  No running instances with a public address were found. Create one in {providerLabel(pickerProvider)}{' '}
                  first, or enter the host manually below.
                </p>
              ) : null}
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  )
}
