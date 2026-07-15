import { useState } from 'react'
import { Loader2, RefreshCw, CheckCircle2 } from 'lucide-react'
import { useForceVerifyStackClient } from '@/hooks/useLauncher'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

/**
 * Client tab action that forces every launcher pointed at this stack to full-verify (re-hash) all
 * client files on its next check, rather than the quick size-only check it normally
 * runs on base files. Use when a same-size edit (e.g. a hand-edited Config.wtf) wouldn't otherwise be
 * detected. It bumps the manifest's verify token server-side; each launcher notices the change once,
 * re-validates, then records the token so the forced verify runs exactly once per request.
 */
export default function ForceVerifyButton({ stackId }: { stackId: string }) {
  const forceVerify = useForceVerifyStackClient(stackId)
  const [done, setDone] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  const onClick = async () => {
    setErr(null)
    try {
      await forceVerify.mutateAsync()
      setDone(true)
      setTimeout(() => setDone(false), 3000)
    } catch (e) {
      setErr(errorMessage(e))
    }
  }

  return (
    <div>
      <h3 className="font-medium text-gray-900 mb-2">Client Files</h3>
      <div className="flex flex-wrap items-center gap-2">
        <button
          onClick={onClick}
          disabled={forceVerify.isPending}
          className="inline-flex items-center gap-1.5 rounded-md bg-amber-600 px-3 py-2 text-sm text-white hover:bg-amber-700 disabled:opacity-50"
          title="Force every launcher to re-hash and re-download all changed client files on its next check"
        >
          {forceVerify.isPending ? (
            <Loader2 className="h-4 w-4 animate-spin" />
          ) : (
            <RefreshCw className="h-4 w-4" />
          )}
          Force clients to re-validate all files
        </button>
        {done && (
          <span className="inline-flex items-center gap-1 text-sm text-green-600">
            <CheckCircle2 className="h-4 w-4" /> Queued — clients re-validate on next check
          </span>
        )}
      </div>
      <span className="mt-1 block text-xs text-gray-500">
        Forces a full hash verification of every client file the next time each player opens the
        launcher (their normal check only size-compares base files). Use after editing files directly
        on the server. Note: the launcher merges server realmlist/Config.wtf values on each launch, so
        the client's <span className="font-mono">WTF/Config.wtf</span> is not distributed as a file.
      </span>
      {err && <div className="mt-2 rounded-md bg-red-50 px-3 py-2 text-xs text-red-700">{err}</div>}
    </div>
  )
}
