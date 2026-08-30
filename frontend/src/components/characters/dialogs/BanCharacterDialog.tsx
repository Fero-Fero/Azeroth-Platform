import { useState } from 'react'
import { X, Loader2 } from 'lucide-react'

interface BanCharacterDialogProps {
  characterName: string
  onClose: () => void
  onSubmit: (duration: string, reason: string) => Promise<void>
}

export default function BanCharacterDialog({ characterName, onClose, onSubmit }: BanCharacterDialogProps) {
  const [duration, setDuration] = useState('1d')
  const [reason, setReason] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [error, setError] = useState('')

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!reason.trim()) { setError('Ban reason is required'); return }
    setIsSubmitting(true)
    try {
      await onSubmit(duration, reason)
      onClose()
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to ban character')
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-md">
        <div className="border-b border-gray-200 px-6 py-4 flex items-center justify-between">
          <h2 className="text-xl font-semibold text-gray-900">Ban Character</h2>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600" disabled={isSubmitting}>
            <X className="w-5 h-5" />
          </button>
        </div>
        <form onSubmit={handleSubmit} className="px-6 py-4 space-y-4">
          {error && <div className="bg-red-50 text-red-600 p-3 rounded-md text-sm">{error}</div>}
          <div className="bg-yellow-50 border border-yellow-200 p-3 rounded-md">
            <p className="text-sm text-yellow-800">Banning character: <strong>{characterName}</strong></p>
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Duration</label>
            <select
              value={duration}
              onChange={(e) => setDuration(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              disabled={isSubmitting}
            >
              <option value="30m">30 minutes</option>
              <option value="1h">1 hour</option>
              <option value="12h">12 hours</option>
              <option value="1d">1 day</option>
              <option value="7d">7 days</option>
              <option value="30d">30 days</option>
              <option value="-1">Permanent</option>
            </select>
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Reason</label>
            <textarea
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="Enter ban reason..."
              rows={3}
              disabled={isSubmitting}
              autoFocus
            />
          </div>
        </form>
        <div className="border-t border-gray-200 px-6 py-4 flex justify-end gap-3">
          <button type="button" onClick={onClose} className="px-4 py-2 border border-gray-300 rounded-md hover:bg-gray-50 text-sm font-medium text-gray-700" disabled={isSubmitting}>
            Cancel
          </button>
          <button type="button" onClick={handleSubmit} className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 disabled:bg-gray-300 disabled:cursor-not-allowed flex items-center gap-2 text-sm font-medium" disabled={isSubmitting}>
            {isSubmitting ? <><Loader2 className="w-4 h-4 animate-spin" />Banning...</> : 'Ban Character'}
          </button>
        </div>
      </div>
    </div>
  )
}
