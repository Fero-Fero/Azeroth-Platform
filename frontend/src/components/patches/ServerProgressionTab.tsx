import { useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle2, Loader2, RefreshCw, Save } from 'lucide-react'
import {
  useDiscoverIndividualProgressionKeys,
  useIndividualProgressionSettings,
  useSaveIndividualProgressionSettings,
} from '@/hooks/usePatches'
import type { IndividualProgressionSettings } from '@/types/individual-progression.types'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

interface ServerProgressionTabProps {
  stackId: string
  bootstrapped: boolean
}

const KEY_FIELDS: { label: string; field: keyof IndividualProgressionSettings['keys'] }[] = [
  { label: 'Starting progression', field: 'startingProgression' },
  { label: 'Progression limit', field: 'progressionLimit' },
  { label: 'TBC races unlock progression', field: 'tbcRacesUnlockProgression' },
  { label: 'TBC races starting progression', field: 'tbcRacesStartingProgression' },
]

export default function ServerProgressionTab({ stackId, bootstrapped }: ServerProgressionTabProps) {
  const { data: settings, isLoading, error } = useIndividualProgressionSettings(stackId)
  const saveMutation = useSaveIndividualProgressionSettings(stackId)
  const discoverMutation = useDiscoverIndividualProgressionKeys(stackId)
  const [draft, setDraft] = useState<IndividualProgressionSettings | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)

  useEffect(() => {
    if (settings) {
      setDraft(structuredClone(settings))
    }
  }, [settings])

  if (isLoading || !draft) {
    return (
      <div className="flex items-center justify-center gap-2 py-16 text-sm text-gray-500">
        <Loader2 className="h-5 w-5 animate-spin" />
        Loading server progression settings…
      </div>
    )
  }

  if (error) {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 px-5 py-4 text-sm text-red-800">
        {errorMessage(error)}
      </div>
    )
  }

  const dirty = JSON.stringify(draft) !== JSON.stringify(settings)

  const updateKey = (field: keyof IndividualProgressionSettings['keys'], value: string) => {
    setDraft((current) =>
      current
        ? {
            ...current,
            keys: { ...current.keys, [field]: value },
          }
        : current
    )
  }

  const updateValue = (key: string, value: string) => {
    setDraft((current) =>
      current
        ? {
            ...current,
            values: { ...current.values, [key]: value },
          }
        : current
    )
  }

  const handleSave = async () => {
    setActionError(null)
    setNotice(null)
    try {
      await saveMutation.mutateAsync(draft)
      setNotice('Settings saved and written to config files.')
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

  const handleDiscover = async () => {
    setActionError(null)
    setNotice(null)
    try {
      const res = await discoverMutation.mutateAsync()
      setDraft(res.data)
      setNotice('Conf keys re-scanned from module config.')
    } catch (err) {
      setActionError(errorMessage(err))
    }
  }

  const valueRows = [
    { label: 'Expansion (worldserver)', key: 'Expansion', configKey: draft.expansionKey },
    { label: 'Starting progression', key: draft.keys.startingProgression },
    { label: 'Progression limit', key: draft.keys.progressionLimit },
    { label: 'TBC races unlock', key: draft.keys.tbcRacesUnlockProgression },
    { label: 'TBC races starting', key: draft.keys.tbcRacesStartingProgression },
  ]

  return (
    <div className="space-y-5">
      <section className="rounded-lg border border-gray-200 bg-white p-5 shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h3 className="text-lg font-semibold text-gray-900">Server Progression</h3>
            <p className="mt-1 max-w-2xl text-sm text-gray-500">
              Map Individual Progression conf keys and edit live values. Keys are discovered from the
              module <code className="rounded bg-gray-100 px-1 text-xs">.conf</code> file on first open.
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={handleDiscover}
              disabled={discoverMutation.isPending}
              className="inline-flex items-center gap-2 rounded-md border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
            >
              {discoverMutation.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <RefreshCw className="h-4 w-4" />
              )}
              Re-scan keys
            </button>
            <button
              type="button"
              onClick={handleSave}
              disabled={!dirty || saveMutation.isPending}
              className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
            >
              {saveMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              Save
            </button>
          </div>
        </div>

        <div className="mt-4 flex flex-wrap items-center gap-3 text-sm">
          <span
            className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-medium ${
              bootstrapped || draft.bootstrapped
                ? 'bg-green-100 text-green-800'
                : 'bg-amber-100 text-amber-800'
            }`}
          >
            {bootstrapped || draft.bootstrapped ? (
              <CheckCircle2 className="h-3.5 w-3.5" />
            ) : (
              <AlertTriangle className="h-3.5 w-3.5" />
            )}
            {bootstrapped || draft.bootstrapped ? 'Progression prepared' : 'Not bootstrapped yet'}
          </span>
          <span className="text-xs text-gray-500">
            Module conf: <code className="font-mono">{draft.moduleConfPath}</code>
          </span>
          <span className="text-xs text-gray-500">
            Worldserver: <code className="font-mono">{draft.worldserverConfPath}</code>
          </span>
        </div>

        {notice && (
          <p className="mt-3 rounded-md border border-green-200 bg-green-50 px-3 py-2 text-sm text-green-800">
            {notice}
          </p>
        )}
        {actionError && (
          <p className="mt-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
            {actionError}
          </p>
        )}
      </section>

      <section className="rounded-lg border border-gray-200 bg-white p-5 shadow-sm space-y-4">
        <h4 className="text-sm font-semibold text-gray-900">Conf key mapping</h4>
        <div className="grid gap-4 sm:grid-cols-2">
          <label className="block text-sm">
            <span className="mb-1 block text-xs font-medium text-gray-500">Expansion key (worldserver)</span>
            <input
              type="text"
              value={draft.expansionKey}
              onChange={(e) => setDraft({ ...draft, expansionKey: e.target.value })}
              className="w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-sm"
            />
          </label>
          {KEY_FIELDS.map(({ label, field }) => (
            <label key={field} className="block text-sm">
              <span className="mb-1 block text-xs font-medium text-gray-500">{label}</span>
              <input
                type="text"
                value={draft.keys[field]}
                onChange={(e) => updateKey(field, e.target.value)}
                className="w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-sm"
              />
            </label>
          ))}
        </div>
      </section>

      <section className="rounded-lg border border-gray-200 bg-white p-5 shadow-sm space-y-4">
        <h4 className="text-sm font-semibold text-gray-900">Current values</h4>
        <div className="grid gap-4 sm:grid-cols-2">
          {valueRows.map(({ label, key }) => (
            <label key={key} className="block text-sm">
              <span className="mb-1 block text-xs font-medium text-gray-500">{label}</span>
              <input
                type="text"
                value={draft.values[key] ?? ''}
                onChange={(e) => updateValue(key, e.target.value)}
                className="w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-sm"
              />
              <span className="mt-0.5 block font-mono text-[10px] text-gray-400">{key}</span>
            </label>
          ))}
        </div>
      </section>
    </div>
  )
}
