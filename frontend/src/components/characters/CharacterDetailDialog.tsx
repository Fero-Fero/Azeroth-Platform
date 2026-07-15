import { X } from 'lucide-react'
import type { CharacterDto } from '@/types/account.types'
import CharacterDetailPanel from './CharacterDetailPanel'

interface CharacterDetailDialogProps {
  character: CharacterDto
  stackId: string
  onClose: () => void
}

export default function CharacterDetailDialog({ character, stackId, onClose }: CharacterDetailDialogProps) {
  return (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-3xl max-h-[90vh] flex flex-col">
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-200 shrink-0">
          <h2 className="text-lg font-semibold">{character.name}</h2>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600">
            <X className="w-5 h-5" />
          </button>
        </div>
        <div className="overflow-y-auto flex-1 p-5">
          <CharacterDetailPanel character={character} stackId={stackId} />
        </div>
      </div>
    </div>
  )
}
