import { useState } from 'react'
import {
  Loader2, RefreshCw, Sword, Shield, ShoppingBag, Building2,
  Wrench, Star, Coins, Ban, VolumeX, Volume2, Heart,
  MessageSquare, Package, TrendingUp, Edit, Palette, UserX, Plus,
} from 'lucide-react'
import type { CharacterDto } from '@/types/account.types'
import { EQUIPMENT_SLOT_LABELS, QUALITY_COLORS, RACES, CLASSES } from '@/types/character.types'
import { useCharacterInventory } from '@/hooks/useCharacters'
import {
  useBanCharacter, useUnbanCharacter, useMuteCharacter, useUnmuteCharacter,
  useReviveCharacter, useModifyMoney, useAddItem,
} from '@/hooks/useCharacters'
import {
  useSendMessage, useSendItems, useSendMoney,
  useKickPlayer, useRenameCharacter, useCustomizeCharacter, useSetLevel,
} from '@/hooks/useAccounts'
import BanCharacterDialog from './dialogs/BanCharacterDialog'
import MuteCharacterDialog from './dialogs/MuteCharacterDialog'
import ModifyMoneyDialog from './dialogs/ModifyMoneyDialog'
import AddItemDialog from './dialogs/AddItemDialog'
import SendMessageDialog from '@/components/accounts/dialogs/SendMessageDialog'
import SendItemsDialog from '@/components/accounts/dialogs/SendItemsDialog'
import SendMoneyDialog from '@/components/accounts/dialogs/SendMoneyDialog'
import SetLevelDialog from '@/components/accounts/dialogs/SetLevelDialog'

const GENDERS: Record<number, string> = { 0: 'Male', 1: 'Female' }

function formatGold(copper: number) {
  const g = Math.floor(copper / 10000)
  const s = Math.floor((copper % 10000) / 100)
  const c = copper % 100
  return (
    <span className="font-mono text-sm">
      {g > 0 && <span className="text-yellow-600">{g}g </span>}
      {(s > 0 || g > 0) && <span className="text-gray-400">{s}s </span>}
      <span className="text-amber-700">{c}c</span>
    </span>
  )
}

function formatPlaytime(seconds: number) {
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const mins = Math.floor((seconds % 3600) / 60)
  return `${days}d ${hours}h ${mins}m`
}

type SubTab = 'info' | 'equipment' | 'inventory' | 'bank' | 'actions'

interface CharacterDetailPanelProps {
  character: CharacterDto
  stackId: string
}

