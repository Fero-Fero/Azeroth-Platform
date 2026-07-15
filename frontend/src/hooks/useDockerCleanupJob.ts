import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useSignalR } from '@/hooks/useSignalR'
import { dockerApi } from '@/services/api'
import { dockerKeys } from '@/hooks/useStackDocker'
import type { DockerCleanupJobStatus } from '@/types/docker.types'

const POLL_INTERVAL_MS = 3000

/**
 * Tracks the detached Docker disk-reclaim background job. Reattaches after navigating away or refreshing
 * via polling and SignalR on the stack-progress hub.
 */
export function useDockerCleanupJob() {
  const queryClient = useQueryClient()
  const [job, setJob] = useState<DockerCleanupJobStatus | null>(null)
  const jobRef = useRef<DockerCleanupJobStatus | null>(null)

  const setJobBoth = (next: DockerCleanupJobStatus | null) => {
    jobRef.current = next
    setJob(next)
  }

  const { on, invoke, getState } = useSignalR({
    hubUrl: '/hubs/stack-progress',
    autoConnect: true,
  })

  useEffect(() => {
    let cancelled = false

    const poll = async () => {
      try {
        const res = await dockerApi.cleanupStatus()
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
  }, [])

  useEffect(() => {
    const subscribeWhenReady = async () => {
      try {
        const maxWaitTime = 5000
        const startTime = Date.now()
        while (getState() !== 'Connected' && Date.now() - startTime < maxWaitTime) {
          await new Promise((resolve) => setTimeout(resolve, 100))
        }
        if (getState() !== 'Connected') return
        await invoke('SubscribeToDockerCleanup')
      } catch (err) {
        console.error('Failed to subscribe to docker cleanup job:', err)
      }
    }

    subscribeWhenReady()

    const cleanup = on('DockerCleanupUpdated', (status: DockerCleanupJobStatus) => {
      if (status?.jobId) {
        setJobBoth(status)
      }
    })

    return () => {
      if (getState() === 'Connected') {
        invoke('UnsubscribeFromDockerCleanup').catch(() => {})
      }
      cleanup()
    }
  }, [on, invoke, getState])

  const invalidateDockerQueries = () => {
    queryClient.invalidateQueries({ queryKey: dockerKeys.disk })
    queryClient.invalidateQueries({ queryKey: ['stacks'] })
    queryClient.invalidateQueries({ queryKey: ['stack'] })
    queryClient.invalidateQueries({ queryKey: ['stack', 'docker'] })
  }

  const applyStatus = (status: DockerCleanupJobStatus | null | undefined) => {
    if (status) setJobBoth(status)
  }

  const startCleanup = async (action: import('@/types/docker.types').DockerCleanupJobAction = 'ReclaimDiskSpace') => {
    const res =
      action === 'CleanupOldBuilds'
        ? await dockerApi.cleanupOldBuilds()
        : await dockerApi.cleanupUnused()
    applyStatus(res.data)
    return res.data
  }

  return {
    job,
    isRunning: job?.isRunning ?? false,
    applyStatus,
    startCleanup,
    invalidateDockerQueries,
  }
}
