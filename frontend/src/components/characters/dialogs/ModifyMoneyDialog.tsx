import { useState } from 'react'
import { X, Loader2 } from 'lucide-react'

interface ModifyMoneyDialogProps {
  characterName: string
  currentMoney: number // copper
  onClose: () => void
  onSubmit: (copperAmount: number) => Promise<void>
}

function formatGold(copper: number) {
  const g = Math.floor(Math.abs(copper) / 10000)
  const s = Math.floor((Math.abs(copper) % 10000) / 100)
  const c = Math.abs(copper) % 100
  return `${g}g ${s}s ${c}c`
}

export default function ModifyMoneyDialog({ characterName, currentMoney, onClose, onSubmit }: ModifyMoneyDialogProps) {
  const [gold, setGold] = useState('0')
  const [silver, setSilver] = useState('0')
  const [copper, setCopper] = useState('0')
  const [operation, setOperation] = useState<'add' | 'remove'>('add')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [error, setError] = useState('')

  const totalCopper = (parseInt(gold || '0') * 10000) + (parseInt(silver || '0') * 100) + parseInt(copper || '0')
  const sign = operation === 'add' ? 1 : -1

  const handleSubmit = async () => {
    if (totalCopper <= 0) { setError('Amount must be greater than 0'); return }
    setIsSubmitting(true)
    try {
      await onSubmit(sign * totalCopper)
      onClose()
    } catch (err: any) {
      setError(err.response?.data?.error || 'Failed to modify money')
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-md">
        <div className="border-b border-gray-200 px-6 py-4 flex items-center justify-between">
          <h2 className="text-xl font-semibold text-gray-900">Modify Gold</h2>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600" disabled={isSubmitting}>
            <X className="w-5 h-5" />
          </button>
        </div>
        <div className="px-6 py-4 space-y-4">
          {error && <div className="bg-red-50 text-red-600 p-3 rounded-md text-sm">{error}</div>}
          <div className="bg-blue-50 border border-blue-200 p-3 rounded-md">
            <p className="text-sm text-blue-800"><strong>{characterName}</strong></p>
            <p className="text-xs text-blue-600 mt-1">Current gold: {formatGold(currentMoney)}</p>
          </div>
          <div className="flex gap-2">
            <button
              onClick={() => setOperation('add')}
              className={`flex-1 py-2 rounded-md text-sm font-medium border ${operation === 'add' ? 'bg-green-600 text-white border-green-600' : 'border-gray-300 text-gray-700 hover:bg-gray-50'}`}
            >+ Add Gold</button>
            <button
              onClick={() => setOperation('remove')}
              className={`flex-1 py-2 rounded-md text-sm font-medium border ${operation === 'remove' ? 'bg-red-600 text-white border-red-600' : 'border-gray-300 text-gray-700 hover:bg-gray-50'}`}
            >− Remove Gold</button>
          </div>
          <div className="grid grid-cols-3 gap-3">
            {[['Gold', gold, setGold], ['Silver', silver, setSilver], ['Copper', copper, setCopper]].map(([label, val, setter]) => (
              <div key={label as string}>
                <label className="block text-xs font-medium text-gray-500 mb-1">{label as string}</label>
                <input
                  type="number"
                  value={val as string}
                  onChange={(e) => (setter as (v: string) => void)(e.target.value)}
                  className="w-full px-2 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 text-center"
                  min="0"
                  disabled={isSubmitting}
                />
              </div>
            ))}
          </div>
          {totalCopper > 0 && (
            <p className="text-sm text-gray-600 text-center">
              {operation === 'add' ? 'Adding' : 'Removing'} <strong>{formatGold(totalCopper)}</strong>
            </p>
          )}
        </div>
        <div className="border-t border-gray-200 px-6 py-4 flex justify-end gap-3">
          <button type="button" onClick={onClose} className="px-4 py-2 border border-gray-300 rounded-md hover:bg-gray-50 text-sm font-medium text-gray-700" disabled={isSubmitting}>
            Cancel
          </button>
          <button type="button" onClick={handleSubmit} className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed flex items-center gap-2 text-sm font-medium" disabled={isSubmitting}>
            {isSubmitting ? <><Loader2 className="w-4 h-4 animate-spin" />Updating...</> : 'Apply'}
          </button>
        </div>
      </div>
    </div>
  )
}
