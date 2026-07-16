import { Loader2, X, RotateCcw, CheckCircle2, AlertTriangle } from 'lucide-react'
import {
  useProgressionIgnoredFiles,
  useRepromptProgressionIgnoredFile,
} from '@/hooks/usePatches'
import { useState } from 'react'

interface IgnoredFilesDialogProps {
  stackId: string
  onClose: () => void
}

export default function IgnoredFilesDialog({ stackId, onClose }: IgnoredFilesDialogProps) {
  const { data: ignoredFiles, isLoading, error } = useProgressionIgnoredFiles(stackId)
  const repromptMutation = useRepromptProgressionIgnoredFile(stackId)
  const [repromptedSources, setRepromptedSources] = useState<Set<string>>(new Set())
  const [repromptError, setRepromptError] = useState<string | null>(null)

  const handleReprompt = async (source: string) => {
    setRepromptError(null)
    try {
      await repromptMutation.mutateAsync(source)
      setRepromptedSources((prev) => new Set(prev).add(source))
    } catch (err) {
      setRepromptError(err instanceof Error ? err.message : 'Failed to re-prompt file.')
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="bg-white rounded-lg shadow-xl max-w-lg w-full max-h-[80vh] flex flex-col">
        <div className="flex items-center justify-between border-b border-gray-100 px-5 py-4">
          <h3 className="text-lg font-semibold text-gray-900">Ignored Optional Files</h3>
          <button
            type="button"
            onClick={onClose}
            className="rounded-md p-1 text-gray-400 hover:bg-gray-100 hover:text-gray-600"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto p-5 space-y-3">
          <p className="text-sm text-gray-600">
            These optional files from mod-individual-progression were previously declined.
            Click <strong>Re-prompt</strong> to include a file and copy it to your patch directory
            without triggering a full sync.
          </p>

          {repromptError && (
            <div className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700 flex items-center gap-2">
              <AlertTriangle className="h-4 w-4 shrink-0" />
              {repromptError}
            </div>
          )}

          {isLoading ? (
            <div className="flex items-center justify-center gap-2 py-8 text-sm text-gray-500">
              <Loader2 className="h-5 w-5 animate-spin" /> Loading…
            </div>
          ) : error ? (
            <div className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
              Failed to load ignored files.
            </div>
          ) : !ignoredFiles || ignoredFiles.length === 0 ? (
            <div className="flex flex-col items-center gap-2 py-8 text-sm text-gray-400">
              <CheckCircle2 className="h-8 w-8" />
              <p>No ignored files — all optional files have been accepted.</p>
            </div>
          ) : (
            <ul className="space-y-2">
              {ignoredFiles.map((file) => {
                const wasReprompted = repromptedSources.has(file.source)
                return (
                  <li
                    key={file.source}
                    className={`rounded-md border px-3 py-2.5 ${
                      wasReprompted
                        ? 'border-green-200 bg-green-50'
                        : 'border-gray-200 bg-white'
                    }`}
                  >
                    <div className="flex items-start justify-between gap-2">
                      <div className="min-w-0 flex-1">
                        <p className="font-mono text-sm text-gray-900 truncate">
                          {file.fileName}
                        </p>
                        <p className="mt-0.5 text-xs text-gray-500 truncate">
                          {file.source} → {file.destination}
                        </p>
                        <p className="mt-0.5 text-xs text-gray-400">
                          Ignored on {new Date(file.decidedAt).toLocaleString()}
                        </p>
                      </div>
                      {wasReprompted ? (
                        <span className="inline-flex shrink-0 items-center gap-1 rounded-full bg-green-100 px-2.5 py-1 text-xs font-medium text-green-700">
                          <CheckCircle2 className="h-3.5 w-3.5" /> Included
                        </span>
                      ) : (
                        <button
                          type="button"
                          onClick={() => handleReprompt(file.source)}
                          disabled={repromptMutation.isPending}
                          className="inline-flex shrink-0 items-center gap-1.5 rounded-md border border-indigo-300 bg-white px-2.5 py-1.5 text-xs font-medium text-indigo-700 hover:bg-indigo-50 disabled:opacity-50"
                        >
                          {repromptMutation.isPending ? (
                            <Loader2 className="h-3.5 w-3.5 animate-spin" />
                          ) : (
                            <RotateCcw className="h-3.5 w-3.5" />
                          )}
                          Re-prompt
                        </button>
                      )}
                    </div>
                  </li>
                )
              })}
            </ul>
          )}
        </div>

        <div className="border-t border-gray-100 px-5 py-3 flex justify-end">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  )
}
