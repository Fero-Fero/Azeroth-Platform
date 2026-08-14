import { useState } from 'react'
import { AlertCircle, Loader2, Plug } from 'lucide-react'
import { SshPrivateKeyField } from '@/components/wizard/common/SshPrivateKeyField'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { stackApi, systemApi } from '@/services/api'
import { stackKeys } from '@/hooks/useStacks'
import { apiErrorMessage } from '@/lib/utils'
import type { DeploymentConfigDto, StackDetailsDto } from '@/types/stack.types'
import { DeploymentTarget } from '@/types/stack.types'

interface ExternalReconnectPanelProps {
  stack: StackDetailsDto
}

export default function ExternalReconnectPanel({ stack }: ExternalReconnectPanelProps) {
  const queryClient = useQueryClient()
  const deployment = stack.configuration.deployment
  const isExternal = deployment?.target === DeploymentTarget.External

  const [host, setHost] = useState(deployment?.externalHost ?? '')
  const [sshPort, setSshPort] = useState(deployment?.externalSshPort ?? 22)
  const [sshUser, setSshUser] = useState(deployment?.externalSshUser ?? '')
  const [privateKey, setPrivateKey] = useState('')
  const [showForm, setShowForm] = useState(false)
  const [testMessage, setTestMessage] = useState<string | null>(null)
  const [testing, setTesting] = useState(false)

  const reconnect = useMutation({
    mutationFn: (body: DeploymentConfigDto) => stackApi.reconnectExternal(stack.stackId, body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stack.stackId) })
      setPrivateKey('')
    },
  })

  if (!isExternal) {
    return null
  }

  const handleTest = async () => {
    setTestMessage(null)
    setTesting(true)
    try {
      const res = await systemApi.testRemoteConnection({
        target: DeploymentTarget.External,
        externalHost: host,
        externalSshPort: sshPort,
        externalSshUser: sshUser,
        externalSshPrivateKey: privateKey,
      })
      setTestMessage(res.data.success ? `Connected (${res.data.serverVersion ?? 'ok'})` : res.data.message)
    } catch (err) {
      setTestMessage(apiErrorMessage(err))
    } finally {
      setTesting(false)
    }
  }

  const handleReconnect = () => {
    reconnect.mutate({
      target: DeploymentTarget.External,
      externalHost: host,
      externalSshPort: sshPort,
      externalSshUser: sshUser,
      externalSshPrivateKey: privateKey,
    })
  }

  const showFields = stack.needsExternalReconnect || showForm

  return (
    <section
      className={`rounded-lg border p-4 ${stack.needsExternalReconnect ? 'border-amber-300 bg-amber-50' : 'border-gray-200 bg-white'}`}
    >
      <div className="flex items-start gap-3">
        <Plug className={`mt-0.5 h-5 w-5 shrink-0 ${stack.needsExternalReconnect ? 'text-amber-700' : 'text-gray-500'}`} />
        <div className="flex-1 space-y-3">
          <div>
            <h3 className="font-medium text-gray-900">Remote Docker engine</h3>
            {stack.needsExternalReconnect ? (
              <p className="mt-1 text-sm text-amber-900">
                {stack.externalReconnectReason ??
                  'This external stack needs to be reconnected. Re-enter the SSH private key after restoring platform keys or pruning the manager data volume.'}
              </p>
            ) : (
              <p className="mt-1 text-sm text-gray-600">
                Re-enter SSH credentials if the manager lost <code className="text-xs">secret-protection.key</code> or
                connection details changed.
              </p>
            )}
          </div>

          {showFields && (
            <div className="grid gap-3 sm:grid-cols-2">
              <label className="block text-sm">
                <span className="text-gray-700">Remote host</span>
                <input
                  type="text"
                  value={host}
                  onChange={(e) => setHost(e.target.value)}
                  className="mt-1 w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
                />
              </label>
              <label className="block text-sm">
                <span className="text-gray-700">SSH port</span>
                <input
                  type="number"
                  value={sshPort}
                  onChange={(e) => setSshPort(Number(e.target.value) || 22)}
                  className="mt-1 w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
                />
              </label>
              <label className="block text-sm">
                <span className="text-gray-700">SSH user</span>
                <input
                  type="text"
                  value={sshUser}
                  onChange={(e) => setSshUser(e.target.value)}
                  className="mt-1 w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
                />
              </label>
              <div className="sm:col-span-2">
                <SshPrivateKeyField
                  id={`reconnect-ssh-key-${stack.stackId}`}
                  value={privateKey}
                  onChange={setPrivateKey}
                  hint="Required to reconnect. Paste or select a key file from this machine."
                />
              </div>
            </div>
          )}

          {!stack.needsExternalReconnect && !showForm && (
            <button
              type="button"
              onClick={() => setShowForm(true)}
              className="text-sm font-medium text-blue-700 hover:underline"
            >
              Reconnect remote engine…
            </button>
          )}

          {showFields && (
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={() => void handleTest()}
                disabled={testing || !privateKey.trim()}
                className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50"
              >
                {testing && <Loader2 className="h-4 w-4 animate-spin" />}
                Test connection
              </button>
              <button
                type="button"
                onClick={handleReconnect}
                disabled={reconnect.isPending || !privateKey.trim() || !host.trim() || !sshUser.trim()}
                className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
              >
                {reconnect.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
                Save &amp; reconnect
              </button>
            </div>
          )}

          {testMessage && <p className="text-sm text-gray-700">{testMessage}</p>}
          {reconnect.isError && (
            <p className="flex items-center gap-2 text-sm text-red-700">
              <AlertCircle className="h-4 w-4 shrink-0" />
              {apiErrorMessage(reconnect.error)}
            </p>
          )}
          {reconnect.isSuccess && (
            <p className="text-sm text-green-800">Remote engine reconnected successfully.</p>
          )}
        </div>
      </div>
    </section>
  )
}
