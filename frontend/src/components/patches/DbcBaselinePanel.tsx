import { useDbcStoreStatus } from '@/hooks/useModuleExtraData'

export default function DbcBaselinePanel() {
  const status = useDbcStoreStatus(false)
  const data = status.data

  return (
    <section className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">DBC baselines</h3>
        <p className="mt-1 max-w-2xl text-sm text-gray-500">
          Tables are converted from this stack&apos;s data directory only when a patch or module
          needs that DBC for a diff. The manager does not export the full client-data set up front.
        </p>
      </div>
      <dl className="mt-3 grid grid-cols-2 gap-x-4 gap-y-1 text-sm sm:grid-cols-3">
        <div>
          <dt className="text-gray-500">Mode</dt>
          <dd className="font-medium text-gray-900">On demand</dd>
        </div>
        <div>
          <dt className="text-gray-500">Cached tables</dt>
          <dd className="font-medium text-gray-900">{data?.tableCount ?? 0}</dd>
        </div>
        <div>
          <dt className="text-gray-500">Last converted</dt>
          <dd className="font-medium text-gray-900">
            {data?.syncedAt ? new Date(data.syncedAt).toLocaleString() : '—'}
          </dd>
        </div>
      </dl>
      {data?.message && <p className="mt-2 text-sm text-gray-600">{data.message}</p>}
    </section>
  )
}
