import { useState } from 'react'
import { Shield, Ban, Trash2, Key, X, Users, ChevronRight } from 'lucide-react'
import type { AccountDto, CharacterDto } from '@/types/account.types'
import BanAccountDialog from './dialogs/BanAccountDialog'
import SetPasswordDialog from './dialogs/SetPasswordDialog'
import DeleteAccountDialog from './dialogs/DeleteAccountDialog'
import CharacterDetailDialog from '@/components/characters/CharacterDetailDialog'
import { useAllCharacters } from '@/hooks/useCharacters'
import {
  useSetGmLevel,
  useBanAccount,
  useUnbanAccount,
  useDeleteAccount,
  useSetPassword,
} from '@/hooks/useAccounts'
import { RACES, CLASSES } from '@/types/character.types'

interface AccountDetailsPanelProps {
  account: AccountDto
  stackId: string
  onClose: () => void
}

export default function AccountDetailsPanel({ account, stackId, onClose }: AccountDetailsPanelProps) {
  const [selectedGmLevel, setSelectedGmLevel] = useState(account.gmLevel.toString())
  const [activeDialog, setActiveDialog] = useState<string | null>(null)
  const [selectedCharacter, setSelectedCharacter] = useState<CharacterDto | null>(null)

  const { data: allCharacters } = useAllCharacters(stackId)
  const accountCharacters = (allCharacters ?? []).filter(c => c.account === account.id)

  const setGmLevelMutation = useSetGmLevel(stackId)
  const banAccountMutation = useBanAccount(stackId)
  const unbanAccountMutation = useUnbanAccount(stackId)
  const deleteAccountMutation = useDeleteAccount(stackId)
  const setPasswordMutation = useSetPassword(stackId)

  const handleSetGmLevel = async () => {
    const gmLevel = parseInt(selectedGmLevel)
    if (gmLevel === account.gmLevel) return

    try {
      await setGmLevelMutation.mutateAsync({
        accountId: account.id,
        request: { username: account.username, level: gmLevel, realmId: -1 }
      })
      alert(`Successfully set GM level to ${gmLevel} for account ${account.username}`)
    } catch (err: any) {
      alert(err.response?.data?.error || 'Failed to set GM level')
    }
  }

  const handleBan = async (duration: string, reason: string) => {
    try {
      await banAccountMutation.mutateAsync({
        accountId: account.id,
        request: { username: account.username, duration, reason }
      })
      alert(`Successfully banned account ${account.username} for ${duration}`)
    } catch (err: any) {
      alert(err.response?.data?.error || 'Failed to ban account')
    }
  }

  const handleUnban = async () => {
    if (confirm(`Unban account ${account.username}?`)) {
      try {
        await unbanAccountMutation.mutateAsync({
          accountId: account.id,
          request: { username: account.username }
        })
        alert(`Successfully unbanned account ${account.username}`)
      } catch (err: any) {
        alert(err.response?.data?.message || 'Failed to unban account')
      }
    }
  }

  const handleSetPassword = async (newPassword: string) => {
    try {
      await setPasswordMutation.mutateAsync({
        accountId: account.id,
        request: { username: account.username, password: newPassword }
      })
      alert(`Successfully changed password for account ${account.username}`)
      setActiveDialog(null)
    } catch (err: any) {
      alert(err.response?.data?.error || 'Failed to change password')
    }
  }

  const handleDelete = async () => {
    try {
      await deleteAccountMutation.mutateAsync({
        accountId: account.id,
        request: { username: account.username }
      })
      alert(`Successfully deleted account ${account.username}`)
      onClose()
    } catch (err: any) {
      alert(err.response?.data?.error || 'Failed to delete account')
    }
  }

  return (
    <>
      <div className="border border-gray-200 rounded-lg bg-white shadow-sm">
        <div className="flex items-center justify-between p-4 border-b border-gray-200">
          <div className="flex items-center gap-3">
            <h3 className="text-xl font-semibold">{account.username}</h3>
            {account.isOnline && (
              <span className="px-3 py-1 bg-green-100 text-green-700 text-sm rounded-full">
                🟢 Online
              </span>
            )}
            {account.isBanned && (
              <span className="px-3 py-1 bg-red-100 text-red-700 text-sm font-medium rounded-full">
                BANNED
              </span>
            )}
          </div>
          <button
            onClick={onClose}
            className="text-gray-400 hover:text-gray-600"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        <div className="p-4 space-y-6">
          {/* Account Info */}
          <div>
            <h4 className="font-semibold text-sm text-gray-700 mb-2">Account Information</h4>
            <div className="grid grid-cols-2 gap-2 text-sm">
              <div className="text-gray-600">Account ID:</div>
              <div className="font-medium">{account.id}</div>
              <div className="text-gray-600">Last Login:</div>
              <div className="font-medium">
                {account.lastLogin ? new Date(account.lastLogin).toLocaleString() : 'Never'}
              </div>
              <div className="text-gray-600">Characters:</div>
              <div className="font-medium">{account.characterCount}</div>
            </div>
          </div>

          {/* Ban Info */}
          {account.isBanned && (
            <div className="bg-red-50 border border-red-200 rounded-lg p-4">
              <h4 className="font-semibold text-sm text-red-700 mb-2">Ban Information</h4>
              <div className="grid grid-cols-2 gap-2 text-sm">
                <div className="text-red-600">Reason:</div>
                <div className="font-medium text-red-800">{account.banReason || 'N/A'}</div>
                <div className="text-red-600">Banned by:</div>
                <div className="font-medium text-red-800">{account.bannedBy || 'Unknown'}</div>
                <div className="text-red-600">Expires:</div>
                <div className="font-medium text-red-800">
                  {account.banExpiry ? new Date(account.banExpiry).toLocaleString() : 'Permanent'}
                </div>
              </div>
            </div>
          )}

          {/* GM Level Control */}
          <div>
            <label className="block text-sm font-semibold text-gray-700 mb-2">
              <Shield className="w-4 h-4 inline mr-1" />
              GM Level
            </label>
            <div className="flex gap-2">
              <select
                value={selectedGmLevel}
                onChange={(e) => setSelectedGmLevel(e.target.value)}
                className="flex-1 px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              >
                <option value="0">0 - Player</option>
                <option value="1">1 - Moderator</option>
                <option value="2">2 - Game Master</option>
                <option value="3">3 - Administrator</option>
              </select>
              <button
                onClick={handleSetGmLevel}
                disabled={parseInt(selectedGmLevel) === account.gmLevel || setGmLevelMutation.isPending}
                className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed"
              >
                Set Level
              </button>
            </div>
          </div>

          {/* Actions */}
          <div>
            <h4 className="font-semibold text-sm text-gray-700 mb-2">Account Actions</h4>
            <div className="grid grid-cols-2 gap-2">
              {account.isBanned ? (
                <button
                  onClick={handleUnban}
                  className="px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 flex items-center justify-center gap-2"
                >
                  <Ban className="w-4 h-4" />
                  Unban Account
                </button>
              ) : (
                <button
                  onClick={() => setActiveDialog('ban')}
                  className="px-4 py-2 bg-orange-600 text-white rounded-md hover:bg-orange-700 flex items-center justify-center gap-2"
                >
                  <Ban className="w-4 h-4" />
                  Ban Account
                </button>
              )}
              <button
                onClick={() => setActiveDialog('password')}
                className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 flex items-center justify-center gap-2"
              >
                <Key className="w-4 h-4" />
                Reset Password
              </button>
              <button
                onClick={() => setActiveDialog('delete')}
                className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 flex items-center justify-center gap-2 col-span-2"
              >
                <Trash2 className="w-4 h-4" />
                Delete Account
              </button>
            </div>
          </div>

          {/* Characters */}
          <div>
            <div className="flex items-center gap-2 mb-2">
              <Users className="w-4 h-4 text-gray-500" />
              <h4 className="font-semibold text-gray-700 text-sm">
                Characters ({account.characterCount})
              </h4>
            </div>
            {accountCharacters.length === 0 ? (
              <p className="text-sm text-gray-400 italic">No characters on this account.</p>
            ) : (
              <div className="space-y-1">
                {accountCharacters.map(c => (
                  <button
                    key={c.guid}
                    onClick={() => setSelectedCharacter(c)}
                    className="w-full flex items-center justify-between px-3 py-2 rounded-md border border-gray-200 hover:border-blue-300 hover:bg-blue-50/40 transition-colors text-left"
                  >
                    <div>
                      <span className="font-medium text-sm">{c.name}</span>
                      <span className="ml-2 text-xs text-gray-500">
                        Lv {c.level} {RACES[c.race] || '?'} {CLASSES[c.class] || '?'}
                      </span>
                      {c.online && (
                        <span className="ml-2 text-xs text-green-600">● Online</span>
                      )}
                    </div>
                    <ChevronRight className="w-4 h-4 text-gray-400 shrink-0" />
                  </button>
                ))}
              </div>
            )}
          </div>
        </div>
      </div>

      {activeDialog === 'ban' && (
        <BanAccountDialog
          accountUsername={account.username}
          onClose={() => setActiveDialog(null)}
          onSubmit={handleBan}
        />
      )}
      {activeDialog === 'password' && (
        <SetPasswordDialog
          accountUsername={account.username}
          onClose={() => setActiveDialog(null)}
          onSubmit={handleSetPassword}
        />
      )}
      {activeDialog === 'delete' && (
        <DeleteAccountDialog
          accountUsername={account.username}
          onClose={() => setActiveDialog(null)}
          onSubmit={handleDelete}
        />
      )}
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
