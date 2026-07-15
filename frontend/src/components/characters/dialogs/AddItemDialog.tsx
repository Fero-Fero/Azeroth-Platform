import { useState } from 'react'
import { X, Loader2 } from 'lucide-react'

interface AddItemDialogProps {
  characterName: string
  onClose: () => void
  onSubmit: (itemId: number, count: number) => Promise<void>
}

export default function AddItemDialog({ characterName, onClose, onSubmit }: AddItemDialogProps) {
  const [itemId, setItemId] = useState('')
  const [count, setCount] = useState('1')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [error, setError] = useState('')

  const handleSubmit = async () => {
    const id = parseInt(itemId)
    const qty = parseInt(count)
    if (isNaN(id) || id <= 0) { setError('Enter a valid item entry ID'); return }
    if (isNaN(qty) || qty < 1) { setError('Count must be at least 1'); return }
    setIsSubmitting(true)
    try {
      await onSubmit(id, qty)
      onClose()
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to add item')
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-md">
        <div className="border-b border-gray-200 px-6 py-4 flex items-center justify-between">
          <h2 className="text-xl font-semibold text-gray-900">Add Item to Inventory</h2>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600" disabled={isSubmitting}>
            <X className="w-5 h-5" />
          </button>
        </div>
        <div className="px-6 py-4 space-y-4">
          {error && <div className="bg-red-50 text-red-600 p-3 rounded-md text-sm">{error}</div>}
          <div className="bg-blue-50 border border-blue-200 p-3 rounded-md">
            <p className="text-sm text-blue-800">Adding item to: <strong>{characterName}</strong></p>
            <p className="text-xs text-blue-600 mt-1">You can find item IDs on wowhead.com (WotLK version)</p>
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Item Entry ID</label>
            <input
              type="number"
              value={itemId}
              onChange={(e) => setItemId(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="e.g. 49623 (Shadowmourne)"
              disabled={isSubmitting}
              autoFocus
              min="1"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Count</label>
            <input
              type="number"
              value={count}
              onChange={(e) => setCount(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              min="1"
              max="65535"
              disabled={isSubmitting}
            />
          </div>
        </div>
        <div className="border-t border-gray-200 px-6 py-4 flex justify-end gap-3">
          <button type="button" onClick={onClose} className="px-4 py-2 border border-gray-300 rounded-md hover:bg-gray-50 text-sm font-medium text-gray-700" disabled={isSubmitting}>
            Cancel
          </button>
          <button type="button" onClick={handleSubmit} className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed flex items-center gap-2 text-sm font-medium" disabled={isSubmitting}>
            {isSubmitting ? <><Loader2 className="w-4 h-4 animate-spin" />Adding...</> : 'Add Item'}
          </button>
        </div>
      </div>
    </div>
  )
}
