import { useEffect, useState } from 'react'
import { Loader2, Save, CheckCircle2, LocateFixed, AlertCircle } from 'lucide-react'
import { useLauncherProfile } from '@/hooks/useLauncher'
import { useSetRealmAddress } from '@/hooks/useRealms'
import { systemApi } from '@/services/api'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import { browserLanHost, detectBrowserLanIp, detectManagerLanHost, isPrivateIPv4 } from '@/lib/network'

/**
 * Shared realmlist host editor used by both the stack Overview and the Client → Realms tab so the two
 * always stay in lockstep. Saving performs the full stack public-host update in one step:
 *   1. persists the stack's realmlist host override (survives restarts),
 *   2. applies it to the live realmlist DB and regenerated runtime env/config,
 *   3. refreshes launcher/armory/client/server processes that consume that host.
 */
export default function RealmlistAddressField({ stackId }: { stackId: string }) {
  const { data, isLoading } = useLauncherProfile(stackId)
  const setAddress = useSetRealmAddress(stackId)

  const [value, setValue] = useState('')
  const [initialized, setInitialized] = useState(false)
  const [saved, setSaved] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const [detecting, setDetecting] = useState(false)

  useEffect(() => {
    if (data && !initialized) {
      setValue(data.realmlistHostOverride ?? '')
      setInitialized(true)
    }
  }, [data, initialized])

  if (isLoading || !data) {
    return (
      <div className="inline-flex items-center gap-2 text-sm text-gray-400">
        <Loader2 className="h-4 w-4 animate-spin" /> Loading…
      </div>
    )
  }

  const busy = setAddress.isPending
  const effectiveHost = value.trim() || data.effectiveRealmlistHost

  const onDetectIp = async () => {
    setErr(null)
    setDetecting(true)
    try {
      // Prefer the manager API first: in Docker it reads HOST_LAN_IP; on bare metal it scans NICs.
      const res = await systemApi.network()
      const apiHost = res.data.suggestedRealmlistHost?.trim()
      if (apiHost) {
        setValue(apiHost)
        return
      }

      const browserHost = browserLanHost() || await detectBrowserLanIp() || await detectManagerLanHost()
      if (browserHost) {
        setValue(browserHost)
        return
      }

      const effective = data.effectiveRealmlistHost?.trim()
      if (effective && isPrivateIPv4(effective)) {
        setValue(effective)
        return
      }

      setErr('Could not detect a usable LAN IP. Enter it manually.')
    } catch {
      setErr('Failed to detect this computer\u2019s IP address.')
    } finally {
      setDetecting(false)
    }
  }

  const onSave = async () => {
    setErr(null)
    const host = value.trim()
    if (host.length === 0) {
      setErr('Realm address is required.')
      return
    }
    try {
      // Backend persists the override, applies the live DB realmlist, and rescans the launcher client.
      await setAddress.mutateAsync(host)
      setSaved(true)
      setTimeout(() => setSaved(false), 2500)
    } catch (e) {
      setErr(errorMessage(e))
    }
  }

  return (
    <div>
      <div className="flex flex-wrap items-start gap-2">
        <input
          className="w-56 rounded-md border border-gray-300 px-3 py-2 text-sm font-mono focus:outline-none focus:ring-2 focus:ring-blue-500"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          placeholder={data.effectiveRealmlistHost || 'e.g. 192.168.1.50'}
          disabled={busy || detecting}
        />
        <button
          type="button"
          onClick={onDetectIp}
          disabled={busy || detecting}
          title="Detect this computer's LAN IP address and fill it in"
          className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
        >
          {detecting ? <Loader2 className="h-4 w-4 animate-spin" /> : <LocateFixed className="h-4 w-4" />}
          Use this computer&rsquo;s IP
        </button>
        <button
          type="button"
          onClick={onSave}
          disabled={busy || detecting || value.trim().length === 0}
          className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          title="Save and force-apply this host across the stack"
        >
          {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
          Save
        </button>
        {saved && (
          <span className="inline-flex items-center gap-1 py-2 text-sm text-green-600">
            <CheckCircle2 className="h-4 w-4" /> Saved
          </span>
        )}
      </div>
      <p className="mt-2 text-xs text-gray-500">
        Effective: <span className="font-mono">{effectiveHost}:{data.realmlistPort}</span>. Saving force-applies
        this host across the stack, including the live realmlist, regenerated runtime config, launcher client,
        and player-facing web services.
      </p>
      {err && (
        <div className="mt-2 flex items-center gap-2 rounded-md bg-red-50 px-3 py-2 text-xs text-red-700">
          <AlertCircle className="h-4 w-4" /> {err}
        </div>
      )}
    </div>
  )
}
