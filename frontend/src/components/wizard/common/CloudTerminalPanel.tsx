import { useCallback, useEffect, useRef, useState } from 'react'
import { FitAddon } from '@xterm/addon-fit'
import { Terminal } from '@xterm/xterm'
import { Loader2, TerminalSquare, X } from 'lucide-react'
import '@xterm/xterm/css/xterm.css'
import { BootstrapScriptStickyPanel } from '@/components/wizard/common/BootstrapScriptStickyPanel'
import { signalRService } from '@/services/signalr'
import { systemApi } from '@/services/api'
import type { DeploymentConfigDto } from '@/types/stack.types'
import { cn } from '@/lib/utils'

const CLOUD_TERMINAL_HUB = '/hubs/cloud-terminal'

function utf8ToBase64(value: string): string {
  const bytes = new TextEncoder().encode(value)
  let binary = ''
  for (const byte of bytes) {
    binary += String.fromCharCode(byte)
  }
  return btoa(binary)
}

function base64ToUint8Array(base64: string): Uint8Array {
  const binary = atob(base64)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i)
  }
  return bytes
}

interface CloudTerminalPanelProps {
  deployment: DeploymentConfigDto
  credentialsReady: boolean
  disabled?: boolean
  bootstrapScript?: string
  sshUser?: string
}

