import { useState } from 'react'
import { Check, Copy, Loader2, Play, StickyNote } from 'lucide-react'
import { cn } from '@/lib/utils'

interface BootstrapScriptStickyPanelProps {
  script?: string
  sshUser: string
  className?: string
  onRunScript?: () => void
  running?: boolean
  runMessage?: string | null
  runSuccess?: boolean | null
  runOutput?: string | null
}

export function BootstrapScriptStickyPanel({
  script,
  sshUser,
  className,
  onRunScript,
  running = false,
  runMessage = null,
  runSuccess = null,
  runOutput = null,
}: BootstrapScriptStickyPanelProps) {
  const [copied, setCopied] = useState(false)
  const user = sshUser.trim() || 'ubuntu'

  const handleCopy = async () => {
    if (!script) {
      return
    }

    try {
      await navigator.clipboard.writeText(script)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 2000)
    } catch {
      setCopied(false)
    }
  }

  return (
    <aside
      className={cn(
        'relative z-[9999] flex flex-col overflow-visible rounded-lg border border-amber-200 bg-amber-50 shadow-lg ring-1 ring-amber-100/80',
        className
      )}
      aria-label="Bootstrap script to paste in terminal"
    >
      <div className="flex items-start gap-2 border-b border-amber-200 bg-amber-100/80 px-3 py-2">
        <StickyNote className="mt-0.5 h-4 w-4 shrink-0 text-amber-800" aria-hidden="true" />
        <div className="min-w-0 flex-1">
          <p className="text-xs font-semibold text-amber-950">Bootstrap script</p>
          <p className="mt-0.5 text-[11px] leading-snug text-amber-900">
            Prefer <span className="font-medium">Run script</span> - browser paste can skip lines. SSH user:{' '}
            <span className="font-mono">{user}</span>
          </p>
        </div>
      </div>
      <div className="flex flex-wrap gap-2 px-3 py-2">
        {onRunScript ? (
          <button
            type="button"
            onClick={() => void onRunScript()}
            disabled={!script || running}
            className="inline-flex items-center gap-1.5 rounded-md border border-amber-400 bg-amber-200/80 px-2.5 py-1 text-[11px] font-semibold text-amber-950 hover:bg-amber-200 disabled:opacity-60"
          >
            {running ? (
              <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />
            ) : (
              <Play className="h-3 w-3" aria-hidden="true" />
            )}
            {running ? 'Running…' : 'Run script'}
          </button>
        ) : null}
        <button
          type="button"
          onClick={() => void handleCopy()}
          disabled={!script}
          className="inline-flex items-center gap-1.5 rounded-md border border-amber-300 bg-white px-2.5 py-1 text-[11px] font-semibold text-amber-950 hover:bg-amber-100 disabled:opacity-60"
        >
          {copied ? (
            <>
              <Check className="h-3 w-3" aria-hidden="true" />
              Copied
            </>
          ) : (
            <>
              <Copy className="h-3 w-3" aria-hidden="true" />
              Copy script
            </>
          )}
        </button>
      </div>
      {runMessage ? (
        <p
          className={cn(
            'mx-3 text-[11px]',
            runSuccess ? 'text-green-900' : 'text-red-900'
          )}
        >
          {runMessage}
        </p>
      ) : null}
      {runOutput ? (
        <pre className="mx-3 max-h-32 overflow-auto rounded border border-amber-200 bg-white p-2 font-mono text-[10px] leading-relaxed text-gray-800">
          {runOutput}
        </pre>
      ) : null}
      {script ? (
        <pre className="mx-3 mb-3 max-h-[min(420px,50vh)] flex-1 overflow-auto rounded border border-amber-200 bg-white p-2 font-mono text-[10px] leading-relaxed text-gray-900">
          {script}
        </pre>
      ) : (
        <p className="px-3 pb-3 text-[11px] text-amber-900">Loading script…</p>
      )}
    </aside>
  )
}
