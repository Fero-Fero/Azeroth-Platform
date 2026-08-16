import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Cloud, ClipboardList, KeyRound, Loader2, ShieldCheck, Trash2 } from 'lucide-react'
import { cloudApi } from '@/services/api'
import { CloudAuthMethod, CloudProvider, type CloudConnectionVerifyResultDto, type CloudProviderConnectionDto } from '@/types/stack.types'
import { cn } from '@/lib/utils'
import { CloudConnectionLinkForm } from '@/components/wizard/common/CloudConnectionLinkForm'
import { CloudProviderLoginButton } from '@/components/wizard/common/CloudProviderLoginButton'
import { SshKeyDownloadButton } from '@/components/wizard/common/SshKeyDownloadButton'
import { connectionStatusLine, providerDisplayName } from '@/lib/cloud-auth'

function eventLabel(eventType: string): string {
  switch (eventType) {
    case 'ssh_key.created':
      return 'SSH key saved'
    case 'ssh_key.deleted':
      return 'SSH key deleted'
    case 'ssh_key.used':
      return 'SSH key used'
    case 'ssh_key.downloaded':
      return 'SSH key downloaded'
    case 'connection.created':
      return 'Account linked'
    case 'connection.deleted':
      return 'Account unlinked'
    case 'terminal.started':
      return 'Terminal started'
    case 'terminal.ended':
      return 'Terminal ended'
    case 'launch.completed':
      return 'Launch completed'
    case 'connection.oauth.linked':
      return 'OAuth linked'
    case 'connection.oauth.refreshed':
      return 'OAuth refreshed'
    case 'connection.oauth.revoked':
      return 'OAuth revoked'
    case 'connection.assumed_role.linked':
      return 'AWS IAM role connected'
    case 'connection.verified':
      return 'Account verified'
    default:
      return eventType
  }
}