export function CloudTerminalPanel({
  deployment,
  credentialsReady,
  disabled = false,
  bootstrapScript,
  sshUser = '',
}: CloudTerminalPanelProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const terminalRef = useRef<Terminal | null>(null)
  const fitAddonRef = useRef<FitAddon | null>(null)
  const sessionActiveRef = useRef(false)

  const [open, setOpen] = useState(false)
  const [connecting, setConnecting] = useState(false)
  const [connected, setConnected] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [bootstrapRunning, setBootstrapRunning] = useState(false)
  const [bootstrapMessage, setBootstrapMessage] = useState<string | null>(null)
  const [bootstrapOutput, setBootstrapOutput] = useState<string | null>(null)
  const [bootstrapSuccess, setBootstrapSuccess] = useState<boolean | null>(null)

  const teardownTerminal = useCallback(async () => {
    sessionActiveRef.current = false
    setConnected(false)
    setConnecting(false)

    try {
      if (signalRService.getState(CLOUD_TERMINAL_HUB) === 'Connected') {
        await signalRService.invoke(CLOUD_TERMINAL_HUB, 'StopTerminal')
      }
    } catch {
      // Hub may already be disconnected.
    }

    signalRService.off(CLOUD_TERMINAL_HUB, 'TerminalOutput')
    signalRService.off(CLOUD_TERMINAL_HUB, 'TerminalStarted')
    signalRService.off(CLOUD_TERMINAL_HUB, 'TerminalError')
    signalRService.off(CLOUD_TERMINAL_HUB, 'TerminalClosed')

    await signalRService.disconnect(CLOUD_TERMINAL_HUB)

    terminalRef.current?.dispose()
    terminalRef.current = null
    fitAddonRef.current = null
  }, [])

  const closeTerminal = useCallback(async () => {
    await teardownTerminal()
    setOpen(false)
    setError(null)
  }, [teardownTerminal])

  const handleRunBootstrap = useCallback(async () => {
    setBootstrapRunning(true)
    setBootstrapMessage(null)
    setBootstrapOutput(null)
    setBootstrapSuccess(null)

    try {
      const res = await systemApi.runVpcBootstrap(deployment)
      setBootstrapSuccess(res.data.success)
      setBootstrapMessage(res.data.message)
      setBootstrapOutput(res.data.output?.trim() || null)
    } catch (err) {
      setBootstrapSuccess(false)
      setBootstrapMessage(err instanceof Error ? err.message : 'Failed to run bootstrap script.')
      setBootstrapOutput(null)
    } finally {
      setBootstrapRunning(false)
    }
  }, [deployment])

  useEffect(() => {
    if (!open || !containerRef.current) {
      return undefined
    }

    const term = new Terminal({
      cursorBlink: true,
      fontFamily: 'Consolas, "Courier New", monospace',
      fontSize: 13,
      theme: {
        background: '#0f172a',
        foreground: '#e2e8f0',
        cursor: '#38bdf8',
      },
    })
    const fitAddon = new FitAddon()
    term.loadAddon(fitAddon)
    term.open(containerRef.current)
    fitAddon.fit()

    term.onData((data) => {
      if (!sessionActiveRef.current) {
        return
      }

      void signalRService.invoke(CLOUD_TERMINAL_HUB, 'SendInput', utf8ToBase64(data)).catch(() => {
        // Ignore send failures when disconnected.
      })
    })

    terminalRef.current = term
    fitAddonRef.current = fitAddon

    const resize = () => fitAddon.fit()
    window.addEventListener('resize', resize)

    let cancelled = false

    void (async () => {
      setConnecting(true)
      setError(null)

      try {
        await signalRService.connect(CLOUD_TERMINAL_HUB)
        if (cancelled) {
          return
        }

        signalRService.on(CLOUD_TERMINAL_HUB, 'TerminalOutput', (base64: string) => {
          terminalRef.current?.write(base64ToUint8Array(base64))
        })

        signalRService.on(CLOUD_TERMINAL_HUB, 'TerminalStarted', () => {
          sessionActiveRef.current = true
          setConnecting(false)
          setConnected(true)
          term.focus()
        })

        signalRService.on(CLOUD_TERMINAL_HUB, 'TerminalError', (message: string) => {
          setError(message)
          setConnecting(false)
          setConnected(false)
          sessionActiveRef.current = false
        })

        signalRService.on(CLOUD_TERMINAL_HUB, 'TerminalClosed', () => {
          setConnected(false)
          setConnecting(false)
          sessionActiveRef.current = false
        })

        await signalRService.invoke(CLOUD_TERMINAL_HUB, 'StartTerminal', deployment)
      } catch (err) {
        if (!cancelled) {
          setConnecting(false)
          setConnected(false)
          sessionActiveRef.current = false
          setError(err instanceof Error ? err.message : 'Failed to open terminal.')
        }
      }
    })()

    return () => {
      cancelled = true
      window.removeEventListener('resize', resize)
      void teardownTerminal()
      term.dispose()
      terminalRef.current = null
      fitAddonRef.current = null
    }
  }, [deployment, open, teardownTerminal])

  useEffect(
    () => () => {
      void teardownTerminal()
    },
    [teardownTerminal]
  )

  return (
    <div className="relative overflow-visible">
      <div className="rounded-lg border border-gray-200 bg-white p-3 space-y-3">
        <div>
          <p className="text-xs font-semibold text-gray-900">Terminal &amp; bootstrap</p>
          <p className="mt-0.5 text-[11px] text-gray-600">
            Open an in-browser SSH session, then paste the bootstrap script to prepare your server.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {!open ? (
            <button
              type="button"
              onClick={() => setOpen(true)}
              disabled={disabled || !credentialsReady}
              className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-60"
            >
              <TerminalSquare className="h-3.5 w-3.5" aria-hidden="true" />
              Open terminal
            </button>
          ) : (
            <button
              type="button"
              onClick={() => void closeTerminal()}
              className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50"
            >
              <X className="h-3.5 w-3.5" aria-hidden="true" />
              Close terminal
            </button>
          )}
          {!credentialsReady ? (
            <span className="text-xs text-amber-800">Enter host, SSH user, and key above first.</span>
          ) : open && connected ? (
            <span className="text-xs text-green-700">Connected — paste the bootstrap script and press Enter.</span>
          ) : open && connecting ? (
            <span className="inline-flex items-center gap-1.5 text-xs text-gray-600">
              <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
              Connecting over SSH…
            </span>
          ) : null}
        </div>

        {error ? <p className="text-xs text-red-700">{error}</p> : null}

        {open ? (
          <div
            className={cn(
              'overflow-hidden rounded-md border border-slate-700 bg-slate-900',
              connecting && 'opacity-80'
            )}
          >
            <div ref={containerRef} className="h-72 w-full p-1 lg:h-80" />
          </div>
        ) : null}
      </div>

      {open ? (
        <BootstrapScriptStickyPanel
          script={bootstrapScript}
          sshUser={sshUser}
          onRunScript={handleRunBootstrap}
          running={bootstrapRunning}
          runMessage={bootstrapMessage}
          runSuccess={bootstrapSuccess}
          runOutput={bootstrapOutput}
          className={cn(
            'pointer-events-auto z-[9999] w-[min(280px,calc(100vw-2rem))] overflow-visible',
            'fixed bottom-4 right-4 lg:absolute lg:bottom-auto lg:right-auto lg:left-full lg:top-14 lg:ml-3'
          )}
        />
      ) : null}
    </div>
  )
}
