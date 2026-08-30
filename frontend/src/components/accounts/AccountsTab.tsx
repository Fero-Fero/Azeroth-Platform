import { useState, useMemo } from 'react'
import { Plus, Search, Loader2, Users, Shield, Bot, Terminal } from 'lucide-react'
import { useAccounts, useCreateAccount } from '@/hooks/useAccounts'
import type { AccountDto } from '@/types/account.types'
import AccountDetailsPanel from './AccountDetailsPanel'
import CreateAccountDialog from './dialogs/CreateAccountDialog'

interface AccountsTabProps {
  stackId: string
  expressGameAccountCreated?: boolean
}

const BOT_ACCOUNT_PATTERN = /^RNDBOT\d+$/i

export default function AccountsTab({ stackId, expressGameAccountCreated = false }: AccountsTabProps) {
  const { data: accounts, isLoading, error } = useAccounts(stackId)
  const createAccountMutation = useCreateAccount(stackId)
  const [selectedAccount, setSelectedAccount] = useState<AccountDto | null>(null)
  const [showCreateDialog, setShowCreateDialog] = useState(false)
  const [searchQuery, setSearchQuery] = useState('')
  const [hideBots, setHideBots] = useState(true) // Hide bots by default

  // Filter and sort accounts
  const filteredAccounts = useMemo(() => {
    if (!accounts) return []
    
    let filtered = [...accounts]
    
    // Filter out bot accounts if enabled
    if (hideBots) {
      filtered = filtered.filter((account) => !BOT_ACCOUNT_PATTERN.test(account.username))
    }
    
    // Filter by search query
    if (searchQuery.trim()) {
      const query = searchQuery.toLowerCase()
      filtered = filtered.filter((account) =>
        account.username.toLowerCase().includes(query)
      )
    }
    
    // Always sort alphabetically
    filtered.sort((a, b) => a.username.localeCompare(b.username))
    
    return filtered
  }, [accounts, searchQuery, hideBots])

  const handleCreateAccount = async (username: string, password: string) => {
    await createAccountMutation.mutateAsync({
      username,
      password,
      expansion: 2, // WotLK
    })
  }

  const getGmLevelBadge = (level: number) => {
    switch (level) {
      case 0:
        return null
      case 1:
        return <span className="text-xs text-blue-600">Mod</span>
      case 2:
        return <span className="text-xs text-purple-600">GM</span>
      case 3:
        return <span className="text-xs text-red-600">Admin</span>
      default:
        return null
    }
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 className="w-8 h-8 text-blue-600 animate-spin" />
      </div>
    )
  }

  if (error) {
    const errorMessage = error instanceof Error ? error.message : 'Unknown error'
    const isConnectionRefused = errorMessage.includes('Connection refused') || 
                                errorMessage.includes('connect to any of the specified MySQL hosts')
    
    return (
      <div className="space-y-4">
        {expressGameAccountCreated && (
          <div className="rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-900">
            Express Setup created your game account: username <strong>admin</strong>, password{' '}
            <strong>admin</strong> (GM level 3 on all realms).
          </div>
        )}
      <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-6">
        <div className="flex items-start gap-3">
          <div className="flex-shrink-0 w-10 h-10 bg-yellow-100 rounded-full flex items-center justify-center">
            <span className="text-yellow-600 text-xl">⚠️</span>
          </div>
          <div className="flex-1">
            <h3 className="text-lg font-semibold text-yellow-900 mb-2">
              {isConnectionRefused ? 'Stack Not Running' : 'Failed to Load Accounts'}
            </h3>
            {isConnectionRefused ? (
              <div className="space-y-2 text-yellow-800">
                <p>
                  The MySQL database for this stack is not accessible. This usually means:
                </p>
                <ul className="list-disc list-inside ml-4 space-y-1">
                  <li>The stack hasn't been built yet (use the wizard to build it)</li>
                  <li>The stack containers are stopped (click "Start" on the Overview tab)</li>
                  <li>The database container is still starting up (wait a moment and refresh)</li>
                </ul>
                <p className="mt-3">
                  Go to the <strong>Overview</strong> tab to start the stack, then return here.
                </p>
              </div>
            ) : (
              <p className="text-yellow-800">
                {errorMessage}
              </p>
            )}
          </div>
        </div>
      </div>
      </div>
    )
  }

  return (
    <>
      <div className="space-y-4">
        {expressGameAccountCreated && (
          <div className="rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-900">
            Express Setup created your game account: username <strong>admin</strong>, password{' '}
            <strong>admin</strong> (GM level 3 on all realms).
          </div>
        )}
        {/* Header */}
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <Users className="w-5 h-5 text-gray-600" />
            <h2 className="text-xl font-semibold">
              Accounts ({accounts?.length || 0})
            </h2>
          </div>
          <button
            onClick={() => setShowCreateDialog(true)}
            className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 flex items-center gap-2"
            title="Creates a game account via the worldserver SOAP interface. The worldserver must be running with SOAP enabled and the SOAP admin account initialized."
          >
            <Plus className="w-4 h-4" />
            Create Account
            <span className="inline-flex items-center gap-1 ml-1 px-1.5 py-0.5 bg-blue-500/60 rounded text-[10px] font-semibold uppercase tracking-wide">
              <Terminal className="w-3 h-3" />
              SOAP
            </span>
          </button>
        </div>

        {/* Search and Filters */}
        <div className="flex gap-3">
          <div className="relative flex-1">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-5 h-5 text-gray-400" />
            <input
              type="text"
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              placeholder="Search accounts by username..."
              className="w-full pl-10 pr-4 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <button
            onClick={() => setHideBots(!hideBots)}
            className={`px-4 py-2 rounded-md border flex items-center gap-2 transition-colors ${
              hideBots
                ? 'bg-blue-50 border-blue-300 text-blue-700 hover:bg-blue-100'
                : 'bg-white border-gray-300 text-gray-700 hover:bg-gray-50'
            }`}
            title={hideBots ? 'Show bot accounts (RNDBOT0-99)' : 'Hide bot accounts (RNDBOT0-99)'}
          >
            <Bot className="w-4 h-4" />
            {hideBots ? 'Bots Hidden' : 'Show All'}
          </button>
        </div>

        {/* Layout: Table + Details Panel */}
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
          {/* Accounts Table */}
          <div className="border border-gray-200 rounded-lg overflow-hidden">
            <div className="overflow-x-auto">
              <table className="w-full">
                <thead className="bg-gray-50 border-b border-gray-200">
                  <tr>
                    <th className="px-4 py-3 text-left text-xs font-semibold text-gray-700 uppercase">
                      Username
                    </th>
                    <th className="px-4 py-3 text-center text-xs font-semibold text-gray-700 uppercase">
                      Status
                    </th>
                    <th className="px-4 py-3 text-center text-xs font-semibold text-gray-700 uppercase">
                      GM
                    </th>
                    <th className="px-4 py-3 text-center text-xs font-semibold text-gray-700 uppercase">
                      Chars
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-200">
                  {filteredAccounts.length === 0 ? (
                    <tr>
                      <td colSpan={4} className="px-4 py-8 text-center text-gray-500">
                        {searchQuery ? 'No accounts found' : 'No accounts yet. Create one to get started.'}
                      </td>
                    </tr>
                  ) : (
                    filteredAccounts.map((account) => (
                      <tr
                        key={account.id}
                        onClick={() => setSelectedAccount(account)}
                        className={`cursor-pointer hover:bg-gray-50 transition-colors ${
                          selectedAccount?.id === account.id ? 'bg-blue-50' : ''
                        }`}
                      >
                        <td className="px-4 py-3">
                          <div className="flex items-center gap-2">
                            <span className="font-medium">{account.username}</span>
                            {account.isBanned && (
                              <span className="px-2 py-0.5 bg-red-100 text-red-700 text-xs font-medium rounded">
                                BANNED
                              </span>
                            )}
                          </div>
                        </td>
                        <td className="px-4 py-3 text-center">
                          {account.isOnline ? (
                            <span className="inline-block w-2 h-2 bg-green-500 rounded-full"></span>
                          ) : (
                            <span className="inline-block w-2 h-2 bg-gray-300 rounded-full"></span>
                          )}
                        </td>
                        <td className="px-4 py-3 text-center">
                          <div className="flex items-center justify-center gap-1">
                            {account.gmLevel > 0 && <Shield className="w-3 h-3" />}
                            {getGmLevelBadge(account.gmLevel)}
                          </div>
                        </td>
                        <td className="px-4 py-3 text-center">
                          <span className="text-sm text-gray-600">
                            {account.characterCount}
                          </span>
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </div>

          {/* Details Panel */}
          <div>
            {selectedAccount ? (
              <AccountDetailsPanel
                account={selectedAccount}
                stackId={stackId}
                onClose={() => setSelectedAccount(null)}
              />
            ) : (
              <div className="border border-gray-200 rounded-lg bg-gray-50 flex items-center justify-center h-64">
                <p className="text-gray-500">Select an account to view details</p>
              </div>
            )}
          </div>
        </div>
      </div>

      {showCreateDialog && (
        <CreateAccountDialog
          onClose={() => setShowCreateDialog(false)}
          onSubmit={handleCreateAccount}
        />
      )}
    </>
  )
}
