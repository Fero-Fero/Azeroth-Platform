import { useState, useEffect, useRef } from 'react'
import { useSignalR } from './useSignalR'
import { stackApi } from '@/services/api'
import type { ClientJobStatus } from '@/types/stack.types'

const POLL_INTERVAL_MS = 3000

/**
 * Tracks the detached client file-server background job for a stack. Reattaches after a page refresh
 * by fetching the current status on mount and subscribing to the SignalR stream for live updates.
 */
export function useClientJob(stackId: string | null) {
  const [job, setJob] = useState<ClientJobStatus | null>(null)
  const jobRef = useRef<ClientJobStatus | null>(null)

  const setJobBoth = (next: ClientJobStatus | null) => {
    jobRef.current = next
    setJob(next)
  }

  const { on, invoke, getState } = useSignalR({
    hubUrl: '/hubs/stack-progress',
    autoConnect: !!stackId,
  })

  useEffect(() => {
    if (!stackId) return

    let cancelled = false

    const poll = async () => {
      try {
        const res = await stackApi.clientStatus(stackId)
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
        console.error('Failed to subscribe to client job:', err)
      }
    }

    subscribeWhenReady()

    const cleanup = on('ClientJobUpdated', (status: ClientJobStatus) => {
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

  const applyStatus = (status: ClientJobStatus | null | undefined) => {
    if (status) setJobBoth(status)
  }

  return { job, isClientBusy: isRunning, applyStatus }
}
