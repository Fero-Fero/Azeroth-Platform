import { Eye, X } from 'lucide-react'
import type { PatchConfigOverrideDto } from '@/types/patch.types'

interface PatchConfigOverridesPreviewProps {
  overrides: PatchConfigOverrideDto[]
  open: boolean
  onClose: () => void
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
  overrides,
  open,
  onClose,
}: PatchConfigOverridesPreviewProps) {
  if (!open) {
    return null
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div
        className="flex max-h-[85vh] w-full max-w-3xl flex-col rounded-lg bg-white shadow-xl"
        role="dialog"
        aria-modal="true"
        aria-labelledby="config-overrides-preview-title"
      >
        <div className="flex items-start justify-between border-b border-gray-100 px-5 py-4">
          <div>
            <h3 id="config-overrides-preview-title" className="text-lg font-semibold text-gray-900">
              Config overrides on apply
            </h3>
            <p className="mt-1 text-sm text-gray-600">
              These settings will be written to the matching server{' '}
              <span className="font-mono text-xs">.conf</span> files when this patch is applied.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-md p-1 text-gray-400 hover:bg-gray-100 hover:text-gray-600"
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
          ) : (
            <div className="overflow-x-auto rounded-md border border-gray-200">
              <table className="min-w-full text-sm">
                <thead className="bg-gray-50 text-left text-xs uppercase tracking-wide text-gray-500">
                  <tr>
                    <th className="px-3 py-2 font-medium">Source</th>
                    <th className="px-3 py-2 font-medium">Server config</th>
                    <th className="px-3 py-2 font-medium">Key</th>
                    <th className="px-3 py-2 font-medium">Value</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {overrides.map((entry) => (
                    <tr key={`${entry.sourceJson}:${entry.key}:${entry.value}`}>
                      <td className="px-3 py-2 font-mono text-xs text-gray-600">{entry.sourceJson}</td>
                      <td className="px-3 py-2 font-mono text-xs text-gray-800">{entry.targetConf}</td>
                      <td className="px-3 py-2 font-mono text-xs text-gray-900">{entry.key}</td>
                      <td className="px-3 py-2 font-mono text-xs text-blue-800">{entry.value}</td>
                    </tr>
                  ))}
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
