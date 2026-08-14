import { useState } from 'react'
import { Info, X } from 'lucide-react'

const dismissKey = (stackId: string) => `azp_ip_sync_hint_dismissed_${stackId}`

export function isIpSyncHintDismissed(stackId: string): boolean {
  try {
    return localStorage.getItem(dismissKey(stackId)) === '1'
  } catch {
    return false
  }
}

interface IndividualProgressionSyncHintProps {
  stackId: string
  /** When set, renders the Patches tab label as a link. */
  patchesHref?: string
  className?: string
  onDismiss?: () => void
}

/**
 * Dismissible hint shown when mod-individual-progression is installed.
 */
export default function IndividualProgressionSyncHint({
  stackId,
  patchesHref,
  className = '',
  onDismiss,
}: IndividualProgressionSyncHintProps) {
  const [dismissed, setDismissed] = useState(() => isIpSyncHintDismissed(stackId))

  if (dismissed) {
    return null
  }

  const dismiss = () => {
    try {
      localStorage.setItem(dismissKey(stackId), '1')
    } catch {
      /* ignore storage errors */
    }
    setDismissed(true)
    onDismiss?.()
  }

  return (
    <div
      className={`rounded-lg border border-violet-200 bg-violet-50 px-5 py-4 ${className}`.trim()}
    >
      <div className="flex items-start gap-3">
        <Info className="mt-0.5 h-5 w-5 shrink-0 text-violet-600" aria-hidden="true" />
        <div className="min-w-0 flex-1">
          <p className="text-sm text-violet-900">
            Open the{' '}
            {patchesHref ? (
              <a
                href={patchesHref}
                className="font-medium text-violet-700 underline hover:text-violet-900"
              >
                Patches tab
              </a>
            ) : (
              <strong>Patches tab</strong>
            )}{' '}
            to <strong>prepare server-wide progression</strong> (bootstrap), run{' '}
            <strong>Sync with mod-individual-progression</strong>, and apply patches in order.
          </p>
        </div>
        <button
          type="button"
          onClick={dismiss}
          className="shrink-0 rounded p-1 text-violet-500 hover:bg-violet-100 hover:text-violet-800"
          aria-label="Dismiss"
        >
          <X className="h-4 w-4" />
        </button>
      </div>
    </div>
  )
}
