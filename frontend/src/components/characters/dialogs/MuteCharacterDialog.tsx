import { useState } from 'react'
import { X, Loader2 } from 'lucide-react'

interface MuteCharacterDialogProps {
  characterName: string
  onClose: () => void
  onSubmit: (minutes: number, reason: string) => Promise<void>
}

export default function MuteCharacterDialog({ characterName, onClose, onSubmit }: MuteCharacterDialogProps) {
  const [minutes, setMinutes] = useState('60')
  const [reason, setReason] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [error, setError] = useState('')

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    const mins = parseInt(minutes)
    if (isNaN(mins) || mins < 1) { setError('Duration must be at least 1 minute'); return }
    if (!reason.trim()) { setError('Reason is required'); return }
    setIsSubmitting(true)
    try {
      await onSubmit(mins, reason)
      onClose()
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to mute character')
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-md">
        <div className="border-b border-gray-200 px-6 py-4 flex items-center justify-between">
          <h2 className="text-xl font-semibold text-gray-900">Mute Character</h2>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600" disabled={isSubmitting}>
            <X className="w-5 h-5" />
          </button>
        </div>
        <form onSubmit={handleSubmit} className="px-6 py-4 space-y-4">
          {error && <div className="bg-red-50 text-red-600 p-3 rounded-md text-sm">{error}</div>}
          <div className="bg-blue-50 border border-blue-200 p-3 rounded-md">
            <p className="text-sm text-blue-800">Muting chat for: <strong>{characterName}</strong></p>
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Duration (minutes)</label>
            <input
              type="number"
              value={minutes}
              onChange={(e) => setMinutes(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              min="1"
              disabled={isSubmitting}
              autoFocus
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Reason</label>
            <textarea
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="Enter mute reason..."
              rows={2}
              disabled={isSubmitting}
            />
          </div>
        </form>
        <div className="border-t border-gray-200 px-6 py-4 flex justify-end gap-3">
          <button type="button" onClick={onClose} className="px-4 py-2 border border-gray-300 rounded-md hover:bg-gray-50 text-sm font-medium text-gray-700" disabled={isSubmitting}>
            Cancel
          </button>
          <button type="button" onClick={handleSubmit} className="px-4 py-2 bg-orange-600 text-white rounded-md hover:bg-orange-700 disabled:bg-gray-300 disabled:cursor-not-allowed flex items-center gap-2 text-sm font-medium" disabled={isSubmitting}>
            {isSubmitting ? <><Loader2 className="w-4 h-4 animate-spin" />Muting...</> : 'Mute Character'}
          </button>
        </div>
      </div>
    </div>
  )
}
