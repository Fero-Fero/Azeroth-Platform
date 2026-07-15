import { useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import {
  Loader2,
  Save,
  RotateCw,
  FileCog,
  AlertCircle,
  Server,
  Puzzle,
  Search,
  ChevronUp,
  ChevronDown,
  Maximize2,
  Minimize2,
} from 'lucide-react'
import {
  useServerConfigs,
  useServerConfig,
  useSaveServerConfig,
  useApplyServerConfig,
} from '@/hooks/useServerFiles'
import type { ServerConfigFileDto } from '@/types/server.types'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

export default function ServerConfigTab({ stackId }: { stackId: string }) {
  const { data: list, isLoading, error } = useServerConfigs(stackId)
  const [selected, setSelected] = useState<string | null>(null)
  const { data: file, isFetching } = useServerConfig(stackId, selected)
  const saveConfig = useSaveServerConfig(stackId)
  const applyConfig = useApplyServerConfig(stackId)

  const [text, setText] = useState('')
  const [dirty, setDirty] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)

  const [fullscreen, setFullscreen] = useState(false)
  const [search, setSearch] = useState('')
  const [matchIndex, setMatchIndex] = useState(-1)
  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const backdropRef = useRef<HTMLDivElement>(null)
  const searchRef = useRef<HTMLInputElement>(null)

  // Byte offsets of every (case-insensitive) match of the search term in the current file.
  const matches = useMemo(() => {
    const out: number[] = []
    if (!search) return out
    const hay = text.toLowerCase()
    const needle = search.toLowerCase()
    let i = hay.indexOf(needle)
    while (i !== -1) {
      out.push(i)
      i = hay.indexOf(needle, i + Math.max(needle.length, 1))
    }
    return out
  }, [text, search])

  // Segments of the file split into plain text and highlighted matches, rendered on a backdrop
  // behind the (transparent) textarea. Skipped when there are no matches, or so many that building
  // thousands of DOM nodes would hurt typing latency.
  const highlightSegments = useMemo(() => {
    if (matches.length === 0 || matches.length > 5000) return null
    const len = search.length
    const segs: { text: string; mark: boolean; current: boolean }[] = []
    let last = 0
    for (let i = 0; i < matches.length; i++) {
      const start = matches[i]
      if (start > last) segs.push({ text: text.slice(last, start), mark: false, current: false })
      segs.push({ text: text.slice(start, start + len), mark: true, current: i === matchIndex })
      last = start + len
    }
    if (last < text.length) segs.push({ text: text.slice(last), mark: false, current: false })
    return segs
  }, [text, matches, search, matchIndex])

  const syncScroll = () => {
    const ta = textareaRef.current
    const bd = backdropRef.current
    if (ta && bd) {
      bd.scrollTop = ta.scrollTop
      bd.scrollLeft = ta.scrollLeft
    }
  }

  // Reset navigation whenever the query or the open file changes.
  useEffect(() => {
    setMatchIndex(-1)
  }, [search, selected])

  // Selects a match in the textarea WITHOUT stealing focus from the search field, so Enter can be
  // pressed repeatedly to cycle matches. The selection is set on the (unfocused) textarea so it is
  // visible the moment the user Tabs into the document; the view scrolls to keep the match centered.
  const applyMatch = (idx: number) => {
    const ta = textareaRef.current
    if (!ta || matches.length === 0) return
    const clamped = ((idx % matches.length) + matches.length) % matches.length
    const start = matches[clamped]
    const end = start + search.length

    ta.setSelectionRange(start, end)

    // Center the matched line in the viewport (textareas don't auto-scroll to a selection).
    const line = text.slice(0, start).split('\n').length - 1
    const style = window.getComputedStyle(ta)
    const lineHeight = parseFloat(style.lineHeight) || 16
    const padTop = parseFloat(style.paddingTop) || 0
    ta.scrollTop = Math.max(0, line * lineHeight + padTop - ta.clientHeight / 2)
    syncScroll()

    setMatchIndex(clamped)
  }

  const gotoMatch = (delta: number) => {
    if (matches.length === 0) return
    const base = matchIndex < 0 ? (delta >= 0 ? 0 : matches.length - 1) : matchIndex + delta
    applyMatch(base)
  }

  // Moves focus into the document at the current match (used when the user presses Tab).
  const focusDocumentAtMatch = () => {
    const ta = textareaRef.current
    if (!ta) return
    ta.focus()
    if (matchIndex >= 0 && matchIndex < matches.length) {
      const start = matches[matchIndex]
      ta.setSelectionRange(start, start + search.length)
    }
  }

  const handleSearchKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      // Stay in the search field and jump to the next (or previous, with Shift) match.
      e.preventDefault()
      gotoMatch(e.shiftKey ? -1 : 1)
    } else if (e.key === 'Tab' && !e.shiftKey && matches.length > 0) {
      // Only Tab hands focus to the document, landing on the current match.
      e.preventDefault()
      focusDocumentAtMatch()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      if (fullscreen) setFullscreen(false)
      else setSearch('')
    }
  }

  // Ctrl/Cmd+F focuses the search field (both inline and full-screen); Esc exits full screen.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && (e.key === 'f' || e.key === 'F')) {
        if (!selected) return
        e.preventDefault()
        searchRef.current?.focus()
        searchRef.current?.select()
      } else if (e.key === 'Escape' && fullscreen) {
        setFullscreen(false)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [selected, fullscreen])

  const groups = useMemo(() => {
    const server: ServerConfigFileDto[] = []
    const modules: ServerConfigFileDto[] = []
    for (const f of list?.files ?? []) {
      ;(f.category === 'modules' ? modules : server).push(f)
    }
    return { server, modules }
  }, [list])

  useEffect(() => {
    if (file) {
      setText(file.content)
      setDirty(false)
    }
  }, [file])

  const handleSave = async () => {
    if (!selected) return
    setNotice(null)
    try {
      await saveConfig.mutateAsync({ path: selected, content: text })
      setDirty(false)
      setNotice('Saved. Click “Apply & restart” for it to take effect.')
    } catch (err) {
      setNotice(errorMessage(err))
    }
  }

  const handleApply = async () => {
    if (!window.confirm('Restart the worldserver and authserver to apply config changes?')) return
    setNotice(null)
    try {
      await applyConfig.mutateAsync()
      setNotice('Servers restarting — changes are being applied.')
    } catch (err) {
      setNotice(errorMessage(err))
    }
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16">
        <Loader2 className="h-8 w-8 animate-spin text-blue-500" />
      </div>
    )
  }

  if (error) {
    return (
      <div className="rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        Failed to load server configuration.
      </div>
    )
  }

  if (!list?.generated) {
    return (
      <div className="rounded-md border border-amber-200 bg-amber-50 px-4 py-4 text-sm text-amber-800">
        <div className="flex items-start gap-2">
          <AlertCircle className="mt-0.5 h-5 w-5 shrink-0 text-amber-600" />
          <div>
            <p className="font-medium">Config files not generated yet.</p>
            <p className="mt-1">
              Start the stack once so the server writes <code>worldserver.conf</code> /{' '}
              <code>authserver.conf</code> (and any module configs). They'll appear here for editing
              afterwards.
            </p>
          </div>
        </div>
      </div>
    )
  }

  const editorCard = (
    <div
      className={
        fullscreen
          ? 'flex h-full w-full flex-col overflow-hidden rounded-lg border border-gray-200 bg-white shadow-2xl'
          : 'rounded-lg border border-gray-200 bg-white'
      }
    >
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-gray-100 px-4 py-2">
        <span className="font-mono text-sm text-gray-700">{selected}</span>
        <div className="flex flex-wrap items-center gap-2">
          <div className="flex items-center gap-1 rounded-md border border-gray-300 bg-white px-2 py-1">
            <Search className="h-3.5 w-3.5 shrink-0 text-gray-400" />
            <input
              ref={searchRef}
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              onKeyDown={handleSearchKeyDown}
              placeholder="Search… (Ctrl/⌘F)"
              className="w-44 border-0 bg-transparent p-0 text-sm text-gray-700 placeholder:text-gray-400 focus:outline-none focus:ring-0"
            />
            <span className="min-w-[2.75rem] text-right text-xs tabular-nums text-gray-400">
              {search ? `${matches.length ? matchIndex + 1 : 0}/${matches.length}` : ''}
            </span>
            <button
              type="button"
              onClick={() => gotoMatch(-1)}
              disabled={matches.length === 0}
              title="Previous match (Shift+Enter)"
              className="rounded p-0.5 text-gray-500 hover:bg-gray-100 disabled:opacity-40"
            >
              <ChevronUp className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={() => gotoMatch(1)}
              disabled={matches.length === 0}
              title="Next match (Enter)"
              className="rounded p-0.5 text-gray-500 hover:bg-gray-100 disabled:opacity-40"
            >
              <ChevronDown className="h-4 w-4" />
            </button>
          </div>
          <button
            type="button"
            onClick={() => setFullscreen((v) => !v)}
            title={fullscreen ? 'Exit full screen (Esc)' : 'Full screen'}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-2.5 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50"
          >
            {fullscreen ? <Minimize2 className="h-4 w-4" /> : <Maximize2 className="h-4 w-4" />}
          </button>
          <button
            onClick={handleSave}
            disabled={!dirty || saveConfig.isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {saveConfig.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Save
          </button>
          <button
            onClick={handleApply}
            disabled={applyConfig.isPending}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
          >
            {applyConfig.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <RotateCw className="h-4 w-4" />}
            Apply &amp; restart
          </button>
        </div>
      </div>
      {isFetching && !file ? (
        <div className="flex items-center justify-center py-16">
          <Loader2 className="h-6 w-6 animate-spin text-blue-500" />
        </div>
      ) : (
        <div
          className={`relative w-full overflow-hidden bg-gray-900 ${
            fullscreen ? 'flex-1' : 'h-[55vh] rounded-b-lg'
          }`}
        >
          {/* Backdrop mirrors the text with <mark> highlights; the textarea on top is transparent. */}
          <div
            ref={backdropRef}
            aria-hidden="true"
            className="pointer-events-none absolute inset-0 select-none overflow-x-hidden overflow-y-scroll whitespace-pre-wrap break-words px-4 py-3 font-mono text-xs leading-relaxed text-transparent"
          >
            {highlightSegments
              ? highlightSegments.map((seg, i) =>
                  seg.mark ? (
                    <mark
                      key={i}
                      className={`rounded-sm text-transparent ${
                        seg.current ? 'bg-orange-400/70' : 'bg-yellow-300/40'
                      }`}
                    >
                      {seg.text}
                    </mark>
                  ) : (
                    <span key={i}>{seg.text}</span>
                  )
                )
              : text}
            {'\n'}
          </div>
          <textarea
            ref={textareaRef}
            value={text}
            onChange={(e) => {
              setText(e.target.value)
              setDirty(true)
            }}
            onScroll={syncScroll}
            spellCheck={false}
            className="absolute inset-0 h-full w-full resize-none overflow-x-hidden overflow-y-scroll whitespace-pre-wrap break-words border-0 bg-transparent px-4 py-3 font-mono text-xs leading-relaxed text-gray-100 caret-gray-100 focus:outline-none focus:ring-0"
          />
        </div>
      )}
    </div>
  )

  return (
    <div className="grid grid-cols-1 gap-4 md:grid-cols-[260px_1fr]">
      <div className="max-h-[60vh] overflow-y-auto rounded-lg border border-gray-200 bg-white">
        <ConfigGroup
          title="Server"
          icon={<Server className="h-3.5 w-3.5" />}
          files={groups.server}
          selected={selected}
          onSelect={setSelected}
        />
        <ConfigGroup
          title="Modules"
          icon={<Puzzle className="h-3.5 w-3.5" />}
          files={groups.modules}
          selected={selected}
          onSelect={setSelected}
          emptyHint="No module configs. Install modules and start the stack to populate them."
          stripPrefix="modules/"
        />
      </div>

      <div className="min-w-0">
        {!selected ? (
          <div className="rounded-lg border border-dashed border-gray-300 py-16 text-center text-sm text-gray-500">
            Select a config file to edit.
          </div>
        ) : fullscreen ? (
          <>
            <div className="rounded-lg border border-dashed border-gray-300 py-16 text-center text-sm text-gray-500">
              Editing <span className="font-mono">{selected}</span> in full screen…
            </div>
            <div className="fixed inset-0 z-50 flex flex-col gap-2 bg-gray-900/70 p-4">
              {editorCard}
              {notice && <p className="text-sm text-gray-100">{notice}</p>}
            </div>
          </>
        ) : (
          editorCard
        )}
        {!fullscreen && notice && <p className="mt-2 text-sm text-gray-600">{notice}</p>}
      </div>
    </div>
  )
}

function ConfigGroup({
  title,
  icon,
  files,
  selected,
  onSelect,
  emptyHint,
  stripPrefix,
}: {
  title: string
  icon: ReactNode
  files: ServerConfigFileDto[]
  selected: string | null
  onSelect: (path: string) => void
  emptyHint?: string
  stripPrefix?: string
}) {
  return (
    <div className="border-b border-gray-100 last:border-b-0">
      <div className="flex items-center gap-1.5 px-3 py-2 text-xs font-semibold uppercase tracking-wide text-gray-500">
        {icon}
        {title}
        <span className="text-gray-400">({files.length})</span>
      </div>
      {files.length === 0 ? (
        emptyHint && <p className="px-3 pb-2 text-xs text-gray-400">{emptyHint}</p>
      ) : (
        <ul className="pb-1">
          {files.map((f) => {
            const label = stripPrefix && f.path.startsWith(stripPrefix) ? f.path.slice(stripPrefix.length) : f.path
            return (
              <li key={f.path}>
                <button
                  onClick={() => onSelect(f.path)}
                  className={`flex w-full items-center gap-2 px-3 py-2 text-left text-sm ${
                    selected === f.path ? 'bg-blue-50 text-blue-700' : 'text-gray-700 hover:bg-gray-50'
                  }`}
                >
                  <FileCog className="h-4 w-4 shrink-0 text-gray-400" />
                  <span className="truncate font-mono text-xs">{label}</span>
                </button>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
