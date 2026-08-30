import { useMemo, useState } from 'react'
import { Loader2, Trash2, Save } from 'lucide-react'
import { usePublishedMpqs, useSetMpqRemovals } from '@/hooks/usePatches'

interface MpqRemovalPanelProps {
  stackId: string
  patchKey: string
  /** Removals currently saved on this patch (from the patch detail). */
  removals: string[]
}

function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value < 10 && unit > 0 ? 1 : 0)} ${units[unit]}`
}

/**
 * Lets a patch author pick which already-published client MPQs the patch should remove on apply.
 * The removal runs before the patch publishes any new MPQ files, so a patch can cleanly retire or
 * replace an archive. The reserved patch-D.MPQ (auto-built from DBCs) is shown but not selectable.
 */
export default function MpqRemovalPanel({ stackId, patchKey, removals }: MpqRemovalPanelProps) {
  const { data: published, isLoading, error } = usePublishedMpqs(stackId)
  const setMutation = useSetMpqRemovals(stackId)

  // Local selection: null until the user first edits, then the working set of names to remove.
  const [draft, setDraft] = useState<string[] | null>(null)

  const savedSet = useMemo(() => new Set(removals.map((n) => n.toLowerCase())), [removals])
  const selected = draft ?? removals
  const selectedSet = useMemo(() => new Set(selected.map((n) => n.toLowerCase())), [selected])

  const removable = useMemo(() => (published ?? []).filter((m) => !m.isReserved), [published])

  const dirty = useMemo(() => {
    if (draft === null) return false
    if (draft.length !== savedSet.size) return true
    return draft.some((n) => !savedSet.has(n.toLowerCase()))
  }, [draft, savedSet])

  // Names selected for removal that are no longer published (e.g. removed by an earlier patch);
  // surface them so the user understands the saved selection and can clear it.
  const orphanRemovals = useMemo(() => {
    const names = new Set((published ?? []).map((m) => m.name.toLowerCase()))
    return selected.filter((n) => !names.has(n.toLowerCase()))
  }, [published, selected])

  const toggle = (name: string) => {
    const base = draft ?? removals
    const exists = base.some((n) => n.toLowerCase() === name.toLowerCase())
    setDraft(exists ? base.filter((n) => n.toLowerCase() !== name.toLowerCase()) : [...base, name])
  }

  const save = async () => {
    if (draft === null) return
    await setMutation.mutateAsync({ patchKey, fileNames: draft })
    setDraft(null)
  }

  return (
    <div className="mt-3 rounded-md border border-gray-200 bg-gray-50 p-3">
      <div className="flex items-center justify-between">
        <h5 className="flex items-center gap-2 text-sm font-semibold text-gray-700">
          <Trash2 className="h-4 w-4 text-gray-500" />
          Remove published MPQs on apply
          {selectedSet.size > 0 && (
            <span className="rounded bg-red-100 px-1.5 py-0.5 text-xs font-medium text-red-700">
              {selectedSet.size} selected
            </span>
          )}
        </h5>
        {dirty && (
          <button
            type="button"
            onClick={save}
            disabled={setMutation.isPending}
            className="inline-flex items-center gap-1 rounded-md bg-blue-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {setMutation.isPending ? (
              <Loader2 className="h-3.5 w-3.5 animate-spin" />
            ) : (
              <Save className="h-3.5 w-3.5" />
            )}
            Save selection
          </button>
        )}
      </div>

      <p className="mt-1 text-xs text-gray-500">
        Selected archives are deleted from the client overlay <span className="font-medium">before</span>{' '}
        this patch publishes its own MPQ files.
      </p>

      {setMutation.isError && (
        <div className="mt-2 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
          Failed to save the removal selection.
        </div>
      )}

      {isLoading ? (
        <div className="mt-3 flex items-center gap-2 text-xs text-gray-500">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading published MPQs…
        </div>
      ) : error ? (
        <div className="mt-2 text-xs text-red-600">Could not load published MPQs.</div>
      ) : (published?.length ?? 0) === 0 ? (
        <p className="mt-3 text-xs text-gray-400">No MPQ files have been published to this stack yet.</p>
      ) : (
        <ul className="mt-3 space-y-1">
          {published!.map((mpq) => {
            const checked = selectedSet.has(mpq.name.toLowerCase())
            return (
              <li
                key={mpq.name}
                className={`flex items-center justify-between rounded-md border px-3 py-1.5 text-sm ${
                  checked ? 'border-red-200 bg-red-50' : 'border-gray-200 bg-white'
                }`}
              >
                <label
                  className={`flex min-w-0 items-center gap-2 ${
                    mpq.isReserved ? 'cursor-not-allowed opacity-60' : 'cursor-pointer'
                  }`}
                >
                  <input
                    type="checkbox"
                    checked={checked}
                    disabled={mpq.isReserved}
                    onChange={() => toggle(mpq.name)}
                    className="h-4 w-4 rounded border-gray-300 text-red-600 focus:ring-red-500 disabled:opacity-50"
                  />
                  <span className={`truncate font-mono ${checked ? 'text-red-700 line-through' : ''}`}>
                    {mpq.name}
                  </span>
                  {mpq.isReserved && (
                    <span className="shrink-0 rounded bg-gray-100 px-1.5 py-0.5 text-xs text-gray-500">
                      auto-managed
                    </span>
                  )}
                </label>
                <span className="shrink-0 text-xs text-gray-400">{formatBytes(mpq.size)}</span>
              </li>
            )
          })}
        </ul>
      )}

      {orphanRemovals.length > 0 && (
        <div className="mt-2 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
          Marked for removal but no longer published:{' '}
          <span className="font-mono">{orphanRemovals.join(', ')}</span>. Deselect to clean up.
          {orphanRemovals.map((name) => (
            <button
              key={name}
              type="button"
              onClick={() => toggle(name)}
              className="ml-2 inline underline hover:text-amber-900"
            >
              remove “{name}”
            </button>
          ))}
        </div>
      )}

      {removable.length === 0 && (published?.length ?? 0) > 0 && (
        <p className="mt-2 text-xs text-gray-400">Only the auto-managed patch-D.MPQ is published; nothing to remove.</p>
      )}
    </div>
  )
}
