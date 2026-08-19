import { useState } from 'react'
import { Loader2, RefreshCw } from 'lucide-react'
import { useDbcStoreStatus, useSyncDbcStore } from '@/hooks/useModuleExtraData'

export default function DbcBaselinePanel() {
  const status = useDbcStoreStatus()
  const sync = useSyncDbcStore()
  const [confirmForce, setConfirmForce] = useState(false)
  const data = status.data

  const onSync = (force: boolean) => {
    if (force && !confirmForce) {
      setConfirmForce(true)
      return
    }
    setConfirmForce(false)
    sync.mutate(force)
  }

  return (
    <section className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h3 className="text-sm font-semibold text-gray-900">DBC baseline store</h3>
          <p className="mt-1 max-w-2xl text-sm text-gray-500">
            Vanilla WotLK CSVs from wowgaming/client-data. Module extra-data trims against this store, not the
            live stack.
          </p>
        </div>
        <button
          type="button"
          onClick={() => onSync(true)}
          disabled={data?.inProgress || sync.isPending}
          className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50"
        >
          {data?.inProgress ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
          {confirmForce ? 'Confirm wipe & re-sync' : 'Sync DBC baseline'}
        </button>
      </div>
      {confirmForce && (
        <p className="mt-2 text-sm text-amber-800">
          Existing module DBC deltas were trimmed against the previous baseline and must be re-applied after a
          forced sync. Click again to confirm.
        </p>
      )}
      <dl className="mt-3 grid grid-cols-2 gap-x-4 gap-y-1 text-sm sm:grid-cols-4">
        <div>
          <dt className="text-gray-500">Status</dt>
          <dd className="font-medium text-gray-900">
            {data?.inProgress ? 'Syncing…' : data?.ready ? 'Ready' : 'Empty'}
          </dd>
        </div>
        <div>
          <dt className="text-gray-500">Release</dt>
          <dd className="font-medium text-gray-900">{data?.tag ?? '—'}</dd>
        </div>
        <div>
          <dt className="text-gray-500">Tables</dt>
          <dd className="font-medium text-gray-900">{data?.tableCount ?? 0}</dd>
        </div>
        <div>
          <dt className="text-gray-500">Last synced</dt>
          <dd className="font-medium text-gray-900">
            {data?.syncedAt ? new Date(data.syncedAt).toLocaleString() : '—'}
          </dd>
        </div>
      </dl>
      {data?.error && <p className="mt-2 text-sm text-red-700">{data.error}</p>}
      {data?.message && !data.error && <p className="mt-2 text-sm text-gray-600">{data.message}</p>}
    </section>
  )
}
