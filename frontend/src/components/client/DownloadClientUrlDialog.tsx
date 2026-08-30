import { useState, type FormEvent } from 'react'
import { Download, Loader2 } from 'lucide-react'

interface DownloadClientUrlDialogProps {
  onClose: () => void
  onSubmit: (url: string) => Promise<void>
}

export default function DownloadClientUrlDialog({ onClose, onSubmit }: DownloadClientUrlDialogProps) {
  const [url, setUrl] = useState('')
  const [error, setError] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError('')
    const trimmed = url.trim()
    if (!trimmed) {
      setError('Paste a direct download URL.')
      return
    }

    try {
      const parsed = new URL(trimmed)
      if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
        setError('The URL must start with http:// or https://')
        return
      }
    } catch {
      setError('That does not look like a valid URL.')
      return
    }

    setIsSubmitting(true)
    try {
      await onSubmit(trimmed)
      onClose()
    } catch (err: unknown) {
      const data = err && typeof err === 'object' && 'response' in err
        ? (err as { response?: { data?: { error?: string; message?: string } } }).response?.data
        : undefined
      setError(data?.error || data?.message || 'Failed to start the download.')
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-lg rounded-lg bg-white shadow-xl">
        <div className="border-b border-gray-200 px-6 py-4">
          <h2 className="text-xl font-semibold text-gray-900">Download client from URL</h2>
          <p className="mt-1 text-sm text-gray-500">
            Paste a direct link to a 3.3.5a client archive (zip, rar, 7z, or tar). Nested folders are
            searched until <span className="font-mono">Wow.exe</span> and{' '}
            <span className="font-mono">Data/*.MPQ</span> are found.
          </p>
        </div>

        <form onSubmit={(event) => void handleSubmit(event)} className="space-y-4 px-6 py-4">
          {error ? <div className="rounded-md bg-red-50 p-3 text-sm text-red-600">{error}</div> : null}

          <div>
            <label htmlFor="client-download-url" className="mb-1 block text-sm font-medium text-gray-700">
              Direct download URL
            </label>
            <input
              id="client-download-url"
              type="url"
              value={url}
              onChange={(event) => setUrl(event.target.value)}
              className="w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="https://example.com/wow-335a.zip"
              disabled={isSubmitting}
              autoFocus
            />
          </div>

          <div className="flex justify-end gap-2 pt-2">
            <button
              type="button"
              onClick={onClose}
              disabled={isSubmitting}
              className="rounded-md border bg-white px-4 py-2 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={isSubmitting || url.trim().length === 0}
              className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
            >
              {isSubmitting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
              Download
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
