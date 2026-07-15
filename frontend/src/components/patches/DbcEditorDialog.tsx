import { useEffect, useState } from 'react'
import { Loader2, X, Save } from 'lucide-react'
import { patchApi } from '@/services/api'
import { useSaveDbcFile } from '@/hooks/usePatches'

interface DbcEditorDialogProps {
  stackId: string
  patchKey: string
  fileName: string
  onClose: () => void
}

export default function DbcEditorDialog({ stackId, patchKey, fileName, onClose }: DbcEditorDialogProps) {
  const [content, setContent] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const saveMutation = useSaveDbcFile(stackId)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    patchApi
      .readDbc(stackId, patchKey, fileName)
      .then((res) => {
        if (!cancelled) setContent(res.data.content)
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : 'Failed to load file')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [stackId, patchKey, fileName])

  const handleSave = async () => {
    setError(null)
    try {
      await saveMutation.mutateAsync({ patchKey, fileName, content })
      onClose()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save file')
    }
  }

  return (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-4xl max-h-[90vh] flex flex-col">
        <div className="flex items-center justify-between px-5 py-3 border-b border-gray-200">
          <h3 className="font-semibold text-gray-900">
            Edit <span className="font-mono">{fileName}</span>
          </h3>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600">
            <X className="w-5 h-5" />
          </button>
        </div>

        <div className="p-4 flex-1 overflow-hidden flex flex-col">
          {loading ? (
            <div className="flex items-center justify-center py-16">
              <Loader2 className="w-6 h-6 text-blue-600 animate-spin" />
            </div>
          ) : (
            <>
              <p className="text-xs text-gray-500 mb-2">
                CSV mirroring the DBC (header row, comma-delimited, quoted fields). Saved as-is; the
                server normalizes line endings before importing.
              </p>
              <textarea
                value={content}
                onChange={(e) => setContent(e.target.value)}
                spellCheck={false}
                className="flex-1 w-full min-h-[50vh] font-mono text-xs border border-gray-300 rounded-md p-3 focus:outline-none focus:ring-2 focus:ring-blue-500 resize-none"
              />
            </>
          )}
          {error && <p className="text-sm text-red-600 mt-2">{error}</p>}
        </div>

        <div className="flex justify-end gap-2 px-5 py-3 border-t border-gray-200">
          <button
            onClick={onClose}
            className="px-4 py-2 rounded-md border border-gray-300 text-gray-700 hover:bg-gray-50"
          >
            Cancel
          </button>
          <button
            onClick={handleSave}
            disabled={loading || saveMutation.isPending}
            className="px-4 py-2 rounded-md bg-blue-600 text-white hover:bg-blue-700 flex items-center gap-2 disabled:opacity-50"
          >
            {saveMutation.isPending ? (
              <Loader2 className="w-4 h-4 animate-spin" />
            ) : (
              <Save className="w-4 h-4" />
            )}
            Save
          </button>
        </div>
      </div>
    </div>
  )
}
