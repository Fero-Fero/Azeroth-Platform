import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useEditor, EditorContent } from '@tiptap/react'
import StarterKit from '@tiptap/starter-kit'
import Image from '@tiptap/extension-image'
import DOMPurify from 'dompurify'
import {
  Bold,
  Italic,
  Heading2,
  Heading3,
  List,
  ListOrdered,
  Quote,
  Minus,
  Link2,
  ImagePlus,
  Plus,
  Trash2,
  ChevronUp,
  ChevronDown,
  Save,
  Loader2,
  CheckCircle2,
  Upload,
  AlertCircle,
  Newspaper,
  Eye,
  Pencil,
} from 'lucide-react'
import type { LauncherNewsItemDto } from '@/types/launcher.types'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import { NEWS_ARTICLE_TEMPLATES, type NewsArticleTemplate } from './newsTemplates'
import './newsContent.css'

/** Selectable content tags (rendered as a colored corner ribbon on the news cards). */
export const NEWS_TAGS: { value: string; label: string }[] = [
  { value: '', label: 'None' },
  { value: 'patch', label: 'Patch' },
  { value: 'announcement', label: 'Announcement' },
  { value: 'expansion', label: 'Expansion' },
  { value: 'event', label: 'Event' },
  { value: 'update', label: 'Update' },
  { value: 'hotfix', label: 'Hotfix' },
]

/** Ribbon/chip color per tag; kept in sync with the armory's theme.css ribbon colors. */
export const NEWS_TAG_COLORS: Record<string, string> = {
  patch: '#2f7dd1',
  announcement: '#c8952f',
  expansion: '#7a3fb0',
  event: '#2e9e5b',
  update: '#2aa198',
  hotfix: '#c0392b',
}

interface NewsEditorProps {
  /** Server-canonical items (used to seed the working draft and after a save). */
  items: LauncherNewsItemDto[]
  onSave: (items: LauncherNewsItemDto[]) => Promise<LauncherNewsItemDto[]>
  onUploadImage: (itemId: string, file: File) => Promise<unknown>
  /** Builds the cover-image URL for an item id (differs global vs per-stack). */
  imageUrlFor: (itemId: string) => string
  isSaving?: boolean
  accentColor?: string
  /** Optional page title shown above the editor chrome. */
  pageTitle?: string
  /** Optional subtitle / help text under the title. */
  pageDescription?: string
  /** Optional info banner (e.g. global broadcast notice). */
  infoBanner?: ReactNode
}

function newId(): string {
  const raw = globalThis.crypto?.randomUUID?.() ?? Math.random().toString(36).slice(2)
  return raw.replace(/-/g, '')
}

function todayIso(): string {
  return new Date().toISOString().slice(0, 10)
}

/**
 * Stable serialization of the editable fields, used to detect unsaved changes (dirty state) for
 * autosave and the navigation guard. `hasImage` is included so uploading/removing a cover counts as a
 * change (the Save button enables and autosave/push fires); the cache-busting `imageUrl` is excluded
 * since it can differ from the server's value and would otherwise mark the editor perpetually dirty.
 */
function serializeNews(items: LauncherNewsItemDto[]): string {
  return JSON.stringify(
    items.map((i) => ({
      id: i.id,
      title: i.title,
      date: i.date,
      html: i.html,
      sortOrder: i.sortOrder,
      isDraft: !!i.isDraft,
      tag: i.tag ?? '',
      hasImage: !!i.hasImage,
    })),
  )
}

/** Debounce (ms) of inactivity before an automatic save fires. */
const AUTOSAVE_DELAY_MS = 1500

