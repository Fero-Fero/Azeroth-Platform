import { useState } from 'react'
import {
  Loader2,
  Globe,
  Save,
  RefreshCw,
  Check,
  AlertCircle,
  Plus,
  X,
  Search,
  ChevronDown,
  ChevronRight,
  ChevronsUpDown,
  ChevronsDownUp,
  Network,
} from 'lucide-react'
import { useRealms, useCreateRealm, useUpdateRealm } from '@/hooks/useRealms'
import RealmlistAddressField from '@/components/launcher/RealmlistAddressField'
import {
  REALM_TYPES,
  SECURITY_LEVELS,
  REALM_TIMEZONES,
  REALM_FLAG_OFFLINE,
  REALM_FLAG_RECOMMENDED,
  REALM_FLAG_NEW_PLAYERS,
  realmTypeLabel,
} from '@/types/realm.types'
import type { RealmDto } from '@/types/realm.types'

interface RealmsTabProps {
  stackId: string
}

export default function RealmsTab({ stackId }: RealmsTabProps) {
  const { data: realms, isLoading, error, refetch } = useRealms(stackId)
  const [creating, setCreating] = useState(false)
  const [search, setSearch] = useState('')
  // Realms are collapsed by default — an id is only present here once explicitly expanded.
  const [expandedIds, setExpandedIds] = useState<Set<number>>(new Set())

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16">
        <Loader2 className="w-6 h-6 animate-spin text-gray-400 mr-2" />
        <span className="text-gray-500">Loading realms...</span>
      </div>
    )
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
        <p className="text-red-600">Failed to load realms. The stack's database must be running.</p>
        <button
          onClick={() => refetch()}
          className="mt-3 inline-flex items-center gap-1.5 px-3 py-1.5 text-sm border border-red-300 text-red-700 rounded-md hover:bg-red-100"
        >
          <RefreshCw className="w-4 h-4" /> Retry
        </button>
      </div>
    )
  }

  const allRealms = realms ?? []
  const query = search.trim().toLowerCase()
  const filtered = query
    ? allRealms.filter(r => r.name.toLowerCase().includes(query) || String(r.id) === query)
    : allRealms

  const allExpanded = filtered.length > 0 && filtered.every(r => expandedIds.has(r.id))

  const toggleAll = () => {
    setExpandedIds(allExpanded ? new Set() : new Set(filtered.map(r => r.id)))
  }

  const toggleOne = (id: number) => {
    setExpandedIds(prev => {
      const next = new Set(prev)
      if (next.has(id)) {
        next.delete(id)
      } else {
        next.add(id)
      }
      return next
    })
  }

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-3 mb-4">
        <div className="flex items-center gap-3">
          <span className="flex items-center gap-2 text-sm text-gray-500">
            <Globe className="w-4 h-4" />
            {allRealms.length} realm{allRealms.length === 1 ? '' : 's'} in this stack's auth database
          </span>
          <button
            onClick={toggleAll}
            disabled={filtered.length === 0}
            className="inline-flex items-center gap-1.5 px-3 py-2 border border-gray-300 text-gray-600 rounded-md text-sm font-medium hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {allExpanded ? <ChevronsDownUp className="w-4 h-4" /> : <ChevronsUpDown className="w-4 h-4" />}
            {allExpanded ? 'Collapse all' : 'Expand all'}
          </button>
        </div>
        <div className="flex items-center gap-2">
          <div className="relative">
            <Search className="absolute left-2.5 top-2.5 w-4 h-4 text-gray-400" />
            <input
              type="text"
              value={search}
              onChange={e => setSearch(e.target.value)}
              placeholder="Search realms..."
              className="w-48 pl-8 pr-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <button
            onClick={() => setCreating(c => !c)}
            className="inline-flex items-center gap-1.5 px-3 py-2 bg-blue-600 text-white rounded-md text-sm font-medium hover:bg-blue-700 whitespace-nowrap"
          >
            <Plus className="w-4 h-4" /> New realm
          </button>
          <button
            onClick={() => refetch()}
            className="p-2 text-gray-400 hover:text-gray-600 border border-gray-300 rounded-md"
            title="Refresh"
          >
            <RefreshCw className="w-4 h-4" />
          </button>
        </div>
      </div>

      <RealmAddressCard stackId={stackId} />

      {creating && (
        <div className="mb-4">
          <CreateRealmCard stackId={stackId} onClose={() => setCreating(false)} />
        </div>
      )}

      {allRealms.length === 0 && !creating ? (
        <div className="text-center py-10 text-gray-400 text-sm">No realms found in realmlist.</div>
      ) : filtered.length === 0 ? (
        <div className="text-center py-10 text-gray-400 text-sm">No realms match &ldquo;{search}&rdquo;.</div>
      ) : (
        <div className="space-y-4">
          {filtered.map(realm => (
            <RealmCard
              key={realm.id}
              stackId={stackId}
              realm={realm}
              expanded={expandedIds.has(realm.id)}
              onToggle={() => toggleOne(realm.id)}
            />
          ))}
        </div>
      )}
    </div>
  )
}

