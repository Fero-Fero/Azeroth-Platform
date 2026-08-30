import * as signalR from '@microsoft/signalr'

interface HubEntry {
  connection: signalR.HubConnection
  connectionPromise: Promise<void> | null
}

/**
 * Manages SignalR connections keyed by hub URL. Each hub (e.g. build progress vs container logs) is a
 * distinct server endpoint with its own methods, so they MUST each get their own connection - sharing a
 * single connection sends invocations to whichever hub connected first, producing
 * "Method does not exist" errors (e.g. StartStreamingLogs landing on BuildProgressHub).
 */
class SignalRService {
  private hubs = new Map<string, HubEntry>()

  async connect(hubUrl: string): Promise<void> {
    const existing = this.hubs.get(hubUrl)
    if (existing) {
      if (existing.connection.state === signalR.HubConnectionState.Connected) {
        return
      }
      if (existing.connectionPromise) {
        return existing.connectionPromise
      }
    }

    // In development, use full backend URL to avoid Vite proxy issues with WebSockets
    // In production, use relative URL (same origin)
    const fullUrl = import.meta.env.DEV
      ? `http://localhost:5128${hubUrl}`
      : hubUrl

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(fullUrl, {
        // Hubs require the admin bearer token; SignalR sends it as ?access_token=... for WebSockets.
        accessTokenFactory: () => localStorage.getItem('azp_admin_token') ?? '',
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          // Exponential backoff: 0s, 2s, 10s, 30s, then 30s
          if (retryContext.previousRetryCount === 0) return 0
          if (retryContext.previousRetryCount === 1) return 2000
          if (retryContext.previousRetryCount === 2) return 10000
          return 30000
        }
      })
      .configureLogging(signalR.LogLevel.Information)
      .build()

    const entry: HubEntry = { connection, connectionPromise: null }
    this.hubs.set(hubUrl, entry)

    // Setup reconnection handlers
    connection.onreconnecting((error) => {
      console.warn(`SignalR reconnecting (${hubUrl})...`, error)
    })

    connection.onreconnected((connectionId) => {
      console.log(`SignalR reconnected (${hubUrl}):`, connectionId)
    })

    connection.onclose((error) => {
      console.error(`SignalR connection closed (${hubUrl}):`, error)
      entry.connectionPromise = null
    })

    entry.connectionPromise = connection
      .start()
      .then(() => {
        console.log('SignalR connected to:', fullUrl)
        entry.connectionPromise = null
      })
      .catch((err) => {
        console.error(`SignalR connection failed (${hubUrl}):`, err)
        entry.connectionPromise = null
        // Drop the failed entry so a later connect() can retry cleanly.
        if (this.hubs.get(hubUrl) === entry) {
          this.hubs.delete(hubUrl)
        }
        throw err
      })

    return entry.connectionPromise
  }

  async disconnect(hubUrl?: string): Promise<void> {
    if (hubUrl) {
      const entry = this.hubs.get(hubUrl)
      if (entry) {
        this.hubs.delete(hubUrl)
        await entry.connection.stop()
      }
      return
    }

    const entries = [...this.hubs.values()]
    this.hubs.clear()
    await Promise.all(entries.map((entry) => entry.connection.stop()))
  }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any -- SignalR event callbacks can have varying signatures
  on(hubUrl: string, eventName: string, callback: (...args: any[]) => void): void {
    this.hubs.get(hubUrl)?.connection.on(eventName, callback)
  }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any -- SignalR event callbacks can have varying signatures
  off(hubUrl: string, eventName: string, callback?: (...args: any[]) => void): void {
    const connection = this.hubs.get(hubUrl)?.connection
    if (!connection) {
      return
    }

    // Remove only the specific handler when provided so co-located subscribers to the same event
    // don't tear each other down.
    if (callback) {
      connection.off(eventName, callback)
    } else {
      connection.off(eventName)
    }
  }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any -- SignalR invoke accepts dynamic parameters
  async invoke<T = any>(hubUrl: string, methodName: string, ...args: any[]): Promise<T> {
    const entry = this.hubs.get(hubUrl)
    if (!entry) {
      throw new Error(`SignalR not initialized for ${hubUrl}`)
    }

    // If not connected, try to wait for connection
    if (entry.connection.state !== signalR.HubConnectionState.Connected) {
      if (entry.connectionPromise) {
        await entry.connectionPromise
      } else {
        throw new Error(`SignalR not connected for ${hubUrl}`)
      }
    }

    return entry.connection.invoke<T>(methodName, ...args)
  }

  getState(hubUrl: string): signalR.HubConnectionState | string | null {
    return this.hubs.get(hubUrl)?.connection.state ?? null
  }
}

export const signalRService = new SignalRService()
