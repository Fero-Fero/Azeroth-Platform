import type { SetupProgressCounts } from '@/setup/resolveVisibleSteps'

interface SetupProgressBarProps {
  progress: SetupProgressCounts
}

export default function SetupProgressBar({ progress }: SetupProgressBarProps) {
  if (progress.total === 0) return null

  const percent = Math.round((progress.completed / progress.total) * 100)
  const remainingLabel =
    progress.remaining === 1 ? '1 step remaining' : `${progress.remaining} steps remaining`

  return (
    <div className="border-b border-gray-200 bg-white px-4 py-3">
      <div className="flex items-baseline justify-between gap-3">
        <p className="text-sm font-medium text-gray-900">Setup progress</p>
        <p className="text-xs text-gray-600">
          {progress.completed} of {progress.total} complete
          {progress.remaining > 0 ? ` · ${remainingLabel}` : ''}
        </p>
      </div>
      <div
        className="mt-2 h-2 overflow-hidden rounded-full bg-gray-200"
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={progress.total}
        aria-valuenow={progress.completed}
        aria-label={remainingLabel}
      >
        <div
          className={`h-full rounded-full transition-[width] duration-300 ${
            progress.remaining === 0 ? 'bg-green-600' : 'bg-blue-600'
          }`}
          style={{ width: `${percent}%` }}
        />
      </div>
    </div>
  )
}
