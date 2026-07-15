import { useState, useEffect, useRef } from 'react'
import { useSignalR } from './useSignalR'
import { stackApi } from '@/services/api'
import type { ArmoryJobStatus } from '@/types/stack.types'

const POLL_INTERVAL_MS = 3000

/**
 * Tracks the detached armory background job for a stack. Reattaches after a page refresh by fetching
 * the current status on mount and subscribing to the SignalR stream for live updates. Falls back to
 * polling while a job is running in case a SignalR event is missed.
 */
export function useArmoryJob(stackId: string | null) {
  const [job, setJob] = useState<ArmoryJobStatus | null>(null)
  const jobRef = useRef<ArmoryJobStatus | null>(null)

  const setJobBoth = (next: ArmoryJobStatus | null) => {
    jobRef.current = next
    setJob(next)
  }

  const { on, invoke, getState } = useSignalR({
    hubUrl: '/hubs/armory-progress',
    autoConnect: !!stackId,
  })

  // Initial fetch (reattach) + polling fallback while a job is running.
  useEffect(() => {
    if (!stackId) return

    let cancelled = false

    const poll = async () => {
      try {
        const res = await stackApi.armoryStatus(stackId)
        if (!cancelled && res.data) {
          setJobBoth(res.data)
        }
      } catch {
        // Non-fatal; SignalR may still deliver updates.
      }
    }

    poll()
    const intervalId = setInterval(() => {
      // Only keep polling while something is in flight; once terminal, SignalR/refetch covers it.
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
        await invoke('SubscribeToArmory', stackId)
      } catch (err) {
        console.error('Failed to subscribe to armory job:', err)
      }
    }

    subscribeWhenReady()

    const cleanup = on('ArmoryJobUpdated', (status: ArmoryJobStatus) => {
      if (status?.stackId === stackId) {
        setJobBoth(status)
      }
    })

    return () => {
      if (getState() === 'Connected') {
        invoke('UnsubscribeFromArmory', stackId).catch(() => {})
      }
      cleanup()
    }
  }, [stackId, on, invoke, getState])

  const isRunning = job?.isRunning ?? false

  // Lets callers (e.g. the trigger buttons) seed the status returned by the enqueue request so the UI
  // reflects the job instantly and the polling fallback engages even before SignalR delivers an event.
  const applyStatus = (status: ArmoryJobStatus | null | undefined) => {
    if (status) setJobBoth(status)
  }

  return { job, isArmoryBusy: isRunning, applyStatus }
}
