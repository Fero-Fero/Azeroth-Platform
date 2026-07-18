import { useEffect, useMemo, useRef, useState, type DragEvent } from 'react'
import type { UseMutationResult, UseQueryResult } from '@tanstack/react-query'
import { ChevronRight, Folder, File as FileIcon, Home, Loader2, CornerLeftUp, Trash2, Upload, Lock } from 'lucide-react'
import type { ClientBrowseResultDto } from '@/types/client.types'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

function formatBytes(bytes: number): string {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`
}

/** Splits a '/'-joined relative path into cumulative breadcrumb segments. */
function breadcrumbs(path: string): { label: string; path: string }[] {
  if (!path) return []
  const parts = path.split('/').filter(Boolean)
  const crumbs: { label: string; path: string }[] = []
  let acc = ''
  for (const part of parts) {
    acc = acc ? `${acc}/${part}` : part
    crumbs.push({ label: part, path: acc })
  }
  return crumbs
}

const hasFiles = (e: DragEvent) => Array.from(e.dataTransfer?.types ?? []).includes('Files')

const ROW_HEIGHT = 40
const MIN_RENDERED_ROWS = 50
const OVERSCAN_ROWS = 10

const nameCollator = new Intl.Collator(undefined, {
  numeric: true,
  sensitivity: 'base',
})

function leadingNumber(name: string): number | null {
  const match = name.match(/^\d+/)
  return match ? Number(match[0]) : null
}

function compareEntryNames(a: string, b: string): number {
  const aNumber = leadingNumber(a)
  const bNumber = leadingNumber(b)
  if (aNumber !== null || bNumber !== null) {
    if (aNumber === null) return 1
    if (bNumber === null) return -1
    if (aNumber !== bNumber) return aNumber - bNumber
  }

  return nameCollator.compare(a, b)
}

interface FileBrowserProps {
  stackId: string
  title: string
  /** Label for the root breadcrumb (e.g. "game" or "data"). */
  rootLabel: string
  /** Lists one directory level of the tree. */
  useBrowse: (stackId: string, path: string, enabled?: boolean) => UseQueryResult<ClientBrowseResultDto>
  /** Deletes a file/folder by its relative path. */
  useDelete: (stackId: string) => UseMutationResult<unknown, unknown, string>
  /** Uploads a single file into a folder (relative dir). */
  useUpload: (stackId: string) => UseMutationResult<unknown, unknown, { dir: string; file: File }>
  /** Set false for read/delete-only browsers. */
  allowUpload?: boolean
  /** Set true to hide delete actions (browse-only). */
  readOnly?: boolean
}

/**
 * Navigable file browser with per-row delete and drag-and-drop upload. Dropping OS files onto the list
 * uploads them into the current folder; dropping onto a folder row uploads them into that subfolder.
 * Shared by the base-client and armory-dataset browsers.
 */
export default function FileBrowser({
  stackId,
  title,
  rootLabel,
  useBrowse,
  useDelete,
  useUpload,
  allowUpload = true,
  readOnly = false,
}: FileBrowserProps) {
  const uploadsEnabled = allowUpload && !readOnly
  const [path, setPath] = useState('')
  const { data, isLoading, isFetching, error } = useBrowse(stackId, path)
  const deleteEntry = useDelete(stackId)
  const uploadFile = useUpload(stackId)

  const [pendingDelete, setPendingDelete] = useState<string | null>(null)
  const [dragOverPath, setDragOverPath] = useState<string | null>(null)
  const [uploading, setUploading] = useState<{ dir: string; total: number; done: number } | null>(null)
  const [uploadError, setUploadError] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  const listRef = useRef<HTMLDivElement>(null)
  const [scrollTop, setScrollTop] = useState(0)
  const [viewportHeight, setViewportHeight] = useState(0)

  const crumbs = breadcrumbs(path)
  const parentPath = path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : ''
  const searchPending = search !== debouncedSearch
  const searchQuery = debouncedSearch.trim().toLowerCase()
  const sortedDirectoryEntries = useMemo(() => {
    const entries = data?.entries ?? []
    return [...entries].sort((a, b) =>
      a.isDirectory !== b.isDirectory
        ? a.isDirectory
          ? -1
          : 1
        : compareEntryNames(a.name, b.name)
    )
  }, [data?.entries])
  const sortedEntries = useMemo(
    () =>
      searchQuery
        ? sortedDirectoryEntries.filter((entry) => entry.name.toLowerCase().includes(searchQuery))
        : sortedDirectoryEntries,
    [searchQuery, sortedDirectoryEntries]
  )

  const visibleWindow = useMemo(() => {
    const viewportRows = viewportHeight > 0 ? Math.ceil(viewportHeight / ROW_HEIGHT) : 0
    const rowCount = Math.max(MIN_RENDERED_ROWS, viewportRows + OVERSCAN_ROWS * 2)
    const start = Math.max(0, Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN_ROWS)
    const end = Math.min(sortedEntries.length, start + rowCount)

    return {
      entries: sortedEntries.slice(start, end),
      topPadding: start * ROW_HEIGHT,
      bottomPadding: Math.max(0, (sortedEntries.length - end) * ROW_HEIGHT),
    }
  }, [scrollTop, sortedEntries, viewportHeight])

  useEffect(() => {
    const timeout = window.setTimeout(() => {
      setDebouncedSearch(search)
    }, 1000)

    return () => window.clearTimeout(timeout)
  }, [search])

  useEffect(() => {
    const el = listRef.current
    if (!el) return

    const updateHeight = () => setViewportHeight(el.clientHeight)
    updateHeight()

    const observer = new ResizeObserver(updateHeight)
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    setScrollTop(0)
    if (listRef.current) {
      listRef.current.scrollTop = 0
    }
  }, [path, searchQuery])

  const handleDelete = (relativePath: string, name: string, isDirectory: boolean) => {
    const label = isDirectory ? `folder "${name}" and all its contents` : `file "${name}"`
    if (!window.confirm(`Are you sure you want to delete the ${label}? This cannot be undone.`)) return
    setPendingDelete(relativePath)
    deleteEntry.mutate(relativePath, {
      onError: (err) => window.alert(errorMessage(err)),
      onSettled: () => setPendingDelete(null),
    })
  }

  const uploadInto = async (dir: string, files: File[]) => {
    if (!uploadsEnabled || files.length === 0 || uploading) return
    setUploadError(null)
    setUploading({ dir, total: files.length, done: 0 })
    try {
      for (let i = 0; i < files.length; i++) {
        await uploadFile.mutateAsync({ dir, file: files[i] })
        setUploading({ dir, total: files.length, done: i + 1 })
      }
      // Reveal the destination so the user sees the uploaded files.
      setPath(dir)
    } catch (err) {
      setUploadError(errorMessage(err))
    } finally {
      setUploading(null)
      setDragOverPath(null)
    }
  }

  // Drop onto the list background => current folder.
  const onContainerDragOver = (e: DragEvent) => {
    if (!uploadsEnabled || !hasFiles(e) || uploading) return
    e.preventDefault()
    setDragOverPath(path)
  }
  const onContainerDragLeave = (e: DragEvent) => {
    if (e.currentTarget.contains(e.relatedTarget as Node | null)) return
    setDragOverPath(null)
  }
  const onContainerDrop = (e: DragEvent) => {
    if (!uploadsEnabled || !hasFiles(e)) return
    e.preventDefault()
    setDragOverPath(null)
    void uploadInto(path, Array.from(e.dataTransfer.files))
  }

  // Drop onto a folder row => that subfolder.
  const onFolderDragOver = (e: DragEvent, folderPath: string) => {
    if (!uploadsEnabled || !hasFiles(e) || uploading) return
    e.preventDefault()
    e.stopPropagation()
    setDragOverPath(folderPath)
  }
  const onFolderDrop = (e: DragEvent, folderPath: string) => {
    if (!uploadsEnabled || !hasFiles(e)) return
    e.preventDefault()
    e.stopPropagation()
    setDragOverPath(null)
    void uploadInto(folderPath, Array.from(e.dataTransfer.files))
  }

  const currentDropActive = dragOverPath === path

  return (
    <section className="rounded-lg border bg-white p-6 shadow-sm">
      <div className="mb-1 flex items-center justify-between">
        <h2 className="text-lg font-semibold text-gray-900">{title}</h2>
        {isFetching && !isLoading && <Loader2 className="h-4 w-4 animate-spin text-gray-400" />}
      </div>
      {uploadsEnabled && (
        <p className="mb-3 text-xs text-gray-500">
          Drag files onto the list to upload them here, or onto a folder to upload into it.
        </p>
      )}

      <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
        {/* Breadcrumbs */}
        <div className="flex min-w-0 flex-wrap items-center gap-1 text-sm">
          <button
            onClick={() => setPath('')}
            className={`inline-flex items-center gap-1 rounded px-1.5 py-0.5 hover:bg-gray-100 ${
              path === '' ? 'font-medium text-gray-900' : 'text-blue-600'
            }`}
          >
            <Home className="h-3.5 w-3.5" /> {rootLabel}
          </button>
          {crumbs.map((c, i) => (
            <span key={c.path} className="inline-flex items-center gap-1">
              <ChevronRight className="h-3.5 w-3.5 text-gray-400" />
              <button
                onClick={() => setPath(c.path)}
                className={`rounded px-1.5 py-0.5 hover:bg-gray-100 ${
                  i === crumbs.length - 1 ? 'font-medium text-gray-900' : 'text-blue-600'
                }`}
              >
                {c.label}
              </button>
            </span>
          ))}
        </div>
        <div className="relative shrink-0">
          <input
            type="search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search current folder..."
            className="w-56 rounded-md border border-gray-300 px-3 py-1.5 pr-8 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
          {searchPending && (
            <Loader2 className="absolute right-2.5 top-1/2 h-4 w-4 -translate-y-1/2 animate-spin text-gray-400" />
          )}
        </div>
      </div>

      {uploadError && <div className="mb-3 rounded-md bg-red-50 p-3 text-sm text-red-700">{uploadError}</div>}
      {uploading && (
        <div className="mb-3 inline-flex items-center gap-2 rounded-md bg-blue-50 px-3 py-2 text-sm text-blue-700">
          <Loader2 className="h-4 w-4 animate-spin" />
          Uploading {uploading.done}/{uploading.total} to{' '}
          <span className="font-mono">{uploading.dir || rootLabel}</span>…
        </div>
      )}

      {error ? (
        <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{errorMessage(error)}</div>
      ) : isLoading ? (
        <div className="flex items-center justify-center py-10 text-gray-400">
          <Loader2 className="h-5 w-5 animate-spin" />
        </div>
      ) : (
        <div
          onDragOver={onContainerDragOver}
          onDragLeave={onContainerDragLeave}
          onDrop={onContainerDrop}
          className={`overflow-hidden rounded-md border transition-colors ${
            currentDropActive ? 'border-blue-400 ring-2 ring-blue-300' : ''
          }`}
        >
          {/* Up a level */}
          {path !== '' && (
            <button
              onClick={() => setPath(parentPath)}
              className="flex w-full items-center gap-2 border-b bg-gray-50/50 px-3 py-2 text-left text-sm text-gray-600 hover:bg-gray-100"
            >
              <CornerLeftUp className="h-4 w-4 text-gray-400" />
              <span>..</span>
            </button>
          )}

          {data && sortedEntries.length > 0 ? (
            <div
              ref={listRef}
              onScroll={(e) => setScrollTop(e.currentTarget.scrollTop)}
              className="max-h-[60vh] overflow-y-auto"
            >
              <ul className="divide-y">
                {visibleWindow.topPadding > 0 && (
                  <li aria-hidden="true" style={{ height: visibleWindow.topPadding }} />
                )}
                {visibleWindow.entries.map((entry) => {
                  const deleting = pendingDelete === entry.relativePath
                  const folderDropActive = entry.isDirectory && dragOverPath === entry.relativePath
                  return (
                    <li
                      key={entry.relativePath}
                      style={{ height: ROW_HEIGHT }}
                      onDragOver={entry.isDirectory ? (e) => onFolderDragOver(e, entry.relativePath) : undefined}
                      onDrop={entry.isDirectory ? (e) => onFolderDrop(e, entry.relativePath) : undefined}
                      className={`group flex items-center ${
                        folderDropActive ? 'bg-blue-100 ring-1 ring-inset ring-blue-400' : 'hover:bg-blue-50/50'
                      }`}
                    >
                      {entry.isDirectory ? (
                        <button
                          onClick={() => setPath(entry.relativePath)}
                          className="flex h-full min-w-0 flex-1 items-center justify-between gap-2 px-3 text-left text-sm"
                        >
                          <span className="flex items-center gap-2 truncate">
                            <Folder className="h-4 w-4 shrink-0 text-amber-500" />
                            <span className="truncate font-medium text-gray-800">{entry.name}</span>
                          </span>
                          <span className="shrink-0 text-xs text-gray-400">
                            {entry.itemCount} {entry.itemCount === 1 ? 'item' : 'items'}
                          </span>
                        </button>
                      ) : (
                        <div className="flex h-full min-w-0 flex-1 items-center justify-between gap-2 px-3 text-sm">
                          <span className="flex items-center gap-2 truncate">
                            <FileIcon className="h-4 w-4 shrink-0 text-gray-400" />
                            <span className="truncate text-gray-700">{entry.name}</span>
                          </span>
                          <span className="shrink-0 font-mono text-xs text-gray-400">{formatBytes(entry.size)}</span>
                        </div>
                      )}
                      {!readOnly && (
                        entry.isLocked ? (
                        <span
                          title={`${entry.name} is locked because its patch has already been applied`}
                          className="mr-1.5 shrink-0 rounded p-1.5 text-gray-300 group-hover:text-gray-400"
                        >
                          <Lock className="h-4 w-4" />
                        </span>
                      ) : (
                        <button
                          onClick={() => handleDelete(entry.relativePath, entry.name, entry.isDirectory)}
                          disabled={deleting}
                          title={`Delete ${entry.name}`}
                          className="mr-1.5 shrink-0 rounded p-1.5 text-gray-300 hover:bg-red-50 hover:text-red-600 disabled:cursor-not-allowed group-hover:text-gray-400"
                        >
                          {deleting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
                        </button>
                      ))}
                    </li>
                  )
                })}
                {visibleWindow.bottomPadding > 0 && (
                  <li aria-hidden="true" style={{ height: visibleWindow.bottomPadding }} />
                )}
              </ul>
            </div>
          ) : (
            <div className="flex flex-col items-center gap-1 px-3 py-8 text-center text-sm text-gray-400">
              <Upload className="h-5 w-5 text-gray-300" />
              {searchQuery
                ? 'No files or folders match your search.'
                : uploadsEnabled
                ? 'This folder is empty. Drag files here to upload.'
                : 'This folder is empty.'}
            </div>
          )}
        </div>
      )}
    </section>
  )
}
