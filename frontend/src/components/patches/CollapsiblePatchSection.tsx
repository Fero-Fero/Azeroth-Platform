import { useEffect, useState, type ReactNode } from 'react'
import { ChevronDown, Loader2 } from 'lucide-react'

interface CollapsiblePatchSectionProps {
  title: string
  count: number
  /** Persists collapse state across visits when set (e.g. patch key + category). */
  storageKey?: string
  /** Collapsed by default when no stored preference exists. */
  defaultCollapsed?: boolean
  uploading?: boolean
  error?: string | null
  headerActions?: ReactNode
  children: ReactNode
}

function readStoredCollapsed(storageKey: string | undefined, defaultCollapsed: boolean): boolean {
  if (!storageKey) {
    return defaultCollapsed
  }
  try {
    const stored = localStorage.getItem(storageKey)
    if (stored === 'true') return true
    if (stored === 'false') return false
  } catch {
    // ignore storage errors
  }
  return defaultCollapsed
}

export default function CollapsiblePatchSection({
  title,
  count,
  storageKey,
  defaultCollapsed = false,
  uploading,
  error,
  headerActions,
  children,
}: CollapsiblePatchSectionProps) {
  const [collapsed, setCollapsed] = useState(() =>
    readStoredCollapsed(storageKey, defaultCollapsed)
  )

  useEffect(() => {
    if (!storageKey) {
      return
    }
    try {
      localStorage.setItem(storageKey, String(collapsed))
    } catch {
      // ignore storage errors
    }
  }, [collapsed, storageKey])

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm">
      <div className="flex items-start justify-between gap-2">
        <button
          type="button"
          onClick={() => setCollapsed((value) => !value)}
          className="flex min-w-0 flex-1 items-center gap-2 rounded-md py-0.5 text-left hover:bg-gray-50/80"
          aria-expanded={!collapsed}
        >
          <ChevronDown
            className={`h-4 w-4 shrink-0 text-gray-500 transition-transform ${collapsed ? '-rotate-90' : ''}`}
          />
          <span className="font-semibold text-gray-800">{title}</span>
          <span className="font-normal text-gray-400">({count})</span>
          {uploading && (
            <span className="inline-flex items-center gap-1 text-xs font-normal text-blue-600">
              <Loader2 className="h-4 w-4 animate-spin" /> Uploading...
            </span>
          )}
          {collapsed && error && (
            <span className="truncate text-xs font-normal text-red-600">- {error}</span>
          )}
        </button>
        {headerActions && (
          <div className="flex shrink-0 items-center gap-2" onClick={(e) => e.stopPropagation()}>
            {headerActions}
          </div>
        )}
      </div>

      {!collapsed && <div className="mt-2">{children}</div>}
    </div>
  )
}

/** Scroll long file lists instead of stretching the whole patch detail panel. */
export function patchFileListClassName(fileCount: number): string {
  return fileCount > 12
    ? 'mt-3 max-h-[min(24rem,50vh)] space-y-2 overflow-y-auto pr-1'
    : 'mt-3 space-y-2'
}
