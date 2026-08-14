import { useCallback, useEffect, useRef, useState } from 'react'
import { isStackJobRunning, mergeStackJobStatus, normalizeStackJobStatus } from '@/lib/stackJob'
import { stackApi } from '@/services/api'
import type { StackJobStatus } from '@/types/stack.types'

const POLL_INTERVAL_MS = 2000

/**
 * Tracks detached lifecycle jobs for stacks on the list page (start/stop/restart). The list page does not
 * subscribe to SignalR per row, so this polls job status while any tracked stack has work in flight.
 */
export function useStackLifecycleJobs() {
  const [jobs, setJobs] = useState<Record<string, StackJobStatus>>({})
  const jobsRef = useRef<Record<string, StackJobStatus>>({})

  const syncJobs = useCallback((next: Record<string, StackJobStatus>) => {
    jobsRef.current = next
    setJobs(next)
  }, [])

  const trackJob = useCallback(
    (stackId: string, status: StackJobStatus | null | undefined) => {
      const normalized = normalizeStackJobStatus(status)
      if (!normalized) return
      syncJobs({
        ...jobsRef.current,
        [stackId]: normalized,
      })
    },
    [syncJobs],
  )

  useEffect(() => {
    const intervalId = setInterval(() => {
      const activeIds = Object.entries(jobsRef.current)
        .filter(([, job]) => isStackJobRunning(job))
        .map(([stackId]) => stackId)

      if (activeIds.length === 0) return

      void (async () => {
        const updates: Record<string, StackJobStatus> = { ...jobsRef.current }
        for (const stackId of activeIds) {
          try {
            const res = await stackApi.jobStatus(stackId)
            const merged = mergeStackJobStatus(updates[stackId], res.data ?? null)
            if (merged) {
              updates[stackId] = merged
            }
          } catch {
            // Keep polling on the next tick.
          }
        }
        syncJobs(updates)
      })()
    }, POLL_INTERVAL_MS)

    return () => clearInterval(intervalId)
  }, [syncJobs])

  const isStackBusy = useCallback(
    (stackId: string) => isStackJobRunning(jobs[stackId]),
    [jobs],
  )

  return { jobs, trackJob, isStackBusy }
}
