import { Eye, Loader2, X } from 'lucide-react'
import { usePatchConfigOverridesPreview } from '@/hooks/usePatches'
import type { PatchConfigOverrideDto } from '@/types/patch.types'

interface PatchConfigOverridesPreviewProps {
  stackId: string
  patchKey: string | null
  overrides: PatchConfigOverrideDto[]
  open: boolean
  onClose: () => void
}

function valuesEqual(current: string | null | undefined, next: string): boolean {
  if (current == null) {
    return false
  }
  return current.trim() === next.trim()
}

function currentValueLabel(entry: PatchConfigOverrideDto): string {
  if (entry.confFound === false) {
    return 'Config file not found'
  }
  if (entry.keyFound === false) {
    return '(not set — will be added)'
  }
  return entry.currentValue ?? '—'
}

export function PatchConfigOverridesPreviewButton({
  overrides,
  onOpen,
  disabled,
}: {
  overrides: PatchConfigOverrideDto[]
  onOpen: () => void
  disabled?: boolean
}) {
  return (
    <button
      type="button"
      onClick={onOpen}
      disabled={disabled}
      title={
        overrides.length > 0
          ? 'Preview config settings that will change on apply'
          : 'No config overrides defined yet'
      }
      className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-2.5 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
    >
      <Eye className="h-3.5 w-3.5" />
      Preview changes
      {overrides.length > 0 && (
        <span className="rounded-full bg-blue-100 px-1.5 py-0.5 text-[10px] font-semibold text-blue-700">
          {overrides.length}
        </span>
      )}
    </button>
  )
}

export default function PatchConfigOverridesPreview({
  stackId,
  patchKey,
  overrides,
  open,
  onClose,
}: PatchConfigOverridesPreviewProps) {
  const { data: previewOverrides, isLoading, isError, error } = usePatchConfigOverridesPreview(
    stackId,
    patchKey,
    open
  )

  if (!open) {
    return null
  }

  const rows = previewOverrides ?? overrides
  const changedCount = rows.filter(
    (entry) => entry.keyFound && !valuesEqual(entry.currentValue, entry.value)
  ).length
  const newKeyCount = rows.filter((entry) => entry.confFound !== false && entry.keyFound === false).length

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center overflow-auto bg-black/40 p-4">
      <div
        className="flex max-h-[90vh] w-max min-w-[min(100%,20rem)] max-w-[min(96vw,120rem)] flex-col rounded-lg bg-white shadow-xl"
        role="dialog"
        aria-modal="true"
        aria-labelledby="config-overrides-preview-title"
      >
        <div className="flex items-start justify-between gap-4 border-b border-gray-100 px-5 py-4">
          <div className="min-w-0">
            <h3 id="config-overrides-preview-title" className="text-lg font-semibold text-gray-900">
              Config overrides on apply
            </h3>
            <p className="mt-1 text-sm text-gray-600">
              Compares patch JSON overrides with the stack&apos;s live server{' '}
              <span className="font-mono text-xs">.conf</span> files.
            </p>
            {rows.length > 0 && !isLoading && (
              <p className="mt-2 text-xs text-gray-500">
                {changedCount} value{changedCount === 1 ? '' : 's'} will change
                {newKeyCount > 0 && (
                  <>
                    {' '}
                    · {newKeyCount} new key{newKeyCount === 1 ? '' : 's'} will be added
                  </>
                )}
              </p>
            )}
          </div>
          <button
            type="button"
            onClick={onClose}
            className="shrink-0 rounded-md p-1 text-gray-400 hover:bg-gray-100 hover:text-gray-600"
            aria-label="Close"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="min-h-0 flex-1 overflow-auto px-5 py-4">
          {overrides.length === 0 ? (
            <p className="text-sm text-gray-500">
              No config overrides yet. Upload JSON files under Config overrides (for example{' '}
              <span className="font-mono">worldserver.json</span>) with key/value pairs.
            </p>
          ) : isLoading ? (
            <div className="flex items-center gap-2 text-sm text-gray-600">
              <Loader2 className="h-4 w-4 animate-spin" />
              Loading live server config values…
            </div>
          ) : isError ? (
            <p className="text-sm text-red-600">
              Failed to load live config values:{' '}
              {error instanceof Error ? error.message : 'Unknown error'}
            </p>
          ) : (
            <div className="inline-block min-w-full rounded-md border border-gray-200">
              <table className="w-full text-sm">
                <thead className="bg-gray-50 text-left text-xs uppercase tracking-wide text-gray-500">
                  <tr>
                    <th className="whitespace-nowrap px-3 py-2 font-medium">Source</th>
                    <th className="whitespace-nowrap px-3 py-2 font-medium">Server config</th>
                    <th className="whitespace-nowrap px-3 py-2 font-medium">Key</th>
                    <th className="whitespace-nowrap px-3 py-2 font-medium">Current</th>
                    <th className="whitespace-nowrap px-3 py-2 font-medium">New</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {rows.map((entry) => {
                    const unchanged = entry.keyFound && valuesEqual(entry.currentValue, entry.value)
                    const willChange = entry.keyFound && !valuesEqual(entry.currentValue, entry.value)
                    const willAdd = entry.confFound !== false && entry.keyFound === false
                    const confMissing = entry.confFound === false

                    return (
                      <tr
                        key={`${entry.sourceJson}:${entry.key}:${entry.value}`}
                        className={
                          willChange
                            ? 'bg-amber-50/60'
                            : willAdd
                              ? 'bg-blue-50/40'
                              : unchanged
                                ? 'bg-gray-50/40'
                                : undefined
                        }
                      >
                        <td className="whitespace-nowrap px-3 py-2 font-mono text-xs text-gray-600">
                          {entry.sourceJson}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 font-mono text-xs text-gray-800">
                          {entry.targetConf}
                        </td>
                        <td className="whitespace-nowrap px-3 py-2 font-mono text-xs text-gray-900">
                          {entry.key}
                        </td>
                        <td
                          className={`whitespace-nowrap px-3 py-2 font-mono text-xs ${
                            confMissing
                              ? 'text-red-600'
                              : willAdd
                                ? 'italic text-gray-500'
                                : unchanged
                                  ? 'text-gray-600'
                                  : 'text-gray-800'
                          }`}
                        >
                          {currentValueLabel(entry)}
                        </td>
                        <td
                          className={`whitespace-nowrap px-3 py-2 font-mono text-xs ${
                            willChange ? 'font-medium text-amber-900' : 'text-blue-800'
                          }`}
                        >
                          {entry.value}
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </div>

        <div className="flex justify-end border-t border-gray-100 px-5 py-3">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  )
}
