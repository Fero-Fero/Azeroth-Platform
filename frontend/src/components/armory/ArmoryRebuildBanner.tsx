import { Loader2, Wand2 } from 'lucide-react'
import { useArmoryJobContext } from '@/contexts/ArmoryJobContext'

export default function ArmoryRebuildBanner({
  rebuildPending,
  onRebuildError,
  compact = false,
}: {
  rebuildPending?: boolean
  onRebuildError?: (message: string) => void
  compact?: boolean
}) {
  const { job, isRebuildRunning, enqueueRebuild } = useArmoryJobContext()

  if (!rebuildPending && !isRebuildRunning) {
    return null
  }

  const onRebuild = async () => {
    try {
      await enqueueRebuild()
    } catch (err) {
      onRebuildError?.(err instanceof Error ? err.message : 'Failed to start armory rebuild.')
    }
  }

  if (isRebuildRunning) {
    return (
      <div
        className={`flex flex-wrap items-center justify-between gap-3 rounded-md border border-teal-200 bg-teal-50 text-sm text-teal-900 ${
          compact ? 'px-4 py-2.5' : 'px-4 py-3'
        }`}
      >
        <span className="inline-flex items-center gap-2">
          <Loader2 className="h-4 w-4 shrink-0 animate-spin" />
          <span>
            <strong>Rebuilding armory image.</strong> This runs in the background - you can refresh or
            navigate away; progress continues on the server.
          </span>
        </span>
        <span className="text-xs text-teal-800">{job?.message ?? 'Working…'}</span>
      </div>
    )
  }

  return (
    <div
      className={`flex flex-wrap items-center justify-between gap-3 rounded-md border border-amber-200 bg-amber-50 text-sm text-amber-900 ${
        compact ? 'px-4 py-2.5' : 'px-4 py-3'
      }`}
    >
      <span>
        <strong>Rebuild required.</strong> Saved changes are not live until you rebuild the armory image.
      </span>
      <button
        type="button"
        onClick={onRebuild}
        className="inline-flex shrink-0 items-center gap-1.5 rounded-md bg-amber-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-amber-700"
      >
        <Wand2 className="h-4 w-4" />
        Rebuild in background
      </button>
    </div>
  )
}
