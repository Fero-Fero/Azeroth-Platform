import type { StackJobStatus } from '@/types/stack.types'

/** Prefer phase over isRunning - the backend computes both, but phase is authoritative. */
export function isStackJobRunning(status: StackJobStatus | null | undefined): boolean {
  if (!status) return false
  if (status.phase === 'Completed' || status.phase === 'Failed') return false
  return true
}

export function normalizeStackJobStatus(
  status: StackJobStatus | null | undefined,
): StackJobStatus | null {
  if (!status) return null
  return {
    ...status,
    isRunning: isStackJobRunning(status),
  }
}

/** Apply poll results without clobbering a newer in-flight job the UI already knows about. */
export function mergeStackJobStatus(
  current: StackJobStatus | null | undefined,
  incoming: StackJobStatus | null | undefined,
): StackJobStatus | null {
  const next = normalizeStackJobStatus(incoming)
  if (!next) return current ?? null

  const prev = normalizeStackJobStatus(current)
  if (!prev) return next

  if (prev.jobId === next.jobId) return next

  // A different job id means a new operation was enqueued - always take the server snapshot.
  if (next.isRunning) return next

  // Ignore a stale completed job when we are already tracking a newer running job locally.
  if (prev.isRunning && prev.jobId !== next.jobId) return prev

  return next
}
