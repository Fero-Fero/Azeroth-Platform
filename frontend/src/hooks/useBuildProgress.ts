import { useState, useEffect, useRef } from 'react'
import { useSignalR } from './useSignalR'
import { buildApi } from '@/services/api'
import { BuildPhase, type ModuleCheckItemDto } from '@/types/stack.types'

interface BuildProgress {
  phase: BuildPhase
  percent: number
  step: string
  logs: string[]
  moduleResults: ModuleCheckItemDto[]
}

const POLL_INTERVAL_MS = 1000

function isTerminalPhase(phase: BuildPhase): boolean {
  return (
    phase === BuildPhase.Completed ||
    phase === BuildPhase.ModuleCheckPassed ||
    phase === BuildPhase.Failed
  )
}

function applyStatus(
  status: {
    currentPhase: BuildPhase
    progressPercent: number
    currentStep: string
    recentLogs?: string[]
    moduleResults?: ModuleCheckItemDto[]
    errorMessage?: string | null
  },
  setProgress: (progress: BuildProgress) => void,
  setError: (error: string | null) => void,
  setIsComplete: (complete: boolean) => void,
  isCompleteRef: { current: boolean },
) {
  setProgress({
    phase: status.currentPhase,
    percent: status.progressPercent,
    step: status.currentStep,
    logs: status.recentLogs ?? [],
    moduleResults: status.moduleResults ?? [],
  })
  if (status.currentPhase === BuildPhase.Failed) {
    setError(status.errorMessage ?? 'Build failed')
    setIsComplete(true)
    isCompleteRef.current = true
    return
  }

  if (status.errorMessage) {
    setError(status.errorMessage)
  } else {
    setError(null)
  }

  const complete = isTerminalPhase(status.currentPhase)
  setIsComplete(complete)
  isCompleteRef.current = complete
}

export function useBuildProgress(stackId: string | null) {
  const [progress, setProgress] = useState<BuildProgress>({
    phase: BuildPhase.Cloning,
    percent: 0,
    step: '',
    logs: [],
    moduleResults: [],
  })
  const [isComplete, setIsComplete] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const signalRActiveRef = useRef(false)
  const isCompleteRef = useRef(false)

  const { on, invoke, getState } = useSignalR({
    hubUrl: '/hubs/buildprogress',
    autoConnect: !!stackId,
  })

  useEffect(() => {
    if (!stackId) return

    const poll = async () => {
      try {
        const res = await buildApi.status(stackId)
        applyStatus(res.data, setProgress, setError, setIsComplete, isCompleteRef)
      } catch (err: unknown) {
        const status = (err as { response?: { status?: number } })?.response?.status
        if (status === 404 && !isCompleteRef.current) {
          setError('No build progress is available for this stack. It may have finished before you opened this page, or the platform restarted during the build.')
          setIsComplete(true)
          isCompleteRef.current = true
        }
      }
    }

    void poll()

    const intervalId = setInterval(poll, POLL_INTERVAL_MS)
    return () => clearInterval(intervalId)
  }, [stackId])

  useEffect(() => {
    if (!stackId) return

    const subscribeWhenReady = async () => {
      try {
        const maxWaitTime = 5000
        const startTime = Date.now()

        while (getState() !== 'Connected' && Date.now() - startTime < maxWaitTime) {
          await new Promise(resolve => setTimeout(resolve, 100))
        }

        if (getState() !== 'Connected') {
          console.error('SignalR connection timeout')
          return
        }

        await invoke('SubscribeToBuild', stackId)
        console.log(`Subscribed to build: ${stackId}`)
      } catch (err) {
        console.error('Failed to subscribe to build:', err)
      }
    }

    void subscribeWhenReady()

    const cleanupPhase = on(
      'BuildPhaseChanged',
      (receivedStackId: string, phase: BuildPhase) => {
        if (receivedStackId === stackId) {
          signalRActiveRef.current = true
          setProgress((prev) => ({ ...prev, phase }))
        }
      }
    )

    const cleanupProgress = on(
      'BuildProgressUpdated',
      (receivedStackId: string, percent: number, step: string) => {
        if (receivedStackId === stackId) {
          signalRActiveRef.current = true
          setProgress((prev) => ({
            ...prev,
            percent,
            step,
          }))
        }
      }
    )

    const cleanupLog = on(
      'BuildLogReceived',
      (receivedStackId: string, logLine: string) => {
        if (receivedStackId === stackId) {
          signalRActiveRef.current = true
          setProgress((prev) => ({
            ...prev,
            logs: [...prev.logs.slice(-50), logLine],
          }))
        }
      }
    )

    const cleanupModules = on(
      'ModuleCheckUpdated',
      (receivedStackId: string, items: ModuleCheckItemDto[]) => {
        if (receivedStackId === stackId) {
          signalRActiveRef.current = true
          setProgress((prev) => ({
            ...prev,
            moduleResults: items ?? [],
          }))
        }
      }
    )

    const cleanupComplete = on(
      'BuildCompleted',
      (receivedStackId: string) => {
        if (receivedStackId === stackId) {
          signalRActiveRef.current = true
          void (async () => {
            try {
              const res = await buildApi.status(stackId)
              applyStatus(res.data, setProgress, setError, setIsComplete, isCompleteRef)
            } catch {
              setIsComplete(true)
              isCompleteRef.current = true
            }
          })()
        }
      }
    )

    const cleanupFailed = on(
      'BuildFailed',
      (receivedStackId: string, errorMessage: string) => {
        if (receivedStackId === stackId) {
          signalRActiveRef.current = true
          if (errorMessage) {
            setError(errorMessage)
          }
          setProgress((prev) => ({ ...prev, phase: BuildPhase.Failed }))
          void (async () => {
            try {
              const res = await buildApi.status(stackId)
              applyStatus(res.data, setProgress, setError, setIsComplete, isCompleteRef)
            } catch {
              setIsComplete(true)
              isCompleteRef.current = true
            }
          })()
        }
      }
    )

    return () => {
      if (getState() === 'Connected') {
        invoke('UnsubscribeFromBuild', stackId).catch(console.error)
      }
      cleanupPhase()
      cleanupProgress()
      cleanupLog()
      cleanupModules()
      cleanupComplete()
      cleanupFailed()
    }
  }, [stackId, on, invoke, getState])

  const beginNewJob = () => {
    isCompleteRef.current = false
    setIsComplete(false)
    setError(null)
    setProgress({
      phase: BuildPhase.Building,
      percent: 0,
      step: 'Starting Docker image build…',
      logs: [],
      moduleResults: [],
    })
  }

  return { progress, isComplete, error, beginNewJob }
}