export default function CloudSettingsPage() {
  const queryClient = useQueryClient()
  const [keyLabel, setKeyLabel] = useState('')
  const [keyUser, setKeyUser] = useState('azp-admin')
  const [keyPem, setKeyPem] = useState('')
  const [keyError, setKeyError] = useState<string | null>(null)
  const [verifyMessageById, setVerifyMessageById] = useState<Record<string, { ok: boolean; message: string }>>({})
  const [verifyingId, setVerifyingId] = useState<string | null>(null)
  const [verifyingAll, setVerifyingAll] = useState(false)

  const { data: sshKeys, isLoading: loadingKeys } = useQuery({
    queryKey: ['cloud-ssh-keys'],
    queryFn: async () => (await cloudApi.listSshKeys()).data,
  })

  const { data: connections, isLoading: loadingConnections } = useQuery({
    queryKey: ['cloud-connections'],
    queryFn: async () => (await cloudApi.listConnections()).data,
  })

  const { data: auditLogs, isLoading: loadingAuditLogs } = useQuery({
    queryKey: ['cloud-audit-logs'],
    queryFn: async () => (await cloudApi.listAuditLogs(100)).data,
  })

  const { data: authProviders } = useQuery({
    queryKey: ['cloud-auth-providers'],
    queryFn: async () => (await cloudApi.listAuthProviders()).data,
  })

  const refreshAuditLogs = async () => {
    await queryClient.invalidateQueries({ queryKey: ['cloud-audit-logs'] })
  }

  const createKeyMutation = useMutation({
    mutationFn: async () =>
      (
        await cloudApi.createSshKey({
          label: keyLabel.trim() || 'SSH key',
          privateKey: keyPem.trim(),
          defaultSshUser: keyUser.trim() || 'azp-admin',
        })
      ).data,
    onSuccess: async () => {
      setKeyLabel('')
      setKeyPem('')
      setKeyError(null)
      await queryClient.invalidateQueries({ queryKey: ['cloud-ssh-keys'] })
      await refreshAuditLogs()
    },
    onError: () => setKeyError('Failed to save SSH key.'),
  })

  const handleDeleteKey = async (id: string) => {
    await cloudApi.deleteSshKey(id)
    await queryClient.invalidateQueries({ queryKey: ['cloud-ssh-keys'] })
    await refreshAuditLogs()
  }

  const handleDeleteConnection = async (id: string) => {
    await cloudApi.revokeCloudAuth(id)
    await queryClient.invalidateQueries({ queryKey: ['cloud-connections'] })
    await refreshAuditLogs()
  }

  const applyVerifyResult = (result: CloudConnectionVerifyResultDto) => {
    setVerifyMessageById((current) => ({
      ...current,
      [result.connection.id]: { ok: result.ok, message: result.message },
    }))
    queryClient.setQueryData(
      ['cloud-connections'],
      (current: CloudProviderConnectionDto[] | undefined) =>
        current?.map((item) => (item.id === result.connection.id ? result.connection : item)),
    )
  }

  const handleVerifyConnection = async (id: string) => {
    setVerifyingId(id)
    try {
      applyVerifyResult((await cloudApi.verifyConnection(id)).data)
      await refreshAuditLogs()
    } catch {
      setVerifyMessageById((current) => ({
        ...current,
        [id]: { ok: false, message: 'Could not reach the platform API to verify this account.' },
      }))
    } finally {
      setVerifyingId(null)
    }
  }

  const handleVerifyAll = async () => {
    if (!connections?.length) {
      return
    }

    setVerifyingAll(true)
    try {
      for (const connection of connections) {
        setVerifyingId(connection.id)
        try {
          applyVerifyResult((await cloudApi.verifyConnection(connection.id)).data)
        } catch {
          setVerifyMessageById((current) => ({
            ...current,
            [connection.id]: { ok: false, message: 'Could not reach the platform API to verify this account.' },
          }))
        }
      }
      await refreshAuditLogs()
    } finally {
      setVerifyingId(null)
      setVerifyingAll(false)
    }
  }

  return (
    <div className="space-y-8">
      <div>
        <h1 className="text-2xl font-semibold text-gray-900">Cloud</h1>
        <p className="mt-1 text-sm text-gray-600">
          Manage saved SSH keys and linked cloud accounts used by the Create Stack wizard. Secrets are
          encrypted at rest. You can download a .pem copy of a saved key when you need to SSH from your
          own machine.
        </p>
      </div>

      <section className="rounded-lg border border-gray-200 bg-white p-5">
        <div className="flex items-center gap-2">
          <KeyRound className="h-5 w-5 text-gray-600" aria-hidden="true" />
          <h2 className="text-lg font-semibold text-gray-900">Saved SSH keys</h2>
        </div>
        <p className="mt-1 text-xs text-gray-500">
          Reuse keys when creating external VPC stacks. You can also add keys in the Create Stack wizard.
        </p>

        {loadingKeys ? (
          <div className="mt-4 flex items-center gap-2 text-sm text-gray-500">
            <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
            Loading keys…
          </div>
        ) : (sshKeys?.length ?? 0) === 0 ? (
          <p className="mt-4 text-sm text-gray-600">No saved keys yet.</p>
        ) : (
          <ul className="mt-4 divide-y divide-gray-100 rounded-md border border-gray-200">
            {sshKeys?.map((key) => (
              <li key={key.id} className="flex flex-wrap items-center justify-between gap-2 px-3 py-2.5">
                <div>
                  <p className="text-sm font-medium text-gray-900">{key.label}</p>
                  <p className="text-xs text-gray-500">
                    User: <span className="font-mono">{key.defaultSshUser}</span> · Fingerprint:{' '}
                    <span className="font-mono">{key.fingerprint}</span>
                  </p>
                </div>
                <div className="flex flex-wrap items-center gap-2">
                  <SshKeyDownloadButton
                    label={key.label}
                    keyId={key.id}
                    className="border-gray-300 text-gray-800 hover:bg-gray-50"
                  />
                  <button
                    type="button"
                    onClick={() => void handleDeleteKey(key.id)}
                    className="inline-flex items-center gap-1 rounded-md border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
                  >
                    <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
                    Delete
                  </button>
                </div>
              </li>
            ))}
          </ul>
        )}

        <div className="mt-4 space-y-2 rounded-md border border-gray-200 bg-gray-50 p-3">
          <p className="text-xs font-medium text-gray-800">Add SSH key</p>
          <div className="grid gap-2 sm:grid-cols-2">
            <input
              type="text"
              placeholder="Label"
              value={keyLabel}
              onChange={(event) => setKeyLabel(event.target.value)}
              className="rounded-md border border-gray-300 px-3 py-2 text-sm"
            />
            <input
              type="text"
              placeholder="Default SSH user (e.g. azp-admin)"
              value={keyUser}
              onChange={(event) => setKeyUser(event.target.value)}
              className="rounded-md border border-gray-300 px-3 py-2 text-sm"
            />
          </div>
          <textarea
            rows={4}
            placeholder="PEM private key"
            value={keyPem}
            onChange={(event) => setKeyPem(event.target.value)}
            className="block w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-xs"
          />
          {keyError ? <p className="text-xs text-red-700">{keyError}</p> : null}
          <button
            type="button"
            disabled={createKeyMutation.isPending || keyPem.trim().length === 0}
            onClick={() => void createKeyMutation.mutate()}
            className={cn(
              'inline-flex items-center gap-2 rounded-md bg-blue-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-blue-700 disabled:opacity-60'
            )}
          >
            {createKeyMutation.isPending ? (
              <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
            ) : null}
            Save key
          </button>
        </div>
      </section>

      <section className="rounded-lg border border-gray-200 bg-white p-5">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div>
            <div className="flex items-center gap-2">
              <Cloud className="h-5 w-5 text-gray-600" aria-hidden="true" />
              <h2 className="text-lg font-semibold text-gray-900">Linked cloud accounts</h2>
            </div>
            <p className="mt-1 text-xs text-gray-500">
              DigitalOcean, AWS, and GCP accounts linked for instance pickers and launch-via-platform in the
              Create Stack wizard. Verify checks that stored credentials still work against the provider API.
            </p>
          </div>
          {(connections?.length ?? 0) > 0 ? (
            <button
              type="button"
              disabled={verifyingAll || verifyingId !== null}
              onClick={() => void handleVerifyAll()}
              className="inline-flex items-center gap-1 rounded-md border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50 disabled:opacity-60"
            >
              {verifyingAll ? (
                <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
              ) : (
                <ShieldCheck className="h-3.5 w-3.5" aria-hidden="true" />
              )}
              Verify all
            </button>
          ) : null}
        </div>

        {loadingConnections ? (
          <div className="mt-4 flex items-center gap-2 text-sm text-gray-500">
            <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
            Loading accounts…
          </div>
        ) : (connections?.length ?? 0) === 0 ? (
          <p className="mt-4 text-sm text-gray-600">No linked accounts yet.</p>
        ) : (
          <ul className="mt-4 divide-y divide-gray-100 rounded-md border border-gray-200">
            {connections?.map((connection) => (
              <li key={connection.id} className="flex flex-wrap items-center justify-between gap-2 px-3 py-2.5">
                <div>
                  <p className="text-sm font-medium text-gray-900">{connection.label}</p>
                  <p className="text-xs text-gray-500">
                    {providerDisplayName(connection.provider)}
                    {connection.defaultRegion ? (
                      <>
                        {' '}
                        · Default: <span className="font-mono">{connection.defaultRegion}</span>
                      </>
                    ) : null}
                    {connection.defaultProjectId ? (
                      <>
                        {' '}
                        · {connection.provider === CloudProvider.Azure ? 'Subscription' : 'Project'}:{' '}
                        <span className="font-mono">{connection.defaultProjectId}</span>
                      </>
                    ) : null}
                    {' '}
                    · {connectionStatusLine(connection)}
                    {connection.needsReauth ? (
                      <span className="ml-1 font-medium text-amber-700">Needs reconnect</span>
                    ) : null}
                  </p>
                  {verifyMessageById[connection.id] ? (
                    <p
                      className={`mt-1 text-xs ${
                        verifyMessageById[connection.id].ok ? 'text-green-700' : 'text-red-700'
                      }`}
                    >
                      {verifyMessageById[connection.id].message}
                    </p>
                  ) : null}
                </div>
                <div className="flex flex-wrap items-center gap-2">
                  <button
                    type="button"
                    disabled={verifyingAll || verifyingId !== null}
                    onClick={() => void handleVerifyConnection(connection.id)}
                    className="inline-flex items-center gap-1 rounded-md border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50 disabled:opacity-60"
                  >
                    {verifyingId === connection.id ? (
                      <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
                    ) : (
                      <ShieldCheck className="h-3.5 w-3.5" aria-hidden="true" />
                    )}
                    Verify
                  </button>
                  {connection.authMethod === CloudAuthMethod.OAuth
                  || connection.authMethod === CloudAuthMethod.AssumedRole ? (
                    <CloudProviderLoginButton
                      provider={connection.provider}
                      status={authProviders?.find((item) => item.provider === connection.provider)}
                      reconnectConnectionId={connection.id}
                      label={connection.label}
                      onLinked={() => void refreshAuditLogs()}
                    />
                  ) : null}
                  <button
                    type="button"
                    onClick={() => void handleDeleteConnection(connection.id)}
                    className="inline-flex items-center gap-1 rounded-md border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
                  >
                    <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
                    Unlink
                  </button>
                </div>
              </li>
            ))}
          </ul>
        )}

        <div className="mt-4 space-y-2 rounded-md border border-gray-200 bg-gray-50 p-3">
          <p className="text-xs font-medium text-gray-800">Link cloud account</p>
          <CloudConnectionLinkForm idPrefix="cloud-settings-link" onLinked={() => void refreshAuditLogs()} />
        </div>
      </section>

      <section className="rounded-lg border border-gray-200 bg-white p-5">
        <div className="flex items-center gap-2">
          <ClipboardList className="h-5 w-5 text-gray-600" aria-hidden="true" />
          <h2 className="text-lg font-semibold text-gray-900">Audit log</h2>
        </div>
        <p className="mt-1 text-xs text-gray-500">
          Recent cloud integration events. Private keys and tokens are never logged.
        </p>

        {loadingAuditLogs ? (
          <div className="mt-4 flex items-center gap-2 text-sm text-gray-500">
            <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
            Loading audit log…
          </div>
        ) : (auditLogs?.length ?? 0) === 0 ? (
          <p className="mt-4 text-sm text-gray-600">No cloud events recorded yet.</p>
        ) : (
          <ul className="mt-4 max-h-96 divide-y divide-gray-100 overflow-y-auto rounded-md border border-gray-200">
            {auditLogs?.map((entry) => (
              <li key={entry.id} className="px-3 py-2.5">
                <div className="flex flex-wrap items-baseline justify-between gap-2">
                  <p className="text-sm font-medium text-gray-900">{eventLabel(entry.eventType)}</p>
                  <p className="text-[11px] text-gray-500">
                    {new Date(entry.occurredAtUtc).toLocaleString()} · {entry.actor}
                  </p>
                </div>
                <p className="mt-0.5 text-xs text-gray-600">{entry.summary}</p>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  )
}