export default function CharacterDetailPanel({ character, stackId }: CharacterDetailPanelProps) {
  const [subTab, setSubTab] = useState<SubTab>('info')
  const [activeDialog, setActiveDialog] = useState<string | null>(null)
  const [actionMessage, setActionMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)

  const { data: inventory, isLoading: inventoryLoading, refetch: refetchInventory } = useCharacterInventory(
    stackId,
    subTab === 'equipment' || subTab === 'inventory' || subTab === 'bank' ? character.guid : null
  )

  // Mutations
  const banMutation = useBanCharacter(stackId)
  const unbanMutation = useUnbanCharacter(stackId)
  const muteMutation = useMuteCharacter(stackId)
  const unmuteMutation = useUnmuteCharacter(stackId)
  const reviveMutation = useReviveCharacter(stackId)
  const modifyMoneyMutation = useModifyMoney(stackId)
  const addItemMutation = useAddItem(stackId)
  const sendMessageMutation = useSendMessage(stackId)
  const sendItemsMutation = useSendItems(stackId)
  const sendMoneyMutation = useSendMoney(stackId)
  const kickMutation = useKickPlayer(stackId)
  const renameMutation = useRenameCharacter(stackId)
  const customizeMutation = useCustomizeCharacter(stackId)
  const setLevelMutation = useSetLevel(stackId)

  const showSuccess = (msg: string) => { setActionMessage({ type: 'success', text: msg }); setTimeout(() => setActionMessage(null), 4000) }
  const showError = (msg: string) => { setActionMessage({ type: 'error', text: msg }); setTimeout(() => setActionMessage(null), 6000) }

  const runAction = async (fn: () => Promise<any>, successMsg: string) => {
    try {
      await fn()
      showSuccess(successMsg)
    } catch (err: any) {
      showError(err.response?.data?.error || 'Action failed')
    }
  }

  const tabs: { id: SubTab; label: string; icon: React.ReactNode }[] = [
    { id: 'info', label: 'Info', icon: <Star className="w-4 h-4" /> },
    { id: 'equipment', label: 'Equipment', icon: <Sword className="w-4 h-4" /> },
    { id: 'inventory', label: 'Inventory', icon: <ShoppingBag className="w-4 h-4" /> },
    { id: 'bank', label: 'Bank', icon: <Building2 className="w-4 h-4" /> },
    { id: 'actions', label: 'Actions', icon: <Wrench className="w-4 h-4" /> },
  ]

  return (
    <div className="flex flex-col h-full">
      {/* Character Header */}
      <div className="bg-white border border-gray-200 rounded-lg p-4 mb-4">
        <div className="flex items-start justify-between">
          <div>
            <div className="flex items-center gap-2 mb-1">
              <h3 className="text-xl font-bold text-gray-900">{character.name}</h3>
              {character.online ? (
                <span className="px-2 py-0.5 bg-green-100 text-green-700 text-xs rounded-full font-medium">● Online</span>
              ) : (
                <span className="px-2 py-0.5 bg-gray-100 text-gray-500 text-xs rounded-full">Offline</span>
              )}
            </div>
            <p className="text-sm text-gray-600">
              Level {character.level} {GENDERS[character.gender]} {RACES[character.race] || 'Unknown'} {CLASSES[character.class] || 'Unknown'}
            </p>
            {character.guild && (
              <p className="text-xs text-blue-600 mt-0.5">⚔ {character.guild}</p>
            )}
          </div>
          <div className="text-right">
            <p className="text-sm text-gray-500">Gold</p>
            <div>{formatGold(character.money ?? 0)}</div>
          </div>
        </div>
      </div>

      {/* Action feedback */}
      {actionMessage && (
        <div className={`mb-3 px-4 py-2 rounded-md text-sm ${actionMessage.type === 'success' ? 'bg-green-50 text-green-700 border border-green-200' : 'bg-red-50 text-red-700 border border-red-200'}`}>
          {actionMessage.text}
        </div>
      )}

      {/* Sub-tab navigation */}
      <div className="flex gap-1 mb-4 bg-gray-100 p-1 rounded-lg">
        {tabs.map(t => (
          <button
            key={t.id}
            onClick={() => setSubTab(t.id)}
            className={`flex-1 flex items-center justify-center gap-1.5 py-1.5 rounded-md text-xs font-medium transition-colors ${
              subTab === t.id ? 'bg-white shadow text-blue-600' : 'text-gray-500 hover:text-gray-700'
            }`}
          >
            {t.icon}{t.label}
          </button>
        ))}
      </div>

      {/* Sub-tab content */}
      <div className="flex-1 overflow-auto">
        {/* INFO TAB */}
        {subTab === 'info' && (
          <div className="bg-white border border-gray-200 rounded-lg p-4 space-y-2">
            <InfoRow label="GUID" value={character.guid.toString()} />
            <InfoRow label="Account ID" value={character.account.toString()} />
            {character.accountUsername && <InfoRow label="Account" value={character.accountUsername} />}
            <InfoRow label="Race" value={RACES[character.race] || `Race ${character.race}`} />
            <InfoRow label="Class" value={CLASSES[character.class] || `Class ${character.class}`} />
            <InfoRow label="Gender" value={GENDERS[character.gender] || 'Unknown'} />
            <InfoRow label="Level" value={character.level.toString()} />
            <InfoRow label="Playtime" value={formatPlaytime(character.totaltime)} />
            <InfoRow label="Guild" value={character.guild || '—'} />
            <InfoRow label="Zone" value={character.zone?.toString() || '—'} />
            <InfoRow label="Map" value={character.map?.toString() || '—'} />
          </div>
        )}

        {/* EQUIPMENT TAB */}
        {subTab === 'equipment' && (
          <div className="bg-white border border-gray-200 rounded-lg p-4">
            <div className="flex items-center justify-between mb-3">
              <h4 className="font-semibold text-gray-700 text-sm">Equipped Items</h4>
              <button onClick={() => refetchInventory()} className="text-gray-400 hover:text-gray-600">
                <RefreshCw className="w-4 h-4" />
              </button>
            </div>
            {inventoryLoading ? (
              <div className="flex items-center justify-center py-8"><Loader2 className="w-6 h-6 animate-spin text-gray-400" /></div>
            ) : (
              <div className="space-y-1">
                {Array.from({ length: 19 }, (_, i) => {
                  const item = inventory?.equippedItems.find(it => it.slot === i)
                  return (
                    <div key={i} className="flex items-center gap-3 py-1.5 border-b border-gray-50 last:border-0">
                      <span className="text-xs text-gray-400 w-20 shrink-0">{EQUIPMENT_SLOT_LABELS[i]}</span>
                      {item ? (
                        <span className={`text-sm font-medium ${QUALITY_COLORS[item.quality]}`}>
                          {item.itemName}
                          {item.stackCount > 1 && <span className="text-gray-400 text-xs"> ×{item.stackCount}</span>}
                        </span>
                      ) : (
                        <span className="text-xs text-gray-300 italic">Empty</span>
                      )}
                    </div>
                  )
                })}
              </div>
            )}
          </div>
        )}

        {/* INVENTORY TAB */}
        {subTab === 'inventory' && (
          <div className="space-y-3">
            <InventorySection
              title="Backpack"
              items={inventory?.backpackItems ?? []}
              loading={inventoryLoading}
              onRefresh={() => refetchInventory()}
            />
            {inventory?.bagItems.map(bag => (
              <InventorySection
                key={bag.containerGuid}
                title={bag.containerName || `Bag (slot ${bag.containerSlot})`}
                items={bag.items}
                loading={false}
                totalSlots={undefined}
              />
            ))}
          </div>
        )}

        {/* BANK TAB */}
        {subTab === 'bank' && (
          <div className="space-y-3">
            <InventorySection
              title="Bank"
              items={inventory?.bankItems ?? []}
              loading={inventoryLoading}
              onRefresh={() => refetchInventory()}
            />
            {inventory?.bankBagItems.map(bag => (
              <InventorySection
                key={bag.containerGuid}
                title={bag.containerName || `Bank Bag (slot ${bag.containerSlot})`}
                items={bag.items}
                loading={false}
              />
            ))}
          </div>
        )}

        {/* ACTIONS TAB */}
        {subTab === 'actions' && (
          <div className="space-y-4">
            {/* Moderation */}
            <ActionSection title="Moderation">
              {character.online && (
                <ActionButton icon={<UserX />} label="Kick" color="red" onClick={() => {
                  if (confirm(`Kick ${character.name}?`)) runAction(() => kickMutation.mutateAsync(character.name), `${character.name} kicked`)
                }} />
              )}
              <ActionButton icon={<Heart />} label="Revive" color="green" onClick={() =>
                runAction(() => reviveMutation.mutateAsync(character.name), `${character.name} revived`)
              } />
              <ActionButton icon={<Ban />} label="Ban" color="red" onClick={() => setActiveDialog('ban')} />
              <ActionButton icon={<Shield />} label="Unban" color="gray" onClick={() =>
                runAction(() => unbanMutation.mutateAsync(character.name), `${character.name} unbanned`)
              } />
              <ActionButton icon={<VolumeX />} label="Mute" color="orange" onClick={() => setActiveDialog('mute')} />
              <ActionButton icon={<Volume2 />} label="Unmute" color="gray" onClick={() =>
                runAction(() => unmuteMutation.mutateAsync(character.name), `${character.name} unmuted`)
              } />
            </ActionSection>

            {/* Gear & Skills */}
            <ActionSection title="Gear & Skills">
              <ActionButton icon={<TrendingUp />} label="Set Level" color="blue" onClick={() => setActiveDialog('level')} />
            </ActionSection>

            {/* Economy */}
            <ActionSection title="Economy">
              <ActionButton icon={<Coins />} label="Modify Gold" color="yellow" onClick={() => setActiveDialog('money')} />
            </ActionSection>

            {/* Items & Mail */}
            <ActionSection title="Items & Mail">
              <ActionButton icon={<Plus />} label="Add Item" color="blue" onClick={() => setActiveDialog('addItem')} />
              <ActionButton icon={<Package />} label="Send Items" color="blue" onClick={() => setActiveDialog('sendItems')} />
              <ActionButton icon={<Coins />} label="Send Money" color="blue" onClick={() => setActiveDialog('sendMoney')} />
              <ActionButton icon={<MessageSquare />} label="Send Message" color="blue" onClick={() => setActiveDialog('message')} />
            </ActionSection>

            {/* Character */}
            <ActionSection title="Character">
              <ActionButton icon={<Edit />} label="Force Rename" color="gray" onClick={() => {
                if (confirm(`Force ${character.name} to rename?`)) runAction(() => renameMutation.mutateAsync(character.name), `${character.name} flagged for rename`)
              }} />
              <ActionButton icon={<Palette />} label="Force Customize" color="gray" onClick={() => {
                if (confirm(`Force ${character.name} to customize?`)) runAction(() => customizeMutation.mutateAsync(character.name), `${character.name} flagged for customization`)
              }} />
            </ActionSection>
          </div>
        )}
      </div>

      {/* Dialogs */}
      {activeDialog === 'ban' && (
        <BanCharacterDialog
          characterName={character.name}
          onClose={() => setActiveDialog(null)}
          onSubmit={async (duration, reason) => {
            await banMutation.mutateAsync({ characterName: character.name, request: { duration, reason } })
            showSuccess(`${character.name} banned for ${duration}`)
          }}
        />
      )}
      {activeDialog === 'mute' && (
        <MuteCharacterDialog
          characterName={character.name}
          onClose={() => setActiveDialog(null)}
          onSubmit={async (minutes, reason) => {
            await muteMutation.mutateAsync({ characterName: character.name, request: { minutes, reason } })
            showSuccess(`${character.name} muted for ${minutes} minutes`)
          }}
        />
      )}
      {activeDialog === 'money' && (
        <ModifyMoneyDialog
          characterName={character.name}
          currentMoney={character.money ?? 0}
          onClose={() => setActiveDialog(null)}
          onSubmit={async (copperAmount) => {
            await modifyMoneyMutation.mutateAsync({ characterName: character.name, request: { copperAmount } })
            showSuccess(`Gold modified for ${character.name}`)
          }}
        />
      )}
      {activeDialog === 'addItem' && (
        <AddItemDialog
          characterName={character.name}
          onClose={() => setActiveDialog(null)}
          onSubmit={async (itemId, count) => {
            await addItemMutation.mutateAsync({ characterGuid: character.guid, characterName: character.name, request: { itemId, count } })
            showSuccess(`Item added to ${character.name}`)
          }}
        />
      )}
      {activeDialog === 'message' && (
        <SendMessageDialog
          characterName={character.name}
          onClose={() => setActiveDialog(null)}
          onSubmit={async (subject, body) => {
            await sendMessageMutation.mutateAsync({ characterName: character.name, request: { subject, body } })
            showSuccess(`Message sent to ${character.name}`)
          }}
        />
      )}
      {activeDialog === 'sendItems' && (
        <SendItemsDialog
          characterName={character.name}
          onClose={() => setActiveDialog(null)}
          onSubmit={async (itemId, count, subject, body) => {
            await sendItemsMutation.mutateAsync({ characterName: character.name, request: { itemId, count, subject, body } })
            showSuccess(`Items sent to ${character.name}`)
          }}
        />
      )}
      {activeDialog === 'sendMoney' && (
        <SendMoneyDialog
          characterName={character.name}
          onClose={() => setActiveDialog(null)}
          onSubmit={async (copperAmount, subject, body) => {
            await sendMoneyMutation.mutateAsync({ characterName: character.name, request: { copperAmount, subject, body } })
            showSuccess(`Money sent to ${character.name}`)
          }}
        />
      )}
      {activeDialog === 'level' && (
        <SetLevelDialog
          characterName={character.name}
          currentLevel={character.level}
          onClose={() => setActiveDialog(null)}
          onSubmit={async (level) => {
            await setLevelMutation.mutateAsync({ characterName: character.name, request: { level } })
            showSuccess(`${character.name} level set to ${level}`)
          }}
        />
      )}
    </div>
  )
}

