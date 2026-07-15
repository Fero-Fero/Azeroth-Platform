import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Loader2, Save, CheckCircle2, AlertCircle, Globe2 } from 'lucide-react'
import { stackApi } from '@/services/api'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import type { ArmoryNetworkConfig } from '@/types/stack.types'

type BindMode = 'inherit' | 'all' | 'local' | 'custom'

function modeFromBind(bind: string): BindMode {
  if (!bind) return 'inherit'
  if (bind === '0.0.0.0') return 'all'
  if (bind === '127.0.0.1') return 'local'
  return 'custom'
}

/**
 * Per-stack network settings for the armory website + client file server: which host ports they publish
 * on and which interface those ports bind to. Lets an operator expose the armory beyond localhost (LAN /
 * VPC / all interfaces) from the UI — no editing the generated .env, which is unreachable on a remote host.
 */
export default function ArmoryNetworkField({ stackId }: { stackId: string }) {
  const qc = useQueryClient()
  const { data, isLoading } = useQuery({
    queryKey: ['armory-network', stackId],
    queryFn: async () => (await stackApi.armoryNetwork(stackId)).data,
  })

  const save = useMutation({
    mutationFn: async (config: ArmoryNetworkConfig) =>
      (await stackApi.updateArmoryNetwork(stackId, config)).data,
    onSuccess: (updated) => {
      qc.setQueryData(['armory-network', stackId], updated)
      // Refresh stack detail/list so the armory port + running badge reflect the change.
      qc.invalidateQueries({ queryKey: ['stacks'] })
    },
  })

  const [mode, setMode] = useState<BindMode>('inherit')
  const [customIp, setCustomIp] = useState('')
  const [armoryPort, setArmoryPort] = useState('')
  const [clientPort, setClientPort] = useState('')
  const [initialized, setInitialized] = useState(false)
  const [saved, setSaved] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  useEffect(() => {
    if (data && !initialized) {
      setMode(modeFromBind(data.bindAddress))
      setCustomIp(modeFromBind(data.bindAddress) === 'custom' ? data.bindAddress : '')
      setArmoryPort(String(data.armoryPort))
      setClientPort(String(data.clientPort))
      setInitialized(true)
    }
  }, [data, initialized])

  if (isLoading || !data) {
    return (
      <div>
        <h3 className="font-medium text-gray-900 mb-2">Armory &amp; client web access</h3>
        <div className="inline-flex items-center gap-2 text-sm text-gray-400">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading…
        </div>
      </div>
    )
  }

  const resolvedBind =
    mode === 'inherit' ? '' : mode === 'all' ? '0.0.0.0' : mode === 'local' ? '127.0.0.1' : customIp.trim()

  const dirty =
    resolvedBind !== data.bindAddress ||
    armoryPort.trim() !== String(data.armoryPort) ||
    clientPort.trim() !== String(data.clientPort)

  const onSave = async () => {
    setErr(null)
    const a = Number(armoryPort)
    const c = Number(clientPort)
    if (!Number.isInteger(a) || !Number.isInteger(c)) {
      setErr('Ports must be whole numbers.')
      return
    }
    try {
      await save.mutateAsync({
        ...data,
        armoryPort: a,
        clientPort: c,
        bindAddress: resolvedBind,
      })
      setSaved(true)
      setTimeout(() => setSaved(false), 2500)
    } catch (e) {
      setErr(errorMessage(e))
    }
  }

  const effectiveUrl = `http://${data.effectiveBindAddress === '0.0.0.0' ? '<this-host-ip>' : data.effectiveBindAddress}:${data.armoryPort}`

  return (
    <div>
      <div className="mb-2 flex items-center gap-2">
        <Globe2 className="h-4 w-4 text-gray-500" />
        <h3 className="font-medium text-gray-900">Armory &amp; client web access</h3>
      </div>
      <p className="mb-3 text-xs text-gray-500">
        Which host interface and ports the armory website and client file server are published on. Keep it
        on this machine for a private local setup, or open it up so other machines on your LAN / a VPC /
        the internet can reach the armory.
      </p>

      <div className="space-y-3">
        <div>
          <label className="mb-1 block text-xs font-medium text-gray-700">Reachable from</label>
          <select
            value={mode}
            onChange={(e) => setMode(e.target.value as BindMode)}
            disabled={save.isPending}
            className="w-full max-w-xs rounded-md border border-gray-300 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            <option value="all">Anywhere — all interfaces (0.0.0.0)</option>
            <option value="local">This machine only (127.0.0.1)</option>
            <option value="custom">A specific IP…</option>
            <option value="inherit">Inherit default ({data.isLocalDeployment ? 'this machine' : 'all interfaces'})</option>
          </select>
          {mode === 'custom' && (
            <input
              type="text"
              value={customIp}
              onChange={(e) => setCustomIp(e.target.value)}
              placeholder="e.g. 192.168.1.50 or a VPC private IP"
              disabled={save.isPending}
              className="mt-2 w-full max-w-xs rounded-md border border-gray-300 px-3 py-2 font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          )}
        </div>

        <div className="flex flex-wrap gap-3">
          <div>
            <label className="mb-1 block text-xs font-medium text-gray-700">Armory port</label>
            <input
              type="number"
              min={1024}
              max={65535}
              value={armoryPort}
              onChange={(e) => setArmoryPort(e.target.value)}
              disabled={save.isPending}
              className="w-32 rounded-md border border-gray-300 px-3 py-2 font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-gray-700">Client files port</label>
            <input
              type="number"
              min={1024}
              max={65535}
              value={clientPort}
              onChange={(e) => setClientPort(e.target.value)}
              disabled={save.isPending}
              className="w-32 rounded-md border border-gray-300 px-3 py-2 font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={onSave}
            disabled={save.isPending || !dirty}
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
            title="Save and apply — recreates the armory/client containers if the stack is running"
          >
            {save.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Save &amp; apply
          </button>
          {saved && (
            <span className="inline-flex items-center gap-1 text-sm text-green-600">
              <CheckCircle2 className="h-4 w-4" /> Applied
            </span>
          )}
        </div>
      </div>

      <p className="mt-2 text-xs text-gray-500">
        Current armory URL: <span className="font-mono">{effectiveUrl}</span>. Saving recreates the running
        armory/client containers so the change is live. When exposing beyond this machine, make sure the
        host firewall allows inbound TCP on these ports.
      </p>
      {err && (
        <div className="mt-2 flex items-center gap-2 rounded-md bg-red-50 px-3 py-2 text-xs text-red-700">
          <AlertCircle className="h-4 w-4" /> {err}
        </div>
      )}
    </div>
  )
}