export default function NewsEditor({
  items,
  onSave,
  onUploadImage,
  imageUrlFor,
  isSaving,
  accentColor = '#4fa8d8',
  pageTitle,
  pageDescription,
  infoBanner,
}: NewsEditorProps) {
  const [draft, setDraft] = useState<LauncherNewsItemDto[]>(items)
  const [selectedId, setSelectedId] = useState<string | null>(items[0]?.id ?? null)
  const [detailTab, setDetailTab] = useState<'edit' | 'preview'>('edit')
  const [bust, setBust] = useState(0)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Snapshot of the last-saved state; anything different means there are unsaved changes.
  const savedSnapshotRef = useRef(serializeNews(items))
  const dirty = serializeNews(draft) !== savedSnapshotRef.current

  const selectedIdRef = useRef(selectedId)
  useEffect(() => {
    selectedIdRef.current = selectedId
  }, [selectedId])

  // Refs kept in sync for the unmount flush + beforeunload guard (which run outside render).
  const draftRef = useRef(draft)
  const dirtyRef = useRef(dirty)
  const onSaveRef = useRef(onSave)
  useEffect(() => {
    draftRef.current = draft
    dirtyRef.current = dirty
    onSaveRef.current = onSave
  }, [draft, dirty, onSave])

  const selected = draft.find((d) => d.id === selectedId) ?? null

  // Display order is by sortOrder descending (latest first). sortOrder is a stable per-article value,
  // so adding/reordering never has to reindex the whole list.
  const ordered = useMemo(() => [...draft].sort((a, b) => b.sortOrder - a.sortOrder), [draft])
  const publishedCount = useMemo(() => draft.filter((item) => !item.isDraft).length, [draft])
  const draftCount = useMemo(() => draft.filter((item) => item.isDraft).length, [draft])

  const selectArticle = (id: string) => {
    setSelectedId(id)
    setDetailTab('edit')
  }

  const editor = useEditor({
    // Defer view creation to an effect. Creating the ProseMirror view during React's render/commit
    // phase (the default) can trip React 19 into a flushSync-during-commit path that surfaces as a
    // "null.cached" crash on mount. Deferring avoids it (we don't server-side render this editor).
    immediatelyRender: false,
    extensions: [StarterKit, Image],
    content: selected?.html ?? '',
    editorProps: {
      attributes: { class: 'ProseMirror', 'data-placeholder': 'Write your article…' },
    },
    onUpdate: ({ editor }) => {
      const id = selectedIdRef.current
      if (!id) return
      const html = editor.getHTML()
      setDraft((prev) => prev.map((d) => (d.id === id ? { ...d, html } : d)))
    },
  })

  // Load the selected article's HTML into the editor when the selection changes.
  useEffect(() => {
    if (!editor) return
    const html = selected?.html ?? ''
    if (editor.getHTML() !== html) {
      editor.commands.setContent(html)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedId, editor])

  const patchSelected = (patch: Partial<LauncherNewsItemDto>) =>
    setDraft((prev) => prev.map((d) => (d.id === selectedId ? { ...d, ...patch } : d)))

  const addArticle = (tpl?: NewsArticleTemplate) => {
    // Give the new article the highest sortOrder so it sorts to the top (latest first) without
    // touching any existing article's sortOrder.
    const nextOrder = draft.reduce((max, d) => Math.max(max, d.sortOrder), -1) + 1
    const item: LauncherNewsItemDto = {
      id: newId(),
      title: tpl?.title ?? 'Untitled article',
      date: todayIso(),
      html: tpl?.html ?? '',
      tag: tpl?.tag ?? '',
      sortOrder: nextOrder,
      // New articles start as drafts so they aren't published to the launcher until the author
      // toggles them live. Autosave still persists the draft so work is never lost.
      isDraft: true,
      hasImage: false,
      imageUrl: null,
    }
    setDraft((prev) => [item, ...prev])
    setSelectedId(item.id)
    setDetailTab('edit')
  }

  const removeItem = (id: string) => {
    setDraft((prev) => {
      const next = prev.filter((d) => d.id !== id)
      if (selectedId === id) setSelectedId(next[0]?.id ?? null)
      return next
    })
  }

  // Reorder within the displayed (sortOrder-descending) list by swapping the two affected
  // articles' sortOrder values. Every other article is left untouched (no reindex).
  const move = (id: string, dir: -1 | 1) => {
    setDraft((prev) => {
      const order = [...prev].sort((a, b) => b.sortOrder - a.sortOrder)
      const idx = order.findIndex((d) => d.id === id)
      const target = idx + dir
      if (idx < 0 || target < 0 || target >= order.length) return prev
      const a = order[idx]
      const b = order[target]
      return prev.map((d) => {
        if (d.id === a.id) return { ...d, sortOrder: b.sortOrder }
        if (d.id === b.id) return { ...d, sortOrder: a.sortOrder }
        return d
      })
    })
  }

  const persist = async (opts?: { silent?: boolean }) => {
    setError(null)
    try {
      const result = await onSaveRef.current(draftRef.current)
      savedSnapshotRef.current = serializeNews(result)
      setDraft(result)
      if (!result.some((r) => r.id === selectedIdRef.current)) {
        setSelectedId(result[0]?.id ?? null)
      }
      if (!opts?.silent) {
        setSaved(true)
        setTimeout(() => setSaved(false), 2000)
      }
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  const save = () => persist()

  const onCover = async (file?: File | null) => {
    if (!file || !selected) return
    setError(null)
    try {
      await onUploadImage(selected.id, file)
      setBust((b) => b + 1)
      patchSelected({ hasImage: true, imageUrl: imageUrlFor(selected.id) })
      // The cover is already stored server-side, but the article's list entry (and its push to the
      // stack's armory/launcher) only updates on save. Persist immediately so the image shows right
      // away without the author having to make an unrelated edit to enable Save. This also covers
      // replacing an existing cover, where hasImage doesn't change so the dirty check wouldn't fire.
      await persist({ silent: true })
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  // Autosave: after a short lull in edits, persist automatically so drafts are never lost.
  useEffect(() => {
    if (!dirty || isSaving) return
    const timer = setTimeout(() => void persist({ silent: true }), AUTOSAVE_DELAY_MS)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft, dirty, isSaving])

  // Warn on hard navigation (refresh / tab close / external link) with unsaved changes. In-app
  // navigation is covered by the unmount flush below (autosave persists the latest draft).
  useEffect(() => {
    const handler = (e: BeforeUnloadEvent) => {
      if (!dirtyRef.current) return
      e.preventDefault()
      e.returnValue = ''
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [])

  // On unmount (e.g. navigating to another page/stack) flush any pending changes so nothing is lost.
  useEffect(() => {
    return () => {
      if (dirtyRef.current) {
        void onSaveRef.current(draftRef.current)
      }
    }
  }, [])

  const previewHtml = useMemo(
    () => (selected ? DOMPurify.sanitize(selected.html || '') : ''),
    [selected],
  )

  const coverUrl = selected?.imageUrl
    ? `${selected.imageUrl}${selected.imageUrl.includes('?') ? '&' : '?'}v=${bust}`
    : null

  const tbBtn = (active: boolean) =>
    `rounded p-1.5 text-gray-700 hover:bg-gray-100 ${active ? 'bg-gray-200 text-gray-900' : ''}`

  const renderArticleRow = (item: LauncherNewsItemDto, index: number) => {
    const selected = item.id === selectedId
    const tagColor = item.tag ? NEWS_TAG_COLORS[item.tag] ?? '#555' : null
    return (
      <div
        key={item.id}
        className={`flex items-stretch gap-1 border-l-4 transition-colors ${
          item.isDraft ? 'border-l-amber-400' : 'border-l-green-500'
        } ${selected ? 'bg-blue-50 ring-1 ring-inset ring-blue-200' : 'hover:bg-gray-50/90'}`}
      >
        <button
          type="button"
          className="flex min-w-0 flex-1 items-start gap-2 px-3 py-2.5 text-left"
          onClick={() => selectArticle(item.id)}
        >
          {item.imageUrl ? (
            <img
              src={`${item.imageUrl}?v=${bust}`}
              alt=""
              className="mt-0.5 h-10 w-14 flex-none rounded object-cover ring-1 ring-gray-200"
            />
          ) : (
            <span className="mt-0.5 flex h-10 w-14 flex-none items-center justify-center rounded bg-gray-100 text-[10px] text-gray-400 ring-1 ring-gray-200">
              No cover
            </span>
          )}
          <span className="min-w-0 flex-1">
            <span className="flex flex-wrap items-center gap-1.5">
              <span className="truncate text-sm font-medium text-gray-900">
                {item.title || 'Untitled'}
              </span>
              {item.isDraft && (
                <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-amber-800">
                  Draft
                </span>
              )}
              {item.tag && tagColor && (
                <span
                  className="rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-white"
                  style={{ backgroundColor: tagColor }}
                >
                  {item.tag}
                </span>
              )}
            </span>
            <span className="mt-0.5 block text-xs text-gray-500">{item.date || 'No date'}</span>
          </span>
        </button>
        <div className="flex flex-none flex-col justify-center gap-0.5 pr-1">
          <button
            type="button"
            onClick={() => move(item.id, -1)}
            disabled={index === 0}
            className="rounded p-0.5 text-gray-400 hover:bg-gray-100 hover:text-gray-700 disabled:opacity-30"
            title="Move up"
          >
            <ChevronUp className="h-3.5 w-3.5" />
          </button>
          <button
            type="button"
            onClick={() => move(item.id, 1)}
            disabled={index === ordered.length - 1}
            className="rounded p-0.5 text-gray-400 hover:bg-gray-100 hover:text-gray-700 disabled:opacity-30"
            title="Move down"
          >
            <ChevronDown className="h-3.5 w-3.5" />
          </button>
        </div>
        <button
          type="button"
          onClick={() => {
            if (window.confirm(`Delete "${item.title || 'Untitled'}"? This can't be undone.`)) {
              removeItem(item.id)
            }
          }}
          className="flex-none self-center px-2 text-gray-400 hover:text-red-600"
          title="Delete article"
        >
          <Trash2 className="h-4 w-4" />
        </button>
      </div>
    )
  }

  return (
    <div className="space-y-5">
      {(pageTitle || pageDescription) && (
        <div>
          {pageTitle && <h2 className="text-xl font-semibold text-gray-900">{pageTitle}</h2>}
          {pageDescription && <p className="mt-1 max-w-3xl text-sm text-gray-500">{pageDescription}</p>}
        </div>
      )}

      {infoBanner}

      {error && (
        <div className="rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {error}
        </div>
      )}

      <section className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-4 px-5 py-4">
          <div>
            <h3 className="text-sm font-semibold text-gray-900">News editor</h3>
            <p className="mt-0.5 text-xs text-gray-500">Create articles, set tags, and preview launcher cards.</p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={() => addArticle()}
              className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700"
            >
              <Plus className="h-4 w-4" /> New article
            </button>
            <select
              className="rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-700 hover:bg-gray-50"
              value=""
              onChange={(e) => {
                const tpl = NEWS_ARTICLE_TEMPLATES.find((t) => t.id === e.target.value)
                if (tpl) addArticle(tpl)
                e.target.value = ''
              }}
            >
              <option value="">From template…</option>
              {NEWS_ARTICLE_TEMPLATES.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.label}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="grid gap-px border-t border-gray-100 bg-gray-100 sm:grid-cols-2 lg:grid-cols-4">
          <div className="bg-white px-5 py-3">
            <p className="text-[11px] font-medium uppercase tracking-wide text-gray-400">Articles</p>
            <p className="mt-0.5 text-sm font-semibold text-gray-900">{draft.length}</p>
          </div>
          <div className="bg-white px-5 py-3">
            <p className="text-[11px] font-medium uppercase tracking-wide text-gray-400">Published</p>
            <p className="mt-0.5 text-sm font-semibold text-green-700">{publishedCount}</p>
          </div>
          <div className="bg-white px-5 py-3">
            <p className="text-[11px] font-medium uppercase tracking-wide text-gray-400">Drafts</p>
            <p className="mt-0.5 text-sm font-semibold text-amber-700">{draftCount}</p>
          </div>
          <div className="flex items-center justify-between gap-3 bg-white px-5 py-3">
            <div>
              <p className="text-[11px] font-medium uppercase tracking-wide text-gray-400">Save status</p>
              {isSaving ? (
                <p className="mt-0.5 inline-flex items-center gap-1 text-sm font-medium text-gray-600">
                  <Loader2 className="h-3.5 w-3.5 animate-spin" /> Saving…
                </p>
              ) : dirty ? (
                <p className="mt-0.5 inline-flex items-center gap-1 text-sm font-medium text-amber-700">
                  <AlertCircle className="h-3.5 w-3.5" /> Unsaved
                </p>
              ) : (
                <p className="mt-0.5 inline-flex items-center gap-1 text-sm font-medium text-green-700">
                  <CheckCircle2 className="h-3.5 w-3.5" /> {saved ? 'Saved' : 'Up to date'}
                </p>
              )}
            </div>
            <button
              type="button"
              onClick={save}
              disabled={isSaving || !dirty}
              className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 px-2.5 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
              title="Autosave keeps drafts saved; use this to save immediately"
            >
              {isSaving ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Save className="h-3.5 w-3.5" />}
              Save now
            </button>
          </div>
        </div>
      </section>

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-[minmax(280px,340px)_1fr]">
        <aside className="lg:sticky lg:top-4 lg:self-start">
          <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
            <div className="border-b border-gray-100 bg-gray-50 px-4 py-3">
              <h3 className="text-sm font-semibold text-gray-900">Article library</h3>
              <p className="mt-0.5 text-xs text-gray-500">Newest first · reorder with arrows</p>
            </div>
            <div className="max-h-[calc(100vh-14rem)] divide-y divide-gray-50 overflow-y-auto">
              {ordered.length === 0 ? (
                <div className="flex flex-col items-center gap-2 px-4 py-10 text-center">
                  <Newspaper className="h-8 w-8 text-gray-300" />
                  <p className="text-sm font-medium text-gray-600">No articles yet</p>
                  <p className="text-xs text-gray-400">Add one or start from a template</p>
                </div>
              ) : (
                ordered.map((item, index) => renderArticleRow(item, index))
              )}
            </div>
          </div>
        </aside>

        <main className="min-w-0">
          {!selected ? (
            <div className="flex h-72 flex-col items-center justify-center gap-2 rounded-lg border border-dashed border-gray-300 bg-gray-50/80 shadow-sm">
              <Newspaper className="h-8 w-8 text-gray-300" />
              <p className="text-sm font-medium text-gray-600">Select an article from the library</p>
              <p className="text-xs text-gray-400">Edit content, cover image, and launcher preview</p>
            </div>
          ) : (
            <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
              <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-100 bg-gray-50/80 px-5 py-4">
                <div className="min-w-0">
                  <h3 className="truncate text-lg font-semibold text-gray-900">
                    {selected.title || 'Untitled article'}
                  </h3>
                  <p className="mt-1 text-xs text-gray-500">
                    {selected.date || 'No date'}
                    {selected.isDraft ? ' · Draft (hidden from launcher)' : ' · Published'}
                  </p>
                </div>
                <label className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm">
                  <input
                    type="checkbox"
                    className="h-4 w-4 rounded border-gray-300"
                    checked={!!selected.isDraft}
                    onChange={(e) => patchSelected({ isDraft: e.target.checked })}
                  />
                  <span className="font-medium text-gray-700">Draft</span>
                </label>
              </div>

              <div className="flex gap-1 border-b border-gray-100 bg-white px-5">
                <button
                  type="button"
                  onClick={() => setDetailTab('edit')}
                  className={`inline-flex items-center gap-1.5 px-3 py-2.5 text-sm font-medium border-b-2 -mb-px transition-colors ${
                    detailTab === 'edit'
                      ? 'border-blue-600 text-blue-700'
                      : 'border-transparent text-gray-500 hover:text-gray-800'
                  }`}
                >
                  <Pencil className="h-4 w-4" /> Edit
                </button>
                <button
                  type="button"
                  onClick={() => setDetailTab('preview')}
                  className={`inline-flex items-center gap-1.5 px-3 py-2.5 text-sm font-medium border-b-2 -mb-px transition-colors ${
                    detailTab === 'preview'
                      ? 'border-blue-600 text-blue-700'
                      : 'border-transparent text-gray-500 hover:text-gray-800'
                  }`}
                >
                  <Eye className="h-4 w-4" /> Launcher preview
                </button>
              </div>

              <div className="space-y-4 p-5">
                {detailTab === 'edit' ? (
                  <>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <label className="block">
                <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Headline</span>
                <input
                  className="mt-1.5 w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-blue-300"
                  value={selected.title}
                  onChange={(e) => patchSelected({ title: e.target.value })}
                  placeholder="Patch 1.0.0 Notes"
                />
              </label>
              <label className="block">
                <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Date</span>
                <input
                  className="mt-1.5 w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-blue-300"
                  value={selected.date}
                  onChange={(e) => patchSelected({ date: e.target.value })}
                  placeholder="2026-01-01"
                />
              </label>
            </div>

            <div className="flex flex-wrap items-center gap-4">
              <label className="flex items-center gap-2">
                <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Tag</span>
                <select
                  className="rounded-md border border-gray-300 bg-white px-3 py-2 text-sm focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-blue-300"
                  value={selected.tag ?? ''}
                  onChange={(e) => patchSelected({ tag: e.target.value })}
                >
                  {NEWS_TAGS.map((t) => (
                    <option key={t.value} value={t.value}>
                      {t.label}
                    </option>
                  ))}
                </select>
              </label>
              <span className="text-xs text-gray-400">Colored ribbon on launcher cards</span>
            </div>

            <div>
              <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Cover image</span>
              <div className="mt-1.5 flex flex-wrap items-center gap-3">
                <label className="inline-flex cursor-pointer items-center gap-2 rounded-md border border-gray-300 px-3 py-2 text-sm hover:bg-gray-50">
                  <Upload className="h-4 w-4" /> Upload cover
                  <input
                    type="file"
                    accept="image/*"
                    className="hidden"
                    onChange={(e) => onCover(e.target.files?.[0])}
                  />
                </label>
                {coverUrl ? (
                  <img src={coverUrl} alt="cover" className="aspect-video h-16 rounded-md object-cover ring-1 ring-gray-200" />
                ) : (
                  <span className="text-sm text-gray-400">No cover uploaded</span>
                )}
              </div>
              <p className="mt-1.5 text-xs text-gray-400">
                Recommended 1280×720 (16:9). Images are resized and cropped to fit launcher cards.
              </p>
            </div>

            <div>
              <span className="text-xs font-semibold uppercase tracking-wide text-gray-500">Body</span>
              <div className="mt-1.5 overflow-hidden rounded-lg border border-gray-200 shadow-sm">
                <div className="flex flex-wrap items-center gap-0.5 border-b border-gray-100 bg-gray-50 p-1.5">
                  <button type="button" className={tbBtn(!!editor?.isActive('bold'))} onClick={() => editor?.chain().focus().toggleBold().run()}>
                    <Bold className="h-4 w-4" />
                  </button>
                  <button type="button" className={tbBtn(!!editor?.isActive('italic'))} onClick={() => editor?.chain().focus().toggleItalic().run()}>
                    <Italic className="h-4 w-4" />
                  </button>
                  <span className="mx-1 h-5 w-px bg-gray-300" />
                  <button type="button" className={tbBtn(!!editor?.isActive('heading', { level: 2 }))} onClick={() => editor?.chain().focus().toggleHeading({ level: 2 }).run()}>
                    <Heading2 className="h-4 w-4" />
                  </button>
                  <button type="button" className={tbBtn(!!editor?.isActive('heading', { level: 3 }))} onClick={() => editor?.chain().focus().toggleHeading({ level: 3 }).run()}>
                    <Heading3 className="h-4 w-4" />
                  </button>
                  <span className="mx-1 h-5 w-px bg-gray-300" />
                  <button type="button" className={tbBtn(!!editor?.isActive('bulletList'))} onClick={() => editor?.chain().focus().toggleBulletList().run()}>
                    <List className="h-4 w-4" />
                  </button>
                  <button type="button" className={tbBtn(!!editor?.isActive('orderedList'))} onClick={() => editor?.chain().focus().toggleOrderedList().run()}>
                    <ListOrdered className="h-4 w-4" />
                  </button>
                  <button type="button" className={tbBtn(!!editor?.isActive('blockquote'))} onClick={() => editor?.chain().focus().toggleBlockquote().run()}>
                    <Quote className="h-4 w-4" />
                  </button>
                  <button type="button" className={tbBtn(false)} onClick={() => editor?.chain().focus().setHorizontalRule().run()}>
                    <Minus className="h-4 w-4" />
                  </button>
                  <span className="mx-1 h-5 w-px bg-gray-300" />
                  <button
                    type="button"
                    className={tbBtn(!!editor?.isActive('link'))}
                    onClick={() => {
                      if (!editor) return
                      const url = window.prompt('Link URL (leave blank to remove)')
                      if (url === null) return
                      if (url === '') editor.chain().focus().unsetLink().run()
                      else editor.chain().focus().extendMarkRange('link').setLink({ href: url }).run()
                    }}
                  >
                    <Link2 className="h-4 w-4" />
                  </button>
                  <button
                    type="button"
                    className={tbBtn(false)}
                    onClick={() => {
                      if (!editor) return
                      const url = window.prompt('Image URL')
                      if (url) editor.chain().focus().setImage({ src: url }).run()
                    }}
                  >
                    <ImagePlus className="h-4 w-4" />
                  </button>
                </div>
                <div className="tiptap-editor bg-white">
                  <EditorContent editor={editor} />
                </div>
              </div>
            </div>
                  </>
                ) : (
                  <div
                    className="overflow-hidden rounded-lg border border-gray-800 bg-gray-900 shadow-inner"
                    style={{ ['--news-accent' as string]: accentColor }}
                  >
                    {coverUrl && (
                      <img src={coverUrl} alt="" className="aspect-video w-full object-cover" />
                    )}
                    <div className="p-5">
                      {selected.tag && (
                        <span
                          className="mb-2 inline-block rounded-full px-2.5 py-0.5 text-[10px] font-bold uppercase tracking-wide text-white"
                          style={{ backgroundColor: NEWS_TAG_COLORS[selected.tag] ?? '#555' }}
                        >
                          {selected.tag}
                        </span>
                      )}
                      <div className="text-xl font-bold text-white">{selected.title || 'Untitled'}</div>
                      {selected.date && <div className="mb-3 text-xs text-gray-400">{selected.date}</div>}
                      <div className="news-content" dangerouslySetInnerHTML={{ __html: previewHtml }} />
                    </div>
                  </div>
                )}
              </div>
            </div>
          )}
        </main>
      </div>
    </div>
  )
}
