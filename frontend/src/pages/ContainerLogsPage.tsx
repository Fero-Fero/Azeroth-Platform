import { useEffect, useState, useRef, useMemo } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { ArrowLeft, Terminal, Download, Trash2, AlertCircle } from 'lucide-react'
import { useContainerLogs } from '@/hooks/useContainerLogs'
import AnsiToHtml from 'ansi-to-html'

export default function ContainerLogsPage() {
  const { stackId, containerName } = useParams<{ stackId: string; containerName: string }>()
  const navigate = useNavigate()
  
  console.log('ContainerLogsPage rendered', { stackId, containerName })
  
  const { logs, isStreaming, error } = useContainerLogs(stackId ?? '', containerName ?? '')
  const [autoScroll, setAutoScroll] = useState(true)
  const logsEndRef = useRef<HTMLDivElement>(null)
  const logsContainerRef = useRef<HTMLDivElement>(null)
  
  // ANSI to HTML converter with custom color mapping
  const ansiConverter = useMemo(() => new AnsiToHtml({
    fg: '#d4d4d4',
    bg: '#1a1a1a',
    newline: false,
    escapeXML: true,
    stream: false,
    colors: {
      0: '#ffffff',  // Map black to white for dark background
      1: '#ff5555',  // red
      2: '#50fa7b',  // green
      3: '#f1fa8c',  // yellow
      4: '#6272a4',  // blue
      5: '#ff79c6',  // magenta
      6: '#8be9fd',  // cyan
      7: '#f8f8f2',  // white/gray
    }
  }), [])

  useEffect(() => {
    if (autoScroll && logsEndRef.current) {
      logsEndRef.current.scrollIntoView({ behavior: 'smooth' })
    }
  }, [logs, autoScroll])

  const handleClearLogs = () => {
    // Clear logs (handled by hook state reset)
    window.location.reload()
  }

  const handleDownloadLogs = () => {
    const logText = logs.map((log) => `[${log.timestamp.toISOString()}] ${log.message}`).join('\n')
    const blob = new Blob([logText], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `${containerName}-logs-${new Date().toISOString()}.txt`
    a.click()
    URL.revokeObjectURL(url)
  }

  const handleBackToStack = () => {
    if (stackId) {
      navigate(`/stacks/${stackId}`)
    }
  }

  if (!stackId || !containerName) {
    return (
      <div className="text-center">
        <p className="text-gray-500">Invalid parameters</p>
      </div>
    )
  }

  return (
    <div className="mx-auto max-w-7xl">
      {/* Header */}
      <div className="mb-6">
        <button
          onClick={handleBackToStack}
          className="mb-4 inline-flex items-center gap-2 text-sm text-gray-600 hover:text-gray-900"
        >
          <ArrowLeft className="h-4 w-4" />
          Back to Stack Details
        </button>
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-3xl font-bold text-gray-900">Container Logs</h1>
            <p className="mt-1 text-gray-500">{containerName}</p>
          </div>
          <div className="flex items-center gap-2">
            {isStreaming && (
              <span className="inline-flex items-center gap-2 rounded-full bg-green-100 px-3 py-1 text-sm font-medium text-green-800">
                <span className="h-2 w-2 animate-pulse rounded-full bg-green-600"></span>
                Streaming
              </span>
            )}
          </div>
        </div>
      </div>

      {/* Error Alert */}
      {error && (
        <div className="mb-6 flex items-start gap-3 rounded-xl border border-red-200 bg-red-50 p-4">
          <AlertCircle className="h-5 w-5 flex-shrink-0 text-red-600" />
          <div>
            <h3 className="font-semibold text-red-900">Error</h3>
            <p className="mt-1 text-sm text-red-700">{error}</p>
          </div>
        </div>
      )}

      {/* Controls */}
      <div className="mb-4 flex items-center justify-between">
        <div className="flex items-center gap-4">
          <label className="flex items-center gap-2 text-sm text-gray-700">
            <input
              type="checkbox"
              checked={autoScroll}
              onChange={(e) => setAutoScroll(e.target.checked)}
              className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            />
            Auto-scroll
          </label>
          <span className="text-sm text-gray-500">{logs.length} lines</span>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={handleDownloadLogs}
            disabled={logs.length === 0}
            className="inline-flex items-center gap-2 rounded-lg border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <Download className="h-4 w-4" />
            Download
          </button>
          <button
            onClick={handleClearLogs}
            disabled={logs.length === 0}
            className="inline-flex items-center gap-2 rounded-lg border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <Trash2 className="h-4 w-4" />
            Clear
          </button>
        </div>
      </div>

      {/* Logs Container */}
      <div className="overflow-hidden rounded-xl border border-gray-200 bg-gray-900 shadow-lg">
        <div className="border-b border-gray-700 bg-gray-800 px-4 py-3">
          <div className="flex items-center gap-2">
            <Terminal className="h-5 w-5 text-gray-400" />
            <span className="font-mono text-sm text-gray-300">Container Logs</span>
          </div>
        </div>
        <div
          ref={logsContainerRef}
          className="h-[600px] overflow-y-auto bg-gray-900 p-4 font-mono text-sm text-gray-200"
          style={{ color: '#d4d4d4' }}
        >
          {logs.length === 0 && !isStreaming && (
            <div className="flex h-full items-center justify-center text-gray-500">
              No logs available
            </div>
          )}
          {logs.map((log, index) => {
            const htmlContent = ansiConverter.toHtml(log.message)
            return (
              <div
                key={index}
                className="py-0.5"
              >
                <span className="text-gray-500">[{log.timestamp.toLocaleTimeString()}]</span>{' '}
                <span dangerouslySetInnerHTML={{ __html: htmlContent }} />
              </div>
            )
          })}
          <div ref={logsEndRef} />
        </div>
      </div>
    </div>
  )
}
