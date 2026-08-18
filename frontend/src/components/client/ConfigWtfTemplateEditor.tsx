import { useState } from 'react'
import { Loader2, Save, CheckCircle2, FileCog } from 'lucide-react'
import { useStackConfigTemplate, useSaveStackConfigTemplate } from '@/hooks/useLauncher'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

/**
 * Editor for the stack's WTF/Config.wtf settings template (the file the launcher seeds on a player's
 * first install). Placeholders {{HOST}}/{{PORT}} are substituted per launcher when served. After the
 * first install Config.wtf is player-owned - later launches only patch the realmlist line - so edits
 * here affect fresh installs (or installs where the player deleted Config.wtf).
 */
export default function ConfigWtfTemplateEditor({ stackId }: { stackId: string }) {
  const { data, isLoading, error } = useStackConfigTemplate(stackId)
  const save = useSaveStackConfigTemplate(stackId)

  // `draft` is null until the admin edits, so the textarea follows the server value until then and no
  // effect/setState is needed to seed it.
  const [draft, setDraft] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  const value = draft ?? data ?? ''
  const dirty = draft !== null && data !== undefined && draft !== data

  const onSave = () => {
    setSaveError(null)
    save.mutate(value, {
      onSuccess: () => {
        setDraft(null)
        setSaved(true)
        setTimeout(() => setSaved(false), 3000)
      },
      onError: (err) => setSaveError(errorMessage(err)),
    })
  }

  return (
    <section className="rounded-lg border bg-white p-6 shadow-sm">
      <div className="mb-1 flex items-center gap-2">
        <FileCog className="h-5 w-5 text-blue-600" />
        <h2 className="text-lg font-semibold text-gray-900">Config.wtf template</h2>
      </div>
      <p className="mb-4 text-sm text-gray-500">
        Seeds <span className="font-mono">WTF/Config.wtf</span> on a player&rsquo;s first install. Use{' '}
        <span className="font-mono">{'{{HOST}}'}</span> and <span className="font-mono">{'{{PORT}}'}</span>{' '}
        for the realmlist address. After first install Config.wtf is player-owned &mdash; later launches
        only update the <span className="font-mono">SET realmList</span> line &mdash; so changes here apply
        to fresh installs.
      </p>

      {error ? (
        <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{errorMessage(error)}</div>
      ) : isLoading || data === undefined ? (
        <div className="flex items-center justify-center py-10 text-gray-400">
          <Loader2 className="h-5 w-5 animate-spin" />
        </div>
      ) : (
        <>
          <textarea
            value={value}
            onChange={(e) => setDraft(e.target.value)}
            spellCheck={false}
            rows={10}
            className="w-full resize-y rounded-md border border-gray-300 bg-gray-50/50 p-3 font-mono text-sm text-gray-800 focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-blue-300"
          />
          {saveError && <div className="mt-2 rounded-md bg-red-50 p-3 text-sm text-red-700">{saveError}</div>}
          <div className="mt-3 flex items-center gap-3">
            <button
              onClick={onSave}
              disabled={!dirty || save.isPending}
              className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
            >
              {save.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              Save template
            </button>
            {saved && (
              <span className="inline-flex items-center gap-1 text-sm text-green-600">
                <CheckCircle2 className="h-4 w-4" /> Saved
              </span>
            )}
            {dirty && !save.isPending && <span className="text-sm text-gray-400">Unsaved changes</span>}
          </div>
        </>
      )}
    </section>
  )
}
