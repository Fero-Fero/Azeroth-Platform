import { useState, useEffect, useRef, useCallback } from 'react'
import { isStackJobRunning, mergeStackJobStatus, normalizeStackJobStatus } from '@/lib/stackJob'
import { useSignalR } from './useSignalR'
import { stackApi } from '@/services/api'
import type { StackJobStatus } from '@/types/stack.types'

const POLL_INTERVAL_MS = 2000
const POST_TERMINAL_POLL_MS = 15000

/**
 * Tracks the detached stack lifecycle background job (start/stop/restart/start-database/apply-public-host)
 * for a stack. Reattaches after navigating away or a page refresh by fetching the current status on mount
 * and subscribing to the SignalR stream for live updates. Falls back to polling while a job is running in
 * case a SignalR event is missed. Mirrors {@link useArmoryJob}.
 */
export function useStackJob(stackId: string | null) {
  const [job, setJob] = useState<StackJobStatus | null>(null)
  const jobRef = useRef<StackJobStatus | null>(null)
  const pollUntilRef = useRef<number>(0)

  const setJobBoth = useCallback((next: StackJobStatus | null) => {
    jobRef.current = next
    setJob(next)
    if (next && isStackJobRunning(next)) {
      pollUntilRef.current = Date.now() + POST_TERMINAL_POLL_MS
    }
  }, [])

  const { on, invoke, getState } = useSignalR({
    hubUrl: '/hubs/stack-progress',
    autoConnect: !!stackId,
  })

  const pollNow = useCallback(async () => {
    if (!stackId) return

    try {
      const res = await stackApi.jobStatus(stackId)
      const merged = mergeStackJobStatus(jobRef.current, res.data ?? null)
      if (merged) {
        setJobBoth(merged)
      } else if (
        jobRef.current?.action === 'ApplyPublicHost'
        && !isStackJobRunning(jobRef.current)
      ) {
        setJobBoth(null)
      }
    } catch {
      // Non-fatal; SignalR may still deliver updates.
    }
  }, [stackId, setJobBoth])

  // Initial fetch (reattach) + polling fallback while a job is running or recently finished.
  useEffect(() => {
    if (!stackId) return

    let cancelled = false

    const poll = async () => {
      if (cancelled) return
      await pollNow()
    }

    poll()
    const intervalId = setInterval(() => {
      const shouldPoll =
        isStackJobRunning(jobRef.current) || Date.now() < pollUntilRef.current
      if (shouldPoll) {
        void poll()
      }
    }, POLL_INTERVAL_MS)

    return () => {
      cancelled = true
      clearInterval(intervalId)
    }
  }, [stackId, pollNow])

  // Subscribe to live updates.
  useEffect(() => {
    if (!stackId) return

    const subscribeWhenReady = async () => {
      try {
        const maxWaitTime = 5000
        const startTime = Date.now()
        while (getState() !== 'Connected' && Date.now() - startTime < maxWaitTime) {
          await new Promise((resolve) => setTimeout(resolve, 100))
        }
        if (getState() !== 'Connected') return
        await invoke('SubscribeToStack', stackId)
      } catch (err) {
        console.error('Failed to subscribe to stack job:', err)
      }
    }

    subscribeWhenReady()

    const cleanup = on('StackJobUpdated', (status: StackJobStatus) => {
      if (status?.stackId === stackId) {
        const merged = mergeStackJobStatus(jobRef.current, status)
        if (merged) {
          setJobBoth(merged)
        }
      }
    })

    return () => {
      if (getState() === 'Connected') {
        invoke('UnsubscribeFromStack', stackId).catch(() => {})
      }
      cleanup()
    }
  }, [stackId, on, invoke, getState, setJobBoth])

  const isRunning = isStackJobRunning(job)

  const applyStatus = useCallback(
    (status: StackJobStatus | null | undefined) => {
      if (!status) return
      const normalized = normalizeStackJobStatus(status)
      if (!normalized) return
      setJobBoth(normalized)
      pollUntilRef.current = Date.now() + POST_TERMINAL_POLL_MS
      void pollNow()
    },
    [pollNow, setJobBoth],
  )

  return { job, isStackBusy: isRunning, applyStatus }
}
