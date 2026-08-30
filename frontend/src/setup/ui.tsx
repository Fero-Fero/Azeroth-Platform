import type { ReactNode } from 'react'
import { Loader2 } from 'lucide-react'

export function setupActionButton(
  label: string,
  onClick: () => void,
  options: {
    disabled?: boolean
    pending?: boolean
    icon?: ReactNode
    tone?: 'red' | 'amber' | 'blue' | 'green'
  } = {},
) {
  const toneClass =
    options.tone === 'red'
      ? 'bg-red-600 hover:bg-red-700'
      : options.tone === 'blue'
        ? 'bg-blue-600 hover:bg-blue-700'
        : options.tone === 'green'
          ? 'bg-green-600 hover:bg-green-700'
          : 'bg-amber-600 hover:bg-amber-700'

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={options.disabled || options.pending}
      className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium text-white disabled:cursor-not-allowed disabled:opacity-50 ${toneClass}`}
    >
      {options.pending ? <Loader2 className="h-4 w-4 animate-spin" /> : options.icon}
      {label}
    </button>
  )
}

export function setupSkipButton(onClick: () => void) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50"
    >
      Skip for now
    </button>
  )
}
