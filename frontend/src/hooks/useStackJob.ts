import { useState, useEffect, useRef } from 'react'
import { useSignalR } from './useSignalR'
import { stackApi } from '@/services/api'
import type { StackJobStatus } from '@/types/stack.types'

const POLL_INTERVAL_MS = 3000

/**
 * Tracks the detached stack lifecycle background job (start/stop/restart/start-database) for a stack.
 * Reattaches after navigating away or a page refresh by fetching the current status on mount and
 * subscribing to the SignalR stream for live updates. Falls back to polling while a job is running in
 * case a SignalR event is missed. Mirrors {@link useArmoryJob}.
 */
export function useStackJob(stackId: string | null) {
  const [job, setJob] = useState<StackJobStatus | null>(null)
  const jobRef = useRef<StackJobStatus | null>(null)

  const setJobBoth = (next: StackJobStatus | null) => {
    jobRef.current = next
    setJob(next)
  }

  const { on, invoke, getState } = useSignalR({
    hubUrl: '/hubs/stack-progress',
    autoConnect: !!stackId,
  })

  // Initial fetch (reattach) + polling fallback while a job is running.
  useEffect(() => {
    if (!stackId) return

    let cancelled = false

    const poll = async () => {
      try {
        const res = await stackApi.jobStatus(stackId)
        if (!cancelled && res.data) {
          setJobBoth(res.data)
        }
      } catch {
        // Non-fatal; SignalR may still deliver updates.
      }
    }

    poll()
    const intervalId = setInterval(() => {
      if (jobRef.current?.isRunning) {
        poll()
      }
    }, POLL_INTERVAL_MS)

    return () => {
      cancelled = true
      clearInterval(intervalId)
    }
  }, [stackId])

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
        setJobBoth(status)
      }
    })

    return () => {
      if (getState() === 'Connected') {
        invoke('UnsubscribeFromStack', stackId).catch(() => {})
      }
      cleanup()
    }
  }, [stackId, on, invoke, getState])

  const isRunning = job?.isRunning ?? false

  // Lets callers (the trigger buttons) seed the status returned by the enqueue request so the UI reflects
  // the job instantly and the polling fallback engages even before SignalR delivers an event.
  const applyStatus = (status: StackJobStatus | null | undefined) => {
    if (status) setJobBoth(status)
  }

  return { job, isStackBusy: isRunning, applyStatus }
}
