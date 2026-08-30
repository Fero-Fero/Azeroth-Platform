import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Check, ChevronsUpDown, GitBranch, Loader2, Search } from 'lucide-react'
import { cn } from '@/lib/utils'

interface BranchComboboxProps {
  id?: string
  value: string
  onChange: (branch: string) => void
  branches: string[]
  isLoading?: boolean
  /** Error message from the branch lookup (e.g. repo unreachable/private). */
  error?: string | null
  /** True when there is no repository URL to look up branches for yet. */
  disabled?: boolean
  /** Highlights the control in red when the form field has a validation error. */
  hasError?: boolean
  placeholder?: string
}

interface PanelPosition {
  left: number
  width: number
  top?: number
  bottom?: number
  maxHeight: number
}

/**
 * Searchable branch picker for the custom-fork step. Fetched branches are shown in a dropdown with an
 * inline search box. Typing a branch that is not in the list is still allowed (the "Use ..." option),
 * so private/unlisted branches keep working even when the lookup returns nothing.
 *
 * The panel is rendered in a portal with fixed positioning so it is never clipped by the wizard's
 * scroll container, which would otherwise cut the list down to a few visible rows. It flips above the
 * trigger when there is more room there and caps its height to the available viewport space.
 */
export function BranchCombobox({
  id,
  value,
  onChange,
  branches,
  isLoading = false,
  error = null,
  disabled = false,
  hasError = false,
  placeholder = 'master',
}: BranchComboboxProps) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [position, setPosition] = useState<PanelPosition | null>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const panelRef = useRef<HTMLDivElement>(null)
  const searchRef = useRef<HTMLInputElement>(null)

  const updatePosition = useCallback(() => {
    const trigger = triggerRef.current
    if (!trigger) return
    const rect = trigger.getBoundingClientRect()
    const margin = 8
    const spaceBelow = window.innerHeight - rect.bottom - margin
    const spaceAbove = rect.top - margin
    const openUp = spaceBelow < 240 && spaceAbove > spaceBelow
    setPosition({
      left: rect.left,
      width: rect.width,
      maxHeight: Math.max(180, Math.min(360, openUp ? spaceAbove : spaceBelow)),
      ...(openUp
        ? { bottom: window.innerHeight - rect.top + 4 }
        : { top: rect.bottom + 4 }),
    })
  }, [])

  useLayoutEffect(() => {
    if (open) updatePosition()
  }, [open, updatePosition])

  useEffect(() => {
    if (!open) return
    const onReflow = () => updatePosition()
    // Capture phase so we also react to scrolling of ancestor scroll containers, not just the window.
    window.addEventListener('scroll', onReflow, true)
    window.addEventListener('resize', onReflow)
    return () => {
      window.removeEventListener('scroll', onReflow, true)
      window.removeEventListener('resize', onReflow)
    }
  }, [open, updatePosition])

  useEffect(() => {
    if (!open) return
    const handlePointer = (event: MouseEvent) => {
      const target = event.target as Node
      if (triggerRef.current?.contains(target)) return
      if (panelRef.current?.contains(target)) return
      setOpen(false)
    }
    const handleKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', handlePointer)
    document.addEventListener('keydown', handleKey)
    return () => {
      document.removeEventListener('mousedown', handlePointer)
      document.removeEventListener('keydown', handleKey)
    }
  }, [open])

  useEffect(() => {
    if (open) {
      setQuery('')
      const timer = window.setTimeout(() => searchRef.current?.focus(), 0)
      return () => window.clearTimeout(timer)
    }
  }, [open])

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q) return branches
    return branches.filter((branch) => branch.toLowerCase().includes(q))
  }, [branches, query])

  const trimmedQuery = query.trim()
  const showCustomOption =
    trimmedQuery.length > 0 && !branches.some((branch) => branch === trimmedQuery)

  const select = (branch: string) => {
    onChange(branch)
    setOpen(false)
  }

  return (
    <div className="relative">
      <button
        id={id}
        ref={triggerRef}
        type="button"
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => !disabled && setOpen((prev) => !prev)}
        className={cn(
          'flex w-full items-center justify-between rounded-md border px-3 py-2 text-left font-mono text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
          disabled ? 'cursor-not-allowed bg-gray-100 text-gray-400' : 'bg-white',
          hasError ? 'border-red-400' : 'border-gray-300'
        )}
      >
        <span className="flex min-w-0 items-center gap-2">
          <GitBranch className="h-4 w-4 shrink-0 text-gray-400" aria-hidden="true" />
          <span className={cn('truncate', !value && 'text-gray-400')}>
            {value || placeholder}
          </span>
        </span>
        {isLoading ? (
          <Loader2 className="h-4 w-4 shrink-0 animate-spin text-gray-400" aria-hidden="true" />
        ) : (
          <ChevronsUpDown className="h-4 w-4 shrink-0 text-gray-400" aria-hidden="true" />
        )}
      </button>

      {open && !disabled && position &&
        createPortal(
          <div
            ref={panelRef}
            style={{
              position: 'fixed',
              left: position.left,
              width: position.width,
              top: position.top,
              bottom: position.bottom,
              maxHeight: position.maxHeight,
            }}
            className="z-50 flex flex-col overflow-hidden rounded-md border border-gray-200 bg-white shadow-lg"
          >
            <div className="flex shrink-0 items-center gap-2 border-b border-gray-100 px-3 py-2">
              <Search className="h-4 w-4 shrink-0 text-gray-400" aria-hidden="true" />
              <input
                ref={searchRef}
                type="text"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault()
                    if (filtered.length > 0) select(filtered[0])
                    else if (showCustomOption) select(trimmedQuery)
                  }
                }}
                placeholder="Search branches…"
                className="w-full border-0 p-0 text-sm focus:outline-none focus:ring-0"
              />
            </div>

            <ul role="listbox" className="flex-1 overflow-y-auto py-1">
              {isLoading && (
                <li className="px-3 py-2 text-sm text-gray-400">Loading branches…</li>
              )}

              {!isLoading && error && (
                <li className="px-3 py-2 text-sm text-amber-600">{error}</li>
              )}

              {!isLoading &&
                filtered.map((branch) => {
                  const selected = branch === value
                  return (
                    <li key={branch}>
                      <button
                        type="button"
                        role="option"
                        aria-selected={selected}
                        onClick={() => select(branch)}
                        className={cn(
                          'flex w-full items-center justify-between px-3 py-2 text-left font-mono text-sm hover:bg-blue-50',
                          selected ? 'text-blue-700' : 'text-gray-700'
                        )}
                      >
                        <span className="truncate">{branch}</span>
                        {selected && <Check className="h-4 w-4 shrink-0 text-blue-600" aria-hidden="true" />}
                      </button>
                    </li>
                  )
                })}

              {showCustomOption && (
                <li>
                  <button
                    type="button"
                    role="option"
                    aria-selected={false}
                    onClick={() => select(trimmedQuery)}
                    className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-gray-700 hover:bg-blue-50"
                  >
                    Use{' '}
                    <span className="font-mono font-medium text-gray-900">{trimmedQuery}</span>
                  </button>
                </li>
              )}

              {!isLoading && !error && filtered.length === 0 && !showCustomOption && (
                <li className="px-3 py-2 text-sm text-gray-400">
                  {branches.length === 0
                    ? 'Enter a repository URL to load branches.'
                    : 'No matching branches.'}
                </li>
              )}
            </ul>
          </div>,
          document.body
        )}
    </div>
  )
}
