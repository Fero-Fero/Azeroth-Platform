/** Determinate bar when byte totals are known; otherwise an indeterminate pulse. */
export default function ClientJobProgress({
  message,
  bytesCompleted,
  bytesTotal,
}: {
  message?: string | null
  bytesCompleted?: number | null
  bytesTotal?: number | null
}) {
  const hasTotal = (bytesTotal ?? 0) > 0
  const percent = hasTotal
    ? Math.min(100, Math.round(((bytesCompleted ?? 0) * 100) / (bytesTotal as number)))
    : null

  return (
    <div className="space-y-1">
      {message ? <p className="text-sm text-blue-700">{message}</p> : null}
      <div className="h-2 w-full overflow-hidden rounded bg-gray-200">
        {percent !== null ? (
          <div className="h-full bg-blue-600 transition-all" style={{ width: `${percent}%` }} />
        ) : (
          <div className="h-full w-2/5 animate-pulse rounded bg-blue-500" />
        )}
      </div>
      {percent !== null ? <p className="text-xs tabular-nums text-gray-500">{percent}%</p> : null}
    </div>
  )
}
