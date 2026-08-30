import { useState, useEffect, useCallback } from 'react'
import { useSignalR } from './useSignalR'

interface LogLine {
  message: string
  isError: boolean
  timestamp: Date
}

interface ContainerLogsState {
  logs: LogLine[]
  isStreaming: boolean
  error: string | null
}

const MAX_LOGS = 1000 // Keep last 1000 lines in memory

export function useContainerLogs(stackId: string, containerName: string) {
  const [state, setState] = useState<ContainerLogsState>({
    logs: [],
    isStreaming: false,
    error: null,
  })

  const { on, invoke, getState } = useSignalR({
    hubUrl: '/hubs/container-logs',
    autoConnect: true,
  })

  const startStreaming = useCallback(
    async (tail: number = 500) => {
      try {
        setState((prev) => ({ ...prev, isStreaming: true, error: null, logs: [] }))

        // Ensure connection is established
        const maxWaitTime = 5000 // 5 seconds
        const startTime = Date.now()

        while (getState() !== 'Connected' && Date.now() - startTime < maxWaitTime) {
          await new Promise((resolve) => setTimeout(resolve, 100))
        }

        if (getState() !== 'Connected') {
          throw new Error('SignalR connection timeout')
        }

        // Start streaming logs
        console.log('Invoking StartStreamingLogs with:', { stackId, containerName, tail })
        await invoke('StartStreamingLogs', stackId, containerName, tail)
        console.log(`Started streaming logs for container: ${containerName}`)
      } catch (err) {
        const errorMessage = err instanceof Error ? err.message : 'Failed to start log stream'
        setState((prev) => ({ ...prev, error: errorMessage, isStreaming: false }))
        console.error('Failed to start log stream:', err)
      }
    },
    [stackId, containerName, invoke, getState]
  )

  const stopStreaming = useCallback(async () => {
    try {
      if (getState() === 'Connected') {
        await invoke('StopStreamingLogs')
      }
      setState((prev) => ({ ...prev, isStreaming: false }))
    } catch (err) {
      console.error('Failed to stop log stream:', err)
    }
  }, [invoke, getState])

  const clearLogs = useCallback(() => {
    setState((prev) => ({ ...prev, logs: [], error: null }))
  }, [])

  useEffect(() => {
    // Listen for log stream events
    const cleanupStarted = on('LogStreamStarted', (containerName: string, initialLineCount: number) => {
      console.log(`Log stream started for ${containerName}, initial lines: ${initialLineCount}`)
      setState((prev) => ({ ...prev, isStreaming: true }))
    })

    const cleanupLogReceived = on('LogReceived', (message: string, isError: boolean) => {
      console.log('[LogReceived]', message.substring(0, 100)) // Debug: log first 100 chars
      setState((prev) => {
        const newLog: LogLine = {
          message,
          isError,
          timestamp: new Date(),
        }

        // Keep only last MAX_LOGS lines
        const newLogs = [...prev.logs, newLog].slice(-MAX_LOGS)

        return { ...prev, logs: newLogs }
      })
    })

    const cleanupEnded = on('LogStreamEnded', (reason: string) => {
      console.log(`Log stream ended: ${reason}`)
      setState((prev) => ({ ...prev, isStreaming: false }))
    })

    const cleanupError = on('LogStreamError', (errorMessage: string) => {
      console.error('Log stream error:', errorMessage)
      setState((prev) => ({
        ...prev,
        error: errorMessage,
        isStreaming: false,
      }))
    })

    return () => {
      cleanupStarted()
      cleanupLogReceived()
      cleanupEnded()
      cleanupError()
    }
  }, [on])

  // Auto-start streaming on mount
  useEffect(() => {
    startStreaming()
    return () => {
      stopStreaming()
    }
  }, []) // Empty deps - only run on mount/unmount

  return {
    logs: state.logs,
    isStreaming: state.isStreaming,
    error: state.error,
    startStreaming,
    stopStreaming,
    clearLogs,
  }
}
