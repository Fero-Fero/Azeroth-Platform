import { useEffect, useRef, useCallback } from 'react'
import { signalRService } from '@/services/signalr'

interface UseSignalROptions {
  hubUrl: string
  autoConnect?: boolean
}

export function useSignalR(options: UseSignalROptions) {
  const { hubUrl, autoConnect = true } = options
  const isConnecting = useRef(false)

  useEffect(() => {
    if (autoConnect && !isConnecting.current) {
      isConnecting.current = true
      signalRService.connect(hubUrl).catch((err) => {
        console.error('Failed to connect to SignalR:', err)
        isConnecting.current = false
      })
    }

    return () => {
      // Don't disconnect on unmount - keep connection alive for other components
    }
  }, [hubUrl, autoConnect])

  // eslint-disable-next-line @typescript-eslint/no-explicit-any -- SignalR event callbacks can have varying signatures
  const on = useCallback((eventName: string, callback: (...args: any[]) => void) => {
    signalRService.on(hubUrl, eventName, callback)

    // Return cleanup that removes only this handler for this hub.
    return () => {
      signalRService.off(hubUrl, eventName, callback)
    }
  }, [hubUrl])

  // eslint-disable-next-line @typescript-eslint/no-explicit-any -- SignalR invoke accepts dynamic parameters
  const invoke = useCallback(async <T = any>(methodName: string, ...args: any[]): Promise<T> => {
    return signalRService.invoke<T>(hubUrl, methodName, ...args)
  }, [hubUrl])

  const getState = useCallback(() => signalRService.getState(hubUrl), [hubUrl])

  return {
    on,
    invoke,
    getState,
    state: signalRService.getState(hubUrl),
  }
}