// --- Helper sub-components ---

function InfoRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between py-1 border-b border-gray-50 last:border-0">
      <span className="text-xs text-gray-500">{label}</span>
      <span className="text-sm text-gray-800 font-medium">{value}</span>
    </div>
  )
}

function InventorySection({
  title,
  items,
  loading,
  onRefresh,
}: {
  title: string
  items: { slot: number; itemName: string; quality: number; stackCount: number; itemEntry: number }[]
  loading: boolean
  totalSlots?: number
  onRefresh?: () => void
}) {
  return (
    <div className="bg-white border border-gray-200 rounded-lg p-4">
      <div className="flex items-center justify-between mb-3">
        <h4 className="font-semibold text-gray-700 text-sm">{title} ({items.length} items)</h4>
        {onRefresh && (
          <button onClick={onRefresh} className="text-gray-400 hover:text-gray-600">
            <RefreshCw className="w-4 h-4" />
          </button>
        )}
      </div>
      {loading ? (
        <div className="flex items-center justify-center py-4"><Loader2 className="w-5 h-5 animate-spin text-gray-400" /></div>
      ) : items.length === 0 ? (
        <p className="text-xs text-gray-400 italic text-center py-2">Empty</p>
      ) : (
        <div className="space-y-1">
          {items.map(item => (
            <div key={item.slot} className="flex items-center gap-2 py-1 border-b border-gray-50 last:border-0">
              <span className="text-xs text-gray-400 w-8 shrink-0">#{item.slot}</span>
              <span className={`text-sm font-medium ${QUALITY_COLORS[item.quality]}`}>
                {item.itemName}
              </span>
              {item.stackCount > 1 && (
                <span className="text-xs text-gray-400 ml-auto">×{item.stackCount}</span>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

const COLOR_MAP: Record<string, string> = {
  red: 'bg-red-600 hover:bg-red-700 text-white',
  blue: 'bg-blue-600 hover:bg-blue-700 text-white',
  green: 'bg-green-600 hover:bg-green-700 text-white',
  yellow: 'bg-yellow-500 hover:bg-yellow-600 text-white',
  orange: 'bg-orange-500 hover:bg-orange-600 text-white',
  purple: 'bg-purple-600 hover:bg-purple-700 text-white',
  gray: 'bg-gray-100 hover:bg-gray-200 text-gray-700 border border-gray-300',
}

function ActionSection({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="bg-white border border-gray-200 rounded-lg p-4">
      <h4 className="font-semibold text-gray-700 text-sm mb-3">{title}</h4>
      <div className="flex flex-wrap gap-2">{children}</div>
    </div>
  )
}

function ActionButton({ icon, label, color, onClick }: { icon: React.ReactNode; label: string; color: string; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      className={`flex items-center gap-1.5 px-3 py-1.5 rounded-md text-xs font-medium transition-colors ${COLOR_MAP[color] || COLOR_MAP.gray}`}
    >
      <span className="w-3.5 h-3.5 [&>svg]:w-3.5 [&>svg]:h-3.5">{icon}</span>
      {label}
    </button>
  )
}
