import { cn } from '@/lib/utils'

/** Thin indeterminate bar shown while stack data is refreshing in the background. */
export default function StackRefreshBar({
  active,
  className,
  label,
}: {
  active: boolean
  className?: string
  label?: string
}) {
  if (!active) {
    return null
  }

  return (
    <div className={cn('space-y-1', className)}>
      {label && <p className="text-xs text-gray-500">{label}</p>}
      <div className="h-1 w-full overflow-hidden rounded-full bg-gray-200">
        <div className="h-full w-2/5 animate-pulse rounded-full bg-blue-500" />
      </div>
    </div>
  )
}
