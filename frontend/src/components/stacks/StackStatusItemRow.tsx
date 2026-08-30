import { useState, type ReactNode } from 'react'
import { ChevronDown, Loader2 } from 'lucide-react'

export type StackStatusLevel = 'error' | 'warning' | 'info' | 'success' | 'loading'

const levelStyles: Record<
  StackStatusLevel,
  { dot: string; title: string; row: string }
> = {
  error: {
    dot: 'bg-red-500',
    title: 'text-red-950',
    row: 'hover:bg-red-50/60',
  },
  warning: {
    dot: 'bg-amber-500',
    title: 'text-amber-950',
    row: 'hover:bg-amber-50/60',
  },
  info: {
    dot: 'bg-blue-500',
    title: 'text-blue-950',
    row: 'hover:bg-blue-50/60',
  },
  success: {
    dot: 'bg-green-500',
    title: 'text-green-950',
    row: 'hover:bg-green-50/60',
  },
  loading: {
    dot: 'bg-gray-400',
    title: 'text-gray-900',
    row: 'hover:bg-gray-50',
  },
}

export interface StackStatusItemRowProps {
  id: string
  level: StackStatusLevel
  title: string
  summary?: string
  defaultExpanded?: boolean
  details?: ReactNode
  action?: ReactNode
  /** When true, omit the expand control even if details are present. */
  forceCollapsed?: boolean
}

export default function StackStatusItemRow({
  level,
  title,
  summary,
  defaultExpanded = false,
  details,
  action,
  forceCollapsed = false,
}: StackStatusItemRowProps) {
  const [expanded, setExpanded] = useState(defaultExpanded)
  const styles = levelStyles[level]
  const hasDetails = !!details && !forceCollapsed

  return (
    <div className="border-b border-gray-200 last:border-b-0">
      <div
        className={`flex items-start gap-3 px-4 py-3 ${hasDetails ? styles.row : ''}`.trim()}
      >
        <div className="flex w-5 shrink-0 items-center justify-center pt-0.5" aria-hidden="true">
          {level === 'loading' ? (
            <Loader2 className="h-4 w-4 animate-spin text-gray-500" />
          ) : (
            <span className={`inline-block h-2.5 w-2.5 rounded-full ${styles.dot}`} />
          )}
        </div>

        <div className="min-w-0 flex-1">
          <div className="flex items-start gap-2">
            {hasDetails ? (
              <button
                type="button"
                onClick={() => setExpanded((value) => !value)}
                className={`group flex min-w-0 flex-1 items-start gap-2 text-left ${styles.title}`}
                aria-expanded={expanded}
              >
                <span className="text-sm font-medium leading-snug">{title}</span>
                <ChevronDown
                  className={`mt-0.5 h-4 w-4 shrink-0 text-gray-400 transition-transform ${
                    expanded ? 'rotate-180' : ''
                  }`}
                />
              </button>
            ) : (
              <div className={`text-sm font-medium leading-snug ${styles.title}`}>{title}</div>
            )}
          </div>
          {summary && (
            <p className={`mt-0.5 text-xs leading-relaxed text-gray-600 ${hasDetails && expanded ? 'hidden' : ''}`.trim()}>
              {summary}
            </p>
          )}
        </div>

        {action && <div className="shrink-0 self-center">{action}</div>}
      </div>

      {hasDetails && expanded && (
        <div className="border-t border-gray-100 bg-gray-50/70 px-4 py-3 pl-12 text-sm text-gray-700">
          {details}
        </div>
      )}
    </div>
  )
}
