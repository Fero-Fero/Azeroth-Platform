import { useState, useMemo } from 'react'
import { Search, Loader2, Users, RefreshCw } from 'lucide-react'
import { useAllCharacters } from '@/hooks/useCharacters'
import type { CharacterDto } from '@/types/account.types'
import { RACES, CLASSES } from '@/types/character.types'
import CharacterDetailDialog from './CharacterDetailDialog'

const ALLIANCE_RACES = new Set([1, 3, 4, 7, 11])
const HORDE_RACES = new Set([2, 5, 6, 8, 10])

interface CharactersTabProps {
  stackId: string
  /** If provided, filters the list to only this account's characters */
  accountId?: number
  /** Optional className to control container sizing */
  className?: string
}

export default function CharactersTab({ stackId, accountId, className }: CharactersTabProps) {
  const { data: characters, isLoading, error, refetch } = useAllCharacters(stackId)
  const [search, setSearch] = useState('')
  const [factionFilter, setFactionFilter] = useState<'all' | 'alliance' | 'horde'>('all')
  const [onlineOnly, setOnlineOnly] = useState(false)
  const [selectedCharacter, setSelectedCharacter] = useState<CharacterDto | null>(null)

  const filtered = useMemo(() => {
    if (!characters) return []
    return characters
      .filter(c => !accountId || c.account === accountId)
      .filter(c => !onlineOnly || c.online)
      .filter(c => {
        if (factionFilter === 'alliance') return ALLIANCE_RACES.has(c.race)
        if (factionFilter === 'horde') return HORDE_RACES.has(c.race)
        return true
      })
      .filter(c =>
        !search ||
        c.name.toLowerCase().includes(search.toLowerCase()) ||
        (c.accountUsername || '').toLowerCase().includes(search.toLowerCase()) ||
        (c.guild || '').toLowerCase().includes(search.toLowerCase())
      )
  }, [characters, search, factionFilter, onlineOnly, accountId])

  const selectedGuid = selectedCharacter?.guid ?? null

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16">
        <Loader2 className="w-6 h-6 animate-spin text-gray-400 mr-2" />
        <span className="text-gray-500">Loading characters...</span>
      </div>
    )
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
        <p className="text-red-600">Failed to load characters. Is the stack running?</p>
      </div>
    )
  }

  const onlineCount = (characters ?? []).filter(c => c.online).length

  return (
    <>
      <div className={className}>
        {/* Toolbar */}
        <div className="mb-3 space-y-2">
          <div className="flex items-center gap-2">
            <div className="relative flex-1">
              <Search className="absolute left-2.5 top-2.5 w-4 h-4 text-gray-400" />
              <input
                type="text"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Search characters..."
                className="w-full pl-8 pr-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
            </div>
            <button onClick={() => refetch()} className="p-2 text-gray-400 hover:text-gray-600 border border-gray-300 rounded-md" title="Refresh">
              <RefreshCw className="w-4 h-4" />
            </button>
          </div>
          <div className="flex items-center gap-2">
            <select
              value={factionFilter}
              onChange={(e) => setFactionFilter(e.target.value as any)}
              className="flex-1 px-2 py-1.5 border border-gray-300 rounded-md text-xs focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              <option value="all">All Factions</option>
              <option value="alliance">Alliance</option>
              <option value="horde">Horde</option>
            </select>
            <label className="flex items-center gap-1.5 text-xs text-gray-600 cursor-pointer whitespace-nowrap">
              <input
                type="checkbox"
                checked={onlineOnly}
                onChange={(e) => setOnlineOnly(e.target.checked)}
                className="rounded"
              />
              Online only
            </label>
          </div>
          <div className="flex items-center gap-1 text-xs text-gray-500">
            <Users className="w-3.5 h-3.5" />
            <span>{filtered.length} characters</span>
            {onlineCount > 0 && <span className="text-green-600">• {onlineCount} online</span>}
          </div>
        </div>

        {/* Character grid */}
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-2">
          {filtered.length === 0 ? (
            <div className="col-span-full text-center py-8 text-gray-400 text-sm">No characters found</div>
          ) : (
            filtered.map(c => (
              <CharacterListItem
                key={c.guid}
                character={c}
                selected={c.guid === selectedGuid}
                onClick={() => setSelectedCharacter(c)}
              />
            ))
          )}
        </div>
      </div>

      {selectedCharacter && (
        <CharacterDetailDialog
          character={selectedCharacter}
          stackId={stackId}
          onClose={() => setSelectedCharacter(null)}
        />
      )}
    </>
  )
}

function CharacterListItem({
  character,
  selected,
  onClick,
}: {
  character: CharacterDto
  selected: boolean
  onClick: () => void
}) {
  const isAlliance = ALLIANCE_RACES.has(character.race)
  const factionColor = isAlliance ? 'text-blue-400' : 'text-red-400'

  return (
    <button
      onClick={onClick}
      className={`w-full text-left px-3 py-2 rounded-lg border transition-colors ${
        selected
          ? 'bg-blue-50 border-blue-300'
          : 'bg-white border-gray-200 hover:border-blue-200 hover:bg-blue-50/30'
      }`}
    >
      <div className="flex items-center justify-between">
        <span className={`font-medium text-sm ${selected ? 'text-blue-700' : 'text-gray-800'}`}>
          {character.name}
        </span>
        <div className="flex items-center gap-1">
          {character.online && <span className="w-1.5 h-1.5 bg-green-500 rounded-full" />}
          <span className={`text-xs ${factionColor}`}>{isAlliance ? '⬡' : '⬢'}</span>
        </div>
      </div>
      <div className="flex items-center gap-1 mt-0.5">
        <span className="text-xs text-gray-500">
          Lv {character.level} {RACES[character.race] || '?'} {CLASSES[character.class] || '?'}
        </span>
      </div>
      {character.guild && (
        <p className="text-xs text-blue-500 truncate mt-0.5">⚔ {character.guild}</p>
      )}
    </button>
  )
}
