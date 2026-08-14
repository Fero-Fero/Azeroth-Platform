import { useQuery } from '@tanstack/react-query'
import { Loader2, RefreshCw, ScrollText } from 'lucide-react'
import { stackApi } from '@/services/api'
import { apiErrorMessage } from '@/lib/utils'
import type { StackDetailsDto, VpcSshLogEntryDto } from '@/types/stack.types'
import { DeploymentTarget } from '@/types/stack.types'

const EVENT_LABELS: Record<string, string> = {
  accepted: 'Accepted login',
  failed: 'Failed password',
  'invalid-user': 'Invalid user',
  closed: 'Closed (pre-auth)',
}

function EventBadge({ eventType }: { eventType: string }) {
  const label = EVENT_LABELS[eventType] ?? eventType
  const className =
    eventType === 'accepted'
      ? 'bg-green-100 text-green-800'
      : eventType === 'failed' || eventType === 'invalid-user'
        ? 'bg-red-100 text-red-800'
        : 'bg-gray-100 text-gray-700'

  return (
    <span className={`inline-flex rounded-full px-2 py-0.5 text-[11px] font-medium ${className}`}>
      {label}
    </span>
  )
}

function formatLogTime(entry: VpcSshLogEntryDto) {
  if (!entry.timestamp) {
    return '—'
  }

  const date = new Date(entry.timestamp)
  return Number.isNaN(date.getTime()) ? entry.timestamp : date.toLocaleString()
}

export default function ExternalVpcLogsPanel({ stack }: { stack: StackDetailsDto }) {
  const isExternal = stack.configuration?.deployment?.target === DeploymentTarget.External

  const {
    data: logs,
    isLoading,
    isFetching,
    error,
    refetch,
  } = useQuery({
    queryKey: ['vpc-ssh-logs', stack.stackId],
    queryFn: async () => (await stackApi.vpcSshLogs(stack.stackId, 150)).data,
    enabled: isExternal,
    refetchInterval: 60_000,
  })

  if (!isExternal) {
    return null
  }

  return (
    <section className="rounded-lg border border-gray-200 bg-white p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex items-start gap-3">
          <ScrollText className="mt-0.5 h-5 w-5 shrink-0 text-gray-500" aria-hidden="true" />
          <div>
            <h3 className="font-medium text-gray-900">SSH access log</h3>
            <p className="mt-1 text-sm text-gray-600">
              Recent successful logins and failed SSH attempts on the remote VPC host. Data is read from{' '}
              <code className="text-xs">auth.log</code>, <code className="text-xs">secure</code>, or{' '}
              <code className="text-xs">journalctl</code> over SSH.
            </p>
            {logs?.logSource && (
              <p className="mt-1 text-xs text-gray-500">
                Source: <span className="font-mono">{logs.logSource}</span>
              </p>
            )}
          </div>
        </div>
        <button
          type="button"
          onClick={() => void refetch()}
          disabled={isFetching}
          className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-60"
        >
          {isFetching ? (
            <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
          ) : (
            <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />
          )}
          Refresh
        </button>
      </div>

      <div className="mt-4">
        {isLoading && <p className="text-sm text-gray-500">Loading SSH events…</p>}
        {error && (
          <p className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
            {apiErrorMessage(error)}
          </p>
        )}
        {!isLoading && !error && logs && !logs.success && (
          <p className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-950">
            {logs.message}
          </p>
        )}
        {!isLoading && !error && logs?.success && logs.entries.length === 0 && (
          <p className="text-sm text-gray-600">{logs.message}</p>
        )}
        {!isLoading && !error && logs?.success && logs.entries.length > 0 && (
          <div className="overflow-x-auto rounded border border-gray-200">
            <table className="min-w-full divide-y divide-gray-200 text-xs">
              <thead className="bg-gray-50">
                <tr>
                  <th className="px-3 py-2 text-left font-medium text-gray-600">Time</th>
                  <th className="px-3 py-2 text-left font-medium text-gray-600">Event</th>
                  <th className="px-3 py-2 text-left font-medium text-gray-600">User</th>
                  <th className="px-3 py-2 text-left font-medium text-gray-600">Source IP</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100 bg-white">
                {[...logs.entries].reverse().map((entry, index) => (
                  <tr key={`${entry.rawLine}-${index}`} title={entry.rawLine}>
                    <td className="whitespace-nowrap px-3 py-2 text-gray-700">{formatLogTime(entry)}</td>
                    <td className="px-3 py-2">
                      <EventBadge eventType={entry.eventType} />
                    </td>
                    <td className="px-3 py-2 font-mono text-gray-800">{entry.username ?? '—'}</td>
                    <td className="px-3 py-2 font-mono text-gray-800">{entry.sourceIp ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  )
}
