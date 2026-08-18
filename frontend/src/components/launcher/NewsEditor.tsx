import { useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { useEditor, EditorContent } from '@tiptap/react'
import StarterKit from '@tiptap/starter-kit'
import Image from '@tiptap/extension-image'
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
  Search,
} from 'lucide-react'
import type { LauncherNewsItemDto } from '@/types/launcher.types'
import { apiErrorMessage as errorMessage, cn } from '@/lib/utils'
import type { LauncherNewsPreviewTheme } from '@/lib/launcher-theme'
import { StackTabHeader, StackTabSideCard } from '@/components/layout/StackTabChrome'
import { NEWS_ARTICLE_TEMPLATES, type NewsArticleTemplate } from './newsTemplates'
import {
  NewsLivePreviewSidebar,
  type NewsPreviewMode,
  type NewsPreviewTarget,
} from './NewsArticlePreview'
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
  launcherPreviewTheme?: LauncherNewsPreviewTheme
  /** Optional page title shown above the editor chrome. */
  pageTitle?: string
  /** Optional subtitle / help text under the title. */
  pageDescription?: string
  /** Optional info banner (e.g. global broadcast notice). */
  infoBanner?: ReactNode
  /** CSS variables + wallpaper for armory-themed preview surfaces. */
  armoryPreviewStyle?: CSSProperties
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
  launcherPreviewTheme,
  pageTitle,
  pageDescription,
  infoBanner,
  armoryPreviewStyle,
}: NewsEditorProps) {
  const [draft, setDraft] = useState<LauncherNewsItemDto[]>(items)
  const [selectedId, setSelectedId] = useState<string | null>(items[0]?.id ?? null)
  const [previewTarget, setPreviewTarget] = useState<NewsPreviewTarget>('launcher')
  const [previewMode, setPreviewMode] = useState<NewsPreviewMode>('article')
  const [librarySearch, setLibrarySearch] = useState('')
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

  const coverUrl = selected?.imageUrl
    ? `${selected.imageUrl}${selected.imageUrl.includes('?') ? '&' : '?'}v=${bust}`
    : null

  const previewArticle = useMemo(
    () =>
      selected
        ? {
            title: selected.title || 'Untitled',
            date: selected.date,
            tag: selected.tag,
            html: selected.html || '',
            coverUrl,
          }
        : null,
    [selected, coverUrl],
  )

  const selectedIndex = useMemo(
    () => (selectedId ? ordered.findIndex((item) => item.id === selectedId) : -1),
    [ordered, selectedId],
  )

  const filteredArticles = useMemo(() => {
    const query = librarySearch.trim().toLowerCase()
    if (!query) return ordered
    return ordered.filter((item) => {
      const haystack = [item.title, item.date, item.tag, item.isDraft ? 'draft' : 'published']
        .filter(Boolean)
        .join(' ')
        .toLowerCase()
      return haystack.includes(query)
    })
  }, [librarySearch, ordered])

  const tbBtn = (active: boolean) =>
    `rounded p-1.5 text-gray-700 hover:bg-gray-100 ${active ? 'bg-gray-200 text-gray-900' : ''}`

  const renderArticleListItem = (item: LauncherNewsItemDto, compact = false) => {
    const isSelected = item.id === selectedId
    const tagColor = item.tag ? NEWS_TAG_COLORS[item.tag] ?? '#555' : null
    return (
      <button
        key={item.id}
        type="button"
        onClick={() => selectArticle(item.id)}
        className={cn(
          'flex w-full border-l-[3px] text-left transition-colors',
          compact ? 'items-center gap-2 px-2 py-2' : 'items-start gap-2.5 px-3 py-2.5',
          item.isDraft ? 'border-l-amber-400' : 'border-l-green-500',
          isSelected ? 'bg-blue-50 ring-1 ring-inset ring-blue-200' : 'hover:bg-slate-50',
        )}
      >
        {!compact &&
          (item.imageUrl ? (
            <img
              src={`${item.imageUrl}?v=${bust}`}
              alt=""
              className="mt-0.5 h-9 w-12 shrink-0 rounded object-cover ring-1 ring-slate-200"
            />
          ) : (
            <span className="mt-0.5 flex h-9 w-12 shrink-0 items-center justify-center rounded bg-slate-100 text-[9px] text-slate-400 ring-1 ring-slate-200">
              -
            </span>
          ))}
        <span className="min-w-0 flex-1">
          <span className={cn('flex items-center gap-1', compact ? 'flex-col items-start gap-0.5' : 'flex-wrap gap-1.5')}>
            <span className={cn('truncate font-medium text-slate-900', compact ? 'text-xs' : 'text-sm')}>
              {item.title || 'Untitled'}
            </span>
            {item.isDraft && (
              <span className="shrink-0 rounded bg-amber-100 px-1 py-0.5 text-[8px] font-semibold uppercase text-amber-800">
                Draft
              </span>
            )}
            {!compact && item.tag && tagColor && (
              <span
                className="shrink-0 rounded px-1.5 py-0.5 text-[9px] font-semibold uppercase text-white"
                style={{ backgroundColor: tagColor }}
              >
                {item.tag}
              </span>
            )}
          </span>
          {!compact && (
            <span className="mt-0.5 block truncate text-xs text-slate-500">{item.date || 'No date'}</span>
          )}
        </span>
      </button>
    )
  }

  const renderArticleLibraryPanel = (compact = false) => (
    <aside
      className={cn(
        'flex min-h-52 flex-col overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm lg:min-h-0 lg:flex-1',
        compact && 'min-h-48',
      )}
    >
      <div className="border-b border-slate-100 bg-slate-50 px-3 py-2">
        <div className="flex items-center justify-between gap-2">
          <h3 className="text-sm font-semibold text-slate-900">Article library</h3>
          <span className="text-xs font-medium text-slate-500">{draft.length}</span>
        </div>
      </div>
      <div className="border-b border-slate-100 px-3 py-2">
        <div className="relative">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400" />
          <input
            type="search"
            value={librarySearch}
            onChange={(e) => setLibrarySearch(e.target.value)}
            placeholder="Search…"
            className="w-full rounded-md border border-slate-300 bg-white py-1.5 pl-8 pr-2 text-sm text-slate-800 placeholder:text-slate-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          />
        </div>
      </div>
      <div className="min-h-0 flex-1 divide-y divide-slate-100 overflow-y-auto">
        {ordered.length === 0 ? (
          <div className="flex h-full flex-col items-center justify-center gap-2 px-3 py-6 text-center text-xs text-slate-500">
            <Newspaper className="h-5 w-5 text-slate-300" />
            No articles yet
          </div>
        ) : filteredArticles.length === 0 ? (
          <div className="px-3 py-6 text-center text-xs text-slate-500">No matches</div>
        ) : (
          filteredArticles.map((item) => renderArticleListItem(item, compact))
        )}
      </div>
    </aside>
  )

  return (
    <div className="relative left-1/2 w-[90vw] -translate-x-1/2 space-y-5">
      {(pageTitle || pageDescription) && (
        <StackTabHeader title={pageTitle ?? 'News'} subtitle={pageDescription} />
      )}
      {infoBanner}

      <div className="grid grid-cols-1 items-stretch gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,2fr)_minmax(0,2fr)] lg:min-h-[320px]">
        {renderArticleLibraryPanel(true)}

        <section className="flex min-w-0 flex-col overflow-hidden rounded-xl border-2 border-dashed border-blue-300 bg-linear-to-br from-blue-50 via-indigo-50/80 to-white shadow-sm">
          <div className="flex flex-1 flex-col items-center justify-center px-4 py-6 text-center sm:px-6">
            <div className="mb-3 flex h-12 w-12 items-center justify-center rounded-full bg-blue-600 text-white shadow-md shadow-blue-600/25">
              <Plus className="h-6 w-6" />
            </div>
            <h3 className="text-base font-semibold text-slate-900 sm:text-lg">Create news article</h3>
            <p className="mt-1 text-sm text-slate-600">
              Patch notes and announcements for the launcher and armory.
            </p>
            <div className="mt-4 flex flex-wrap items-center justify-center gap-2">
              <button
                type="button"
                onClick={() => addArticle()}
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-blue-700"
              >
                <Plus className="h-4 w-4" /> New article
              </button>
              <select
                className="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700 shadow-sm hover:bg-slate-50"
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
        </section>

        <StackTabSideCard
          className="min-w-0"
          title="Article stats"
          description="Autosave keeps drafts safe - publish when ready."
          icon={<Newspaper className="h-5 w-5" />}
        >
          <div className="grid grid-cols-2 gap-3 text-sm">
            <StatPill label="Articles" value={String(draft.length)} />
            <StatPill label="Published" value={String(publishedCount)} tone="success" />
            <StatPill label="Drafts" value={String(draftCount)} tone="warning" />
            <div className="rounded-lg border border-white/10 bg-white/5 px-3 py-2">
              <p className="text-[10px] font-medium uppercase tracking-wide text-slate-400">Status</p>
              {isSaving ? (
                <p className="mt-1 inline-flex items-center gap-1 font-semibold text-slate-200">
                  <Loader2 className="h-3.5 w-3.5 animate-spin" /> Saving…
                </p>
              ) : dirty ? (
                <p className="mt-1 inline-flex items-center gap-1 font-semibold text-amber-300">
                  <AlertCircle className="h-3.5 w-3.5" /> Unsaved
                </p>
              ) : (
                <p className="mt-1 inline-flex items-center gap-1 font-semibold text-emerald-300">
                  <CheckCircle2 className="h-3.5 w-3.5" /> {saved ? 'Saved' : 'Up to date'}
                </p>
              )}
            </div>
          </div>
          <button
            type="button"
            onClick={save}
            disabled={isSaving || !dirty}
            className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-lg border border-white/20 bg-white/10 px-3 py-2 text-sm font-semibold text-white hover:bg-white/15 disabled:opacity-50"
          >
            {isSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Save now
          </button>
        </StackTabSideCard>
      </div>

      {error && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>
      )}

      {!selected ? (
        <div className="flex h-72 flex-col items-center justify-center gap-2 rounded-xl border border-dashed border-slate-300 bg-slate-50/80 shadow-sm">
          <Newspaper className="h-8 w-8 text-slate-300" />
          <p className="text-sm font-medium text-slate-600">Select an article from the library</p>
          <p className="text-xs text-slate-500">Edit content, cover image, and live launcher/armory preview</p>
        </div>
      ) : (
        <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm">
          <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-100 bg-slate-50/80 px-5 py-4">
            <div className="min-w-0">
              <h3 className="truncate text-lg font-semibold text-gray-900">
                {selected.title || 'Untitled article'}
              </h3>
              <p className="mt-1 text-xs text-gray-500">
                {selected.date || 'No date'}
                {selected.isDraft ? ' · Draft (hidden from launcher)' : ' · Published'}
              </p>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <div className="inline-flex items-center rounded-md border border-gray-300 bg-white">
                <button
                  type="button"
                  onClick={() => selectedId && move(selectedId, -1)}
                  disabled={selectedIndex <= 0}
                  className="rounded-l-md p-2 text-gray-500 hover:bg-gray-50 hover:text-gray-800 disabled:opacity-30"
                  title="Move newer"
                >
                  <ChevronUp className="h-4 w-4" />
                </button>
                <button
                  type="button"
                  onClick={() => selectedId && move(selectedId, 1)}
                  disabled={selectedIndex < 0 || selectedIndex >= ordered.length - 1}
                  className="rounded-r-md border-l border-gray-300 p-2 text-gray-500 hover:bg-gray-50 hover:text-gray-800 disabled:opacity-30"
                  title="Move older"
                >
                  <ChevronDown className="h-4 w-4" />
                </button>
              </div>
              <button
                type="button"
                onClick={() => {
                  if (window.confirm(`Delete "${selected.title || 'Untitled'}"? This can't be undone.`)) {
                    removeItem(selected.id)
                  }
                }}
                className="inline-flex items-center gap-1.5 rounded-md border border-red-200 bg-red-50 px-3 py-1.5 text-sm text-red-700 hover:bg-red-100"
                title="Delete article"
              >
                <Trash2 className="h-4 w-4" />
                Delete
              </button>
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
          </div>

          <div className="grid gap-5 p-5 xl:grid-cols-2">
                <div className="min-w-0 space-y-4">
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
              <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">Cover image</span>
              <div className="mt-2 flex flex-wrap items-center gap-3">
                <label className="inline-flex cursor-pointer items-center gap-2 rounded-lg border-2 border-dashed border-blue-200 bg-blue-50/60 px-4 py-3 text-sm font-semibold text-slate-800 hover:border-blue-300 hover:bg-blue-50">
                  <Upload className="h-4 w-4 text-blue-600" /> Upload cover
                  <input
                    type="file"
                    accept="image/*"
                    className="hidden"
                    onChange={(e) => onCover(e.target.files?.[0])}
                  />
                </label>
                {coverUrl ? (
                  <img src={coverUrl} alt="cover" className="aspect-video h-16 rounded-lg object-cover ring-1 ring-slate-200" />
                ) : (
                  <span className="text-sm text-slate-500">No cover uploaded</span>
                )}
              </div>
              <p className="mt-1.5 text-xs text-slate-500">
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
                </div>

                {previewArticle && (
                  <NewsLivePreviewSidebar
                    article={previewArticle}
                    accentColor={launcherPreviewTheme?.accentColor ?? accentColor}
                    launcherPreviewTheme={launcherPreviewTheme}
                    target={previewTarget}
                    mode={previewMode}
                    onTargetChange={setPreviewTarget}
                    onModeChange={setPreviewMode}
                    armoryHostStyle={armoryPreviewStyle}
                  />
                )}
              </div>
            </div>
      )}
    </div>
  )
}

function StatPill({
  label,
  value,
  tone,
}: {
  label: string
  value: string
  tone?: 'success' | 'warning'
}) {
  return (
    <div className="rounded-lg border border-white/10 bg-white/5 px-3 py-2">
      <p className="text-[10px] font-medium uppercase tracking-wide text-slate-400">{label}</p>
      <p
        className={cn(
          'mt-1 text-lg font-bold tabular-nums',
          tone === 'success' && 'text-emerald-300',
          tone === 'warning' && 'text-amber-300',
          !tone && 'text-white',
        )}
      >
        {value}
      </p>
    </div>
  )
}
