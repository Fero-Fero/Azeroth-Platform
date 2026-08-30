import { useState } from 'react'
import { Loader2, RefreshCw, CheckCircle2 } from 'lucide-react'
import { useRebuildStackClientManifest } from '@/hooks/useLauncher'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import type { ClientManifestRebuildResultDto } from '@/types/client.types'

function formatBytes(bytes: number): string {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(2)} ${units[unit]}`
}

/**
 * Rebuilds the stack's distributable client manifest from disk (full re-hash), applies corrected
 * base/managed file groups, and bumps the verify token so every launcher full-syncs on its next check.
 */
export default function RebuildClientManifestButton({ stackId }: { stackId: string }) {
  const rebuild = useRebuildStackClientManifest(stackId)
  const [result, setResult] = useState<ClientManifestRebuildResultDto | null>(null)
  const [err, setErr] = useState<string | null>(null)

  const onClick = async () => {
    setErr(null)
    setResult(null)
    try {
      const data = await rebuild.mutateAsync()
      setResult(data)
    } catch (e) {
      setErr(errorMessage(e))
    }
  }

  return (
    <div className="mb-4 rounded-md border border-blue-100 bg-blue-50/40 p-4">
      <div className="flex flex-wrap items-center gap-2">
        <button
          onClick={onClick}
          disabled={rebuild.isPending}
          className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm text-white hover:bg-blue-700 disabled:opacity-50"
          title="Re-hash every file, rebuild the launcher manifest, and queue a full client sync"
        >
          {rebuild.isPending ? (
            <Loader2 className="h-4 w-4 animate-spin" />
          ) : (
            <RefreshCw className="h-4 w-4" />
          )}
          Rebuild launcher manifest
        </button>
        {result && (
          <span className="inline-flex items-center gap-1 text-sm text-green-700">
            <CheckCircle2 className="h-4 w-4" />
            Manifest rebuilt - clients will full-sync on next check
          </span>
        )}
      </div>
      <p className="mt-2 text-xs text-gray-600">
        Re-hashes every distributable file, rebuilds the signed manifest (including standard MPQs like{' '}
        <span className="font-mono">patch-2.mpq</span> / <span className="font-mono">patch-3.mpq</span> as
        base content), and queues a full download on every launcher. Run this after uploading or editing
        client files, then have players click <strong>Update</strong>.
      </p>
      {result && (
        <div className="mt-2 text-xs text-gray-700 space-y-1">
          <div>
            <strong>{result.fileCount.toLocaleString()}</strong> distributable files ·{' '}
            {formatBytes(result.totalSize)} total
          </div>
          <div>
            Base: {result.baseFileCount.toLocaleString()} files · {formatBytes(result.baseTotalSize)}
            {result.managedFileCount > 0 && (
              <>
                {' '}
                · Managed: {result.managedFileCount.toLocaleString()} files ·{' '}
                {formatBytes(result.managedTotalSize)}
              </>
            )}
          </div>
        </div>
      )}
      {err && <div className="mt-2 rounded-md bg-red-50 px-3 py-2 text-xs text-red-700">{err}</div>}
    </div>
  )
}
