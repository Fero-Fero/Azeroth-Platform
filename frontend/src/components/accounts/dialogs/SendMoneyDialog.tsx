import { useState } from 'react'
import { X, Loader2 } from 'lucide-react'

interface SendMoneyDialogProps {
  characterName: string
  onClose: () => void
  onSubmit: (copperAmount: number, subject: string, body: string) => Promise<void>
}

export default function SendMoneyDialog({ characterName, onClose, onSubmit }: SendMoneyDialogProps) {
  const [gold, setGold] = useState('0')
  const [silver, setSilver] = useState('0')
  const [copper, setCopper] = useState('0')
  const [subject, setSubject] = useState('')
  const [body, setBody] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [error, setError] = useState('')

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')

    const goldNum = parseInt(gold) || 0
    const silverNum = parseInt(silver) || 0
    const copperNum = parseInt(copper) || 0

    // Convert to copper (1 gold = 10000 copper, 1 silver = 100 copper)
    const totalCopper = goldNum * 10000 + silverNum * 100 + copperNum

    if (totalCopper <= 0) {
      setError('Amount must be greater than 0')
      return
    }

    if (!subject.trim() || !body.trim()) {
      setError('Subject and message are required')
      return
    }

    setIsSubmitting(true)
    try {
      await onSubmit(totalCopper, subject, body)
      onClose()
    } catch (err: any) {
      setError(err.response?.data?.message || 'Failed to send money')
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-md">
        <div className="border-b border-gray-200 px-6 py-4 flex items-center justify-between">
          <h2 className="text-xl font-semibold text-gray-900">Send Gold</h2>
          <button
            onClick={onClose}
            className="text-gray-400 hover:text-gray-600"
            disabled={isSubmitting}
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="px-6 py-4 space-y-4">
          {error && (
            <div className="bg-red-50 text-red-600 p-3 rounded-md text-sm">
              {error}
            </div>
          )}

          <div className="bg-blue-50 border border-blue-200 p-3 rounded-md">
            <p className="text-sm text-blue-800">
              Sending gold via mail to: <strong>{characterName}</strong>
            </p>
          </div>

          <div className="grid grid-cols-3 gap-3">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Gold
              </label>
              <input
                type="number"
                value={gold}
                onChange={(e) => setGold(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                placeholder="0"
                disabled={isSubmitting}
                autoFocus
                min="0"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Silver
              </label>
              <input
                type="number"
                value={silver}
                onChange={(e) => setSilver(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                placeholder="0"
                disabled={isSubmitting}
                min="0"
                max="99"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Copper
              </label>
              <input
                type="number"
                value={copper}
                onChange={(e) => setCopper(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
                placeholder="0"
                disabled={isSubmitting}
                min="0"
                max="99"
              />
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Subject
            </label>
            <input
              type="text"
              value={subject}
              onChange={(e) => setSubject(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="Enter mail subject..."
              disabled={isSubmitting}
              maxLength={128}
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Message
            </label>
            <textarea
              value={body}
              onChange={(e) => setBody(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="Enter your message..."
              rows={3}
              disabled={isSubmitting}
              maxLength={500}
            />
          </div>
        </form>

        <div className="border-t border-gray-200 px-6 py-4 flex justify-end gap-3">
          <button
            type="button"
            onClick={onClose}
            className="px-4 py-2 border border-gray-300 rounded-md hover:bg-gray-50 text-sm font-medium text-gray-700"
            disabled={isSubmitting}
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={handleSubmit}
            className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed flex items-center gap-2 text-sm font-medium"
            disabled={isSubmitting}
          >
            {isSubmitting ? (
              <>
                <Loader2 className="w-4 h-4 animate-spin" />
                Sending...
              </>
            ) : (
              'Send Gold'
            )}
          </button>
        </div>
      </div>
    </div>
  )
}
