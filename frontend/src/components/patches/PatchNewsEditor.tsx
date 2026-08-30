import { useEffect, useMemo, useState } from 'react'
import { CheckCircle2, Eye, Loader2, Lock, Save, Upload } from 'lucide-react'
import {
  usePatchNewsPreview,
  useSavePatchNews,
  useUploadPatchNewsCover,
} from '@/hooks/usePatches'
import { useAuthenticatedImageUrl } from '@/hooks/usePatchNewsPreviewMedia'
import { NEWS_TAGS } from '@/components/launcher/NewsEditor'
import type { PatchStatus } from '@/types/patch.types'
import { apiErrorMessage } from '@/lib/utils'

export interface PatchNewsDraft {
  id: string
  title: string
  date: string
  tag: string
  sortOrder: number
  html: string
}

function todayIsoDate(): string {
  return new Date().toISOString().slice(0, 10)
}

function defaultDraftFromPatchKey(patchKey: string): PatchNewsDraft {
  const slug = patchKey
    .replace(/^patch\s+/i, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
  return {
    id: slug ? `progression-${slug}` : 'progression-patch',
    title: '',
    date: todayIsoDate(),
    tag: 'patch',
    sortOrder: 0,
    html: '<p></p>',
  }
}

interface PatchNewsEditorProps {
  stackId: string
  patchKey: string
  patchStatus?: PatchStatus
  onPreview: () => void
}

export default function PatchNewsEditor({
  stackId,
  patchKey,
  patchStatus,
  onPreview,
}: PatchNewsEditorProps) {
  const { data: preview, isLoading, isError, error } = usePatchNewsPreview(stackId, patchKey, true)
  const saveMutation = useSavePatchNews(stackId)
  const uploadCoverMutation = useUploadPatchNewsCover(stackId)

  const [draft, setDraft] = useState<PatchNewsDraft>(() => defaultDraftFromPatchKey(patchKey))
  const [initialized, setInitialized] = useState(false)
  const [saved, setSaved] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [coverBust, setCoverBust] = useState(0)

  const isDateLocked = preview?.dateLocked ?? patchStatus === 'Applied'
  const displayDate = isDateLocked ? draft.date : todayIsoDate()

  useEffect(() => {
    setInitialized(false)
    setDraft(defaultDraftFromPatchKey(patchKey))
    setSaveError(null)
    setSaved(false)
  }, [patchKey])

  useEffect(() => {
    if (initialized || isLoading || !preview) {
      return
    }

    if (preview.available) {
      setDraft({
        id: preview.id ?? defaultDraftFromPatchKey(patchKey).id,
        title: preview.title ?? '',
        date: preview.date ?? todayIsoDate(),
        tag: preview.tag ?? 'patch',
        sortOrder: 0,
        html: preview.html ?? '',
      })
    } else {
      setDraft(defaultDraftFromPatchKey(patchKey))
    }

    setInitialized(true)
  }, [initialized, isLoading, patchKey, preview])

  const baseline = useMemo(() => {
    if (!preview?.available) {
      return defaultDraftFromPatchKey(patchKey)
    }

    return {
      id: preview.id ?? '',
      title: preview.title ?? '',
      date: preview.date ?? '',
      tag: preview.tag ?? 'patch',
      sortOrder: 0,
      html: preview.html ?? '',
    }
  }, [patchKey, preview])

  const dirty =
    initialized &&
    (draft.id !== baseline.id ||
      draft.title !== baseline.title ||
      draft.tag !== baseline.tag ||
      draft.html !== baseline.html)

  const coverApiUrl =
    preview?.hasCover && preview.coverUrl
      ? `${preview.coverUrl}${preview.coverUrl.includes('?') ? '&' : '?'}v=${coverBust}`
      : null
  const coverUrl = useAuthenticatedImageUrl(coverApiUrl, !!coverApiUrl)

  const handleSave = async () => {
    setSaveError(null)
    try {
      await saveMutation.mutateAsync({ patchKey, ...draft, date: displayDate })
      setSaved(true)
      setTimeout(() => setSaved(false), 3000)
    } catch (err) {
      setSaveError(apiErrorMessage(err))
    }
  }

  const handleCoverUpload = async (file?: File | null) => {
    if (!file) {
      return
    }

    setSaveError(null)
    try {
      await uploadCoverMutation.mutateAsync({ patchKey, file })
      setCoverBust((value) => value + 1)
    } catch (err) {
      setSaveError(apiErrorMessage(err))
    }
  }

  if (isLoading && !initialized) {
    return (
      <div className="flex items-center justify-center gap-2 py-12 text-sm text-gray-500">
        <Loader2 className="h-4 w-4 animate-spin" />
        Loading news article…
      </div>
    )
  }

  if (isError) {
    return (
      <p className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
        {error instanceof Error ? error.message : 'Failed to load patch news.'}
      </p>
    )
  }

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h4 className="text-sm font-semibold text-gray-900">Player-facing news article</h4>
          <p className="mt-1 text-xs text-gray-500">
            Saved to <span className="font-mono">news/article.json</span> and{' '}
            <span className="font-mono">news/article.html</span>. Published to the launcher and armory
            when this patch is applied.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <button
            type="button"
            onClick={() => void handleSave()}
            disabled={!dirty || saveMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {saveMutation.isPending ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <Save className="h-4 w-4" />
            )}
            Save news article
          </button>
          <button
            type="button"
            onClick={onPreview}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
          >
            <Eye className="h-4 w-4" />
            Preview
          </button>
          {saved && (
            <span className="inline-flex items-center gap-1 text-sm text-green-600">
              <CheckCircle2 className="h-4 w-4" /> Saved
            </span>
          )}
          {dirty && !saveMutation.isPending && (
            <span className="text-sm text-gray-400">Unsaved changes</span>
          )}
        </div>
      </div>

      {saveError && (
        <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{saveError}</div>
      )}

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <label className="block">
          <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Article id</span>
          <input
            className="mt-1.5 w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-sm focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-blue-300"
            value={draft.id}
            onChange={(e) => setDraft((prev) => ({ ...prev, id: e.target.value }))}
          />
        </label>
        <label className="block">
          <span className="inline-flex items-center gap-1 text-xs font-semibold uppercase tracking-wide text-gray-500">
            Date
            {isDateLocked && <Lock className="h-3 w-3 text-gray-400" aria-hidden />}
          </span>
          <input
            readOnly
            disabled={isDateLocked}
            className="mt-1.5 w-full rounded-md border border-gray-300 bg-gray-50 px-3 py-2 text-sm text-gray-700 disabled:cursor-not-allowed"
            value={displayDate}
          />
          <span className="mt-1 block text-xs text-gray-400">
            {isDateLocked
              ? 'Locked to the date set when this patch was applied.'
              : 'Uses today\u2019s date on save and when the patch is applied.'}
          </span>
        </label>
      </div>

      <label className="block">
        <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Headline</span>
        <input
          className="mt-1.5 w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-blue-300"
          value={draft.title}
          onChange={(e) => setDraft((prev) => ({ ...prev, title: e.target.value }))}
          placeholder="Fire and Shadow: Molten Core and Onyxia Now Live"
        />
      </label>

      <label className="block sm:max-w-xs">
        <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Tag</span>
        <select
          className="mt-1.5 w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-blue-300"
          value={draft.tag}
          onChange={(e) => setDraft((prev) => ({ ...prev, tag: e.target.value }))}
        >
          {NEWS_TAGS.filter((tag) => tag.value).map((tag) => (
            <option key={tag.value} value={tag.value}>
              {tag.label}
            </option>
          ))}
        </select>
      </label>

      <div>
        <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Cover image</span>
        <div className="mt-1.5 flex flex-wrap items-center gap-3">
          <label className="inline-flex cursor-pointer items-center gap-2 rounded-md border border-gray-300 px-3 py-2 text-sm hover:bg-gray-50">
            <Upload className="h-4 w-4" />
            Upload cover
            <input
              type="file"
              accept="image/*"
              className="hidden"
              onChange={(e) => void handleCoverUpload(e.target.files?.[0])}
            />
          </label>
          {uploadCoverMutation.isPending && (
            <Loader2 className="h-4 w-4 animate-spin text-gray-400" />
          )}
          {coverUrl ? (
            <img src={coverUrl} alt="" className="aspect-video h-16 rounded-md object-cover ring-1 ring-gray-200" />
          ) : (
            <span className="text-sm text-gray-400">No cover uploaded</span>
          )}
        </div>
      </div>

      <label className="block">
        <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">
          Article body (<span className="font-mono normal-case">article.html</span>)
        </span>
        <textarea
          value={draft.html}
          onChange={(e) => setDraft((prev) => ({ ...prev, html: e.target.value }))}
          rows={18}
          spellCheck={false}
          className="mt-1.5 w-full resize-y rounded-md border border-gray-300 bg-gray-50/50 p-3 font-mono text-xs leading-relaxed text-gray-800 focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-blue-300"
        />
      </label>
    </div>
  )
}
