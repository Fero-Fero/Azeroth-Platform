import { useEffect, useState } from 'react'
import { CheckCircle2, Loader2, Save } from 'lucide-react'
import { useSavePatchLauncherTheme } from '@/hooks/usePatches'
import type { PatchDetailsDto } from '@/types/patch.types'
import { apiErrorMessage } from '@/lib/utils'

const LAUNCHER_THEMES = [
  { value: '', label: 'None' },
  { value: 'classic', label: 'Classic' },
  { value: 'tbc', label: 'The Burning Crusade' },
  { value: 'wotlk', label: 'Wrath of the Lich King' },
]

interface PatchLauncherThemePanelProps {
  stackId: string
  patchKey: string
  detail?: PatchDetailsDto | null
}

export default function PatchLauncherThemePanel({
  stackId,
  patchKey,
  detail,
}: PatchLauncherThemePanelProps) {
  const saveMutation = useSavePatchLauncherTheme(stackId)
  const [theme, setTheme] = useState('')
  const [saved, setSaved] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  useEffect(() => {
    setTheme(detail?.launcherTheme ?? '')
    setSaveError(null)
    setSaved(false)
  }, [detail?.launcherTheme, patchKey])

  const baseline = detail?.launcherTheme ?? ''
  const dirty = theme !== baseline

  const handleSave = async () => {
    if (!theme) {
      setSaveError('Select a theme (classic, tbc, or wotlk). Remove config/launcher.json manually if needed.')
      return
    }

    setSaveError(null)
    try {
      await saveMutation.mutateAsync({ patchKey, theme })
      setSaved(true)
      setTimeout(() => setSaved(false), 3000)
    } catch (err) {
      setSaveError(apiErrorMessage(err))
    }
  }

  return (
    <section className="rounded-lg border border-gray-200 bg-white p-4 space-y-3">
      <div>
        <h4 className="text-sm font-semibold text-gray-900">Launcher theme</h4>
        <p className="mt-1 text-xs text-gray-500">
          From <span className="font-mono">config/launcher.json</span>. Applied to the stack launcher
          visual theme when this patch is applied. Synced from Azeroth-Platform-Progression on entry
          patches (1.0, 2.0, 3.0).
        </p>
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <label className="block min-w-[12rem]">
          <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Theme</span>
          <select
            className="mt-1.5 w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-blue-300"
            value={theme}
            onChange={(e) => setTheme(e.target.value)}
          >
            {LAUNCHER_THEMES.map((option) => (
              <option key={option.value || 'none'} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </label>

        <button
          type="button"
          onClick={() => void handleSave()}
          disabled={!dirty || saveMutation.isPending || !theme}
          className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
        >
          {saveMutation.isPending ? (
            <Loader2 className="h-4 w-4 animate-spin" />
          ) : (
            <Save className="h-4 w-4" />
          )}
          Save theme
        </button>

        {detail?.hasLauncherTheme && !dirty && (
          <span className="text-xs text-gray-500">
            Current: <span className="font-mono">{detail.launcherTheme}</span>
          </span>
        )}
        {saved && (
          <span className="inline-flex items-center gap-1 text-sm text-green-600">
            <CheckCircle2 className="h-4 w-4" /> Saved
          </span>
        )}
      </div>

      {saveError && (
        <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{saveError}</div>
      )}
    </section>
  )
}

interface PatchNewsFilesPanelProps {
  files: { name: string; size: number }[]
}

export function PatchNewsFilesPanel({ files }: PatchNewsFilesPanelProps) {
  if (files.length === 0) {
    return (
      <section className="rounded-lg border border-dashed border-gray-300 bg-gray-50/80 p-4">
        <h4 className="text-sm font-semibold text-gray-700">News files</h4>
        <p className="mt-1 text-xs text-gray-500">
          No <span className="font-mono">news/</span> files yet. Create an article on the News tab or
          run progression sync to import from Azeroth-Platform-Progression.
        </p>
      </section>
    )
  }

  return (
    <section className="rounded-lg border border-gray-200 bg-white p-4 space-y-3">
      <div>
        <h4 className="text-sm font-semibold text-gray-900">News files</h4>
        <p className="mt-1 text-xs text-gray-500">
          Bundled player-facing assets under <span className="font-mono">news/</span>. Imported on
          progression sync; edit content on the News tab.
        </p>
      </div>
      <ul className="divide-y divide-gray-100 rounded-md border border-gray-200">
        {files.map((file) => (
          <li
            key={file.name}
            className="flex items-center justify-between gap-3 px-3 py-2 text-sm font-mono text-gray-700"
          >
            <span className="truncate">{file.name}</span>
            <span className="shrink-0 text-xs text-gray-400 tabular-nums">
              {(file.size / 1024).toFixed(file.size >= 1024 ? 0 : 1)} KB
            </span>
          </li>
        ))}
      </ul>
    </section>
  )
}