function RealmAddressCard({ stackId }: { stackId: string }) {
  return (
    <div className="mb-4 bg-white border border-gray-200 rounded-lg p-5">
      <div className="flex items-center gap-2 mb-1">
        <Network className="w-4 h-4 text-gray-500" />
        <h3 className="text-sm font-semibold text-gray-900">Realm address (realmlist)</h3>
      </div>
      <p className="text-xs text-gray-500 mb-3">
        The host/IP players are redirected to after logging in. It must be reachable from their
        machines &mdash; <span className="font-mono">127.0.0.1</span> only works on this computer.
      </p>

      <RealmlistAddressField stackId={stackId} />
    </div>
  )
}

function CreateRealmCard({ stackId, onClose }: { stackId: string; onClose: () => void }) {
  const createRealm = useCreateRealm(stackId)

  const [name, setName] = useState('')
  const [type, setType] = useState(0)
  const [timezone, setTimezone] = useState(1)
  const [allowedSecurityLevel, setAllowedSecurityLevel] = useState(0)
  const [flags, setFlags] = useState(0)
  const [errorMsg, setErrorMsg] = useState<string | null>(null)

  const handleCreate = async () => {
    setErrorMsg(null)
    if (name.trim().length === 0) {
      setErrorMsg('Realm name is required.')
      return
    }
    try {
      await createRealm.mutateAsync({ name: name.trim(), type, flags, timezone, allowedSecurityLevel })
      onClose()
    } catch (e) {
      const message = (e as { response?: { data?: { error?: string } } })?.response?.data?.error
      setErrorMsg(message ?? 'Failed to create realm.')
    }
  }

  return (
    <div className="bg-blue-50/40 border border-blue-200 rounded-lg p-5">
      <div className="flex items-center justify-between mb-4">
        <h3 className="text-lg font-semibold text-gray-900">New realm</h3>
        <button onClick={onClose} className="p-1 text-gray-400 hover:text-gray-600" title="Cancel">
          <X className="w-4 h-4" />
        </button>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Realm Name</label>
          <input
            type="text"
            value={name}
            maxLength={64}
            autoFocus
            placeholder="e.g. Blackrock PvP"
            onChange={e => setName(e.target.value)}
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
        </div>

        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Realm Type</label>
          <select
            value={type}
            onChange={e => setType(Number(e.target.value))}
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            {REALM_TYPES.map(t => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
        </div>

        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Timezone / Region</label>
          <select
            value={timezone}
            onChange={e => setTimezone(Number(e.target.value))}
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            {REALM_TIMEZONES.map(t => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
        </div>

        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Who can connect</label>
          <select
            value={allowedSecurityLevel}
            onChange={e => setAllowedSecurityLevel(Number(e.target.value))}
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            {SECURITY_LEVELS.map(l => (
              <option key={l.value} value={l.value}>
                {l.label}
              </option>
            ))}
          </select>
        </div>
      </div>

      <div className="mt-4">
        <label className="block text-sm font-medium text-gray-700 mb-2">Flags</label>
        <div className="flex flex-wrap gap-4">
          <label className="flex items-center gap-2 text-sm text-gray-600 cursor-pointer">
            <input
              type="checkbox"
              checked={hasFlag(flags, REALM_FLAG_RECOMMENDED)}
              onChange={e => setFlags(f => setFlag(f, REALM_FLAG_RECOMMENDED, e.target.checked))}
              className="rounded"
            />
            Recommended
          </label>
          <label className="flex items-center gap-2 text-sm text-gray-600 cursor-pointer">
            <input
              type="checkbox"
              checked={hasFlag(flags, REALM_FLAG_NEW_PLAYERS)}
              onChange={e => setFlags(f => setFlag(f, REALM_FLAG_NEW_PLAYERS, e.target.checked))}
              className="rounded"
            />
            New Players
          </label>
          <label className="flex items-center gap-2 text-sm text-gray-600 cursor-pointer">
            <input
              type="checkbox"
              checked={hasFlag(flags, REALM_FLAG_OFFLINE)}
              onChange={e => setFlags(f => setFlag(f, REALM_FLAG_OFFLINE, e.target.checked))}
              className="rounded"
            />
            Offline (hide from realm list)
          </label>
        </div>
      </div>

      <p className="mt-4 text-xs text-gray-500">
        The new realm copies its world address and port from this stack's existing realm. To actually
        host players on it you'll need a world server bound to this realm id.
      </p>

      {errorMsg && (
        <div className="mt-3 flex items-center gap-2 text-sm text-red-600">
          <AlertCircle className="w-4 h-4" />
          {errorMsg}
        </div>
      )}

      <div className="mt-4 flex items-center gap-3">
        <button
          onClick={handleCreate}
          disabled={createRealm.isPending}
          className="inline-flex items-center gap-1.5 px-4 py-2 bg-blue-600 text-white rounded-md text-sm font-medium hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {createRealm.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : <Plus className="w-4 h-4" />}
          Create realm
        </button>
        <button
          onClick={onClose}
          className="px-4 py-2 border border-gray-300 text-gray-600 rounded-md text-sm font-medium hover:bg-gray-50"
        >
          Cancel
        </button>
      </div>
    </div>
  )
}

function hasFlag(flags: number, bit: number): boolean {
  return (flags & bit) === bit
}

function setFlag(flags: number, bit: number, on: boolean): number {
  return on ? flags | bit : flags & ~bit
}

function RealmCard({
  stackId,
  realm,
  expanded,
  onToggle,
}: {
  stackId: string
  realm: RealmDto
  expanded: boolean
  onToggle: () => void
}) {
  const updateRealm = useUpdateRealm(stackId)

  const [name, setName] = useState(realm.name)
  const [type, setType] = useState(realm.type)
  const [timezone, setTimezone] = useState(realm.timezone)
  const [allowedSecurityLevel, setAllowedSecurityLevel] = useState(realm.allowedSecurityLevel)
  const [flags, setFlags] = useState(realm.flags)
  const [saved, setSaved] = useState(false)
  const [errorMsg, setErrorMsg] = useState<string | null>(null)

  // Reset local edit state to match the server when a fresh realm object arrives (e.g. after
  // save/refresh) — the React-recommended "adjust state during render" pattern.
  const [syncedRealm, setSyncedRealm] = useState(realm)
  if (syncedRealm !== realm) {
    setSyncedRealm(realm)
    setName(realm.name)
    setType(realm.type)
    setTimezone(realm.timezone)
    setAllowedSecurityLevel(realm.allowedSecurityLevel)
    setFlags(realm.flags)
  }

  const dirty =
    name !== realm.name ||
    type !== realm.type ||
    timezone !== realm.timezone ||
    allowedSecurityLevel !== realm.allowedSecurityLevel ||
    flags !== realm.flags

  const handleSave = async () => {
    setErrorMsg(null)
    setSaved(false)
    try {
      await updateRealm.mutateAsync({
        realmId: realm.id,
        request: { name: name.trim(), type, flags, timezone, allowedSecurityLevel },
      })
      setSaved(true)
      setTimeout(() => setSaved(false), 2500)
    } catch (e) {
      const message = (e as { response?: { data?: { error?: string } } })?.response?.data?.error
      setErrorMsg(message ?? 'Failed to save realm.')
    }
  }

  return (
    <div className="bg-white border border-gray-200 rounded-lg">
      <button
        onClick={onToggle}
        className="w-full flex items-center justify-between gap-3 p-5 text-left"
      >
        <div className="flex items-center gap-3 min-w-0">
          {expanded ? (
            <ChevronDown className="w-4 h-4 text-gray-400 shrink-0" />
          ) : (
            <ChevronRight className="w-4 h-4 text-gray-400 shrink-0" />
          )}
          <div className="min-w-0">
            <h3 className="text-lg font-semibold text-gray-900 truncate">
              {realm.name || 'Unnamed realm'}
              {dirty && <span className="ml-2 text-xs font-normal text-amber-600">• unsaved changes</span>}
            </h3>
            <p className="text-xs text-gray-400 mt-0.5 truncate">
              Realm ID {realm.id} · {realmTypeLabel(realm.type)} · {realm.address || 'no address'}:{realm.port}
            </p>
          </div>
        </div>
        {realm.id === 1 && (
          <span className="text-[11px] px-2 py-0.5 rounded-full bg-blue-50 text-blue-600 border border-blue-100 shrink-0">
            Primary realm
          </span>
        )}
      </button>

      {expanded && (
      <div className="px-5 pb-5 border-t border-gray-100 pt-4">
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Realm Name</label>
          <input
            type="text"
            value={name}
            maxLength={64}
            onChange={e => setName(e.target.value)}
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
          <p className="text-xs text-gray-400 mt-1">Shown in the client's realm-selection screen.</p>
        </div>

        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Realm Type</label>
          <select
            value={type}
            onChange={e => setType(Number(e.target.value))}
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            {REALM_TYPES.map(t => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
          <p className="text-xs text-gray-400 mt-1">PvP realms enable open-world player combat.</p>
        </div>

        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Timezone / Region</label>
          <select
            value={timezone}
            onChange={e => setTimezone(Number(e.target.value))}
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            {REALM_TIMEZONES.map(t => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
        </div>

        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Who can connect</label>
          <select
            value={allowedSecurityLevel}
            onChange={e => setAllowedSecurityLevel(Number(e.target.value))}
            className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            {SECURITY_LEVELS.map(l => (
              <option key={l.value} value={l.value}>
                {l.label}
              </option>
            ))}
          </select>
          <p className="text-xs text-gray-400 mt-1">Restrict logins during maintenance or testing.</p>
        </div>
      </div>

      <div className="mt-4">
        <label className="block text-sm font-medium text-gray-700 mb-2">Flags</label>
        <div className="flex flex-wrap gap-4">
          <label className="flex items-center gap-2 text-sm text-gray-600 cursor-pointer">
            <input
              type="checkbox"
              checked={hasFlag(flags, REALM_FLAG_RECOMMENDED)}
              onChange={e => setFlags(f => setFlag(f, REALM_FLAG_RECOMMENDED, e.target.checked))}
              className="rounded"
            />
            Recommended
          </label>
          <label className="flex items-center gap-2 text-sm text-gray-600 cursor-pointer">
            <input
              type="checkbox"
              checked={hasFlag(flags, REALM_FLAG_NEW_PLAYERS)}
              onChange={e => setFlags(f => setFlag(f, REALM_FLAG_NEW_PLAYERS, e.target.checked))}
              className="rounded"
            />
            New Players
          </label>
          <label className="flex items-center gap-2 text-sm text-gray-600 cursor-pointer">
            <input
              type="checkbox"
              checked={hasFlag(flags, REALM_FLAG_OFFLINE)}
              onChange={e => setFlags(f => setFlag(f, REALM_FLAG_OFFLINE, e.target.checked))}
              className="rounded"
            />
            Offline (hide from realm list)
          </label>
        </div>
      </div>

      {errorMsg && (
        <div className="mt-4 flex items-center gap-2 text-sm text-red-600">
          <AlertCircle className="w-4 h-4" />
          {errorMsg}
        </div>
      )}

      <div className="mt-5 flex items-center gap-3">
        <button
          onClick={handleSave}
          disabled={!dirty || updateRealm.isPending}
          className="inline-flex items-center gap-1.5 px-4 py-2 bg-blue-600 text-white rounded-md text-sm font-medium hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {updateRealm.isPending ? (
            <Loader2 className="w-4 h-4 animate-spin" />
          ) : (
            <Save className="w-4 h-4" />
          )}
          Save changes
        </button>
        {saved && (
          <span className="inline-flex items-center gap-1 text-sm text-green-600">
            <Check className="w-4 h-4" /> Saved
          </span>
        )}
        {realm.id === 1 && (
          <span className="text-xs text-gray-400">
            Name/type changes apply immediately; some client-side details refresh after a stack restart.
          </span>
        )}
      </div>
      </div>
      )}
    </div>
  )
}
