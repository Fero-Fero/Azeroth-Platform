import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Upload, Loader2, FolderPlus, Folder } from 'lucide-react'
import type { PatchFileDto } from '@/types/patch.types'
import PatchFileListRow from './PatchFileListRow'
import CollapsiblePatchSection, { patchFileListClassName } from './CollapsiblePatchSection'

interface ContainerFileCategoryProps {
  title: string
  accept?: string
  files: PatchFileDto[]
  disabled?: boolean
  uploading?: boolean
  error?: string | null
  notice?: ReactNode
  collapseStorageKey?: string
  onUploadItems: (items: { file: File; path: string }[]) => void | Promise<void>
  onDelete: (fileName: string) => void
  onEdit?: (fileName: string) => void
  /** Max path segments (file included). DBC/maps default to 2 (one folder). MPQ content allows deep trees. */
  maxPathSegments?: number
}

type UploadItem = { file: File; path: string }

/** Reads all entries from a directory reader (readEntries returns them in batches). */
function readAllEntries(reader: FileSystemDirectoryReader): Promise<FileSystemEntry[]> {
  return new Promise((resolve, reject) => {
    const all: FileSystemEntry[] = []
    const readBatch = () => {
      reader.readEntries((batch) => {
        if (batch.length === 0) {
          resolve(all)
        } else {
          all.push(...batch)
          readBatch()
        }
      }, reject)
    }
    readBatch()
  })
}

/** Recursively walks a dropped file/directory entry, building relative paths (e.g. "gems/Item.csv"). */
async function walkEntry(entry: FileSystemEntry, prefix: string, out: UploadItem[]): Promise<void> {
  if (entry.isFile) {
    const file = await new Promise<File>((resolve, reject) =>
      (entry as FileSystemFileEntry).file(resolve, reject)
    )
    out.push({ file, path: prefix + entry.name })
  } else if (entry.isDirectory) {
    const reader = (entry as FileSystemDirectoryEntry).createReader()
    const children = await readAllEntries(reader)
    for (const child of children) {
      await walkEntry(child, `${prefix}${entry.name}/`, out)
    }
  }
}

/**
 * Collects dropped items, preserving folder structure via the entries API (dropped folders are not
 * present in dataTransfer.files). Entry handles must be captured synchronously during the drop.
 */
async function collectDropItems(dataTransfer: DataTransfer): Promise<UploadItem[]> {
  const list = dataTransfer.items
  const supportsEntries = list && list.length > 0 && typeof list[0].webkitGetAsEntry === 'function'

  if (supportsEntries) {
    const entries: FileSystemEntry[] = []
    for (let i = 0; i < list.length; i++) {
      const entry = list[i].webkitGetAsEntry()
      if (entry) entries.push(entry)
    }
    const out: UploadItem[] = []
    for (const entry of entries) {
      await walkEntry(entry, '', out)
    }
    return out
  }

  return Array.from(dataTransfer.files).map((f) => ({ file: f, path: f.name }))
}

/** Only text DBC sources (CSV/.txt) can be edited inline; a binary .dbc upload cannot. */
function isTextEditable(fileName: string): boolean {
  const lower = fileName.toLowerCase()
  return lower.endsWith('.csv') || lower.endsWith('.txt')
}

function matchesAccept(fileName: string, accept?: string): boolean {
  if (!accept) return true
  const exts = accept.split(',').map((s) => s.trim().toLowerCase()).filter(Boolean)
  const lower = fileName.toLowerCase()
  return exts.some((ext) => lower.endsWith(ext))
}

export default function ContainerFileCategory({
  title,
  accept,
  files,
  disabled,
  uploading,
  error,
  notice,
  collapseStorageKey,
  onUploadItems,
  onDelete,
  onEdit,
  maxPathSegments = 2,
}: ContainerFileCategoryProps) {
  const rootInputRef = useRef<HTMLInputElement>(null)
  const folderInputRef = useRef<HTMLInputElement>(null)
  const containerInputRef = useRef<HTMLInputElement>(null)
  const [containerTarget, setContainerTarget] = useState<string | null>(null)
  const [dragRoot, setDragRoot] = useState(false)
  const [dragContainer, setDragContainer] = useState<string | null>(null)

  useEffect(() => {
    if (folderInputRef.current) {
      folderInputRef.current.setAttribute('webkitdirectory', '')
      folderInputRef.current.setAttribute('directory', '')
    }
  }, [])

  // Group into root-level files and one-level containers.
  const rootFiles: PatchFileDto[] = []
  const containers = new Map<string, PatchFileDto[]>()
  for (const file of files) {
    const slash = file.name.indexOf('/')
    if (slash === -1) {
      rootFiles.push(file)
    } else {
      const sub = file.name.slice(0, slash)
      ;(containers.get(sub) ?? containers.set(sub, []).get(sub)!).push(file)
    }
  }
  const existing = new Set(files.map((f) => f.name))

  const submit = async (items: UploadItem[]) => {
    if (items.length === 0) {
      window.alert(`No ${accept ? accept + ' ' : ''}files were found in the selection.`)
      return
    }

    const tooDeep = items.filter((i) => i.path.split('/').filter(Boolean).length > maxPathSegments)
    if (tooDeep.length > 0) {
      window.alert(
        maxPathSegments <= 2
          ? 'Only one level of folders is allowed. These are nested too deep:\n\n' +
            tooDeep.map((i) => i.path).join('\n') +
            '\n\nFlatten so each file sits directly inside a single container folder.'
          : `These paths are nested too deep (max ${maxPathSegments} segments):\n\n` +
            tooDeep.map((i) => i.path).join('\n')
      )
      return
    }

    const clashes = items.filter((i) => existing.has(i.path))
    if (clashes.length > 0) {
      const ok = window.confirm(
        'The following file(s) already exist and will be overwritten:\n\n' +
          clashes.map((i) => i.path).join('\n') +
          '\n\nContinue?'
      )
      if (!ok) return
    }

    await onUploadItems(items)
  }

  const toItems = (list: FileList | File[], pathFor: (f: File) => string) =>
    Array.from(list)
      .filter((f) => matchesAccept(f.name, accept))
      .map((f) => ({ file: f, path: pathFor(f) }))

  return (
    <CollapsiblePatchSection
      title={title}
      count={files.length}
      storageKey={collapseStorageKey}
      defaultCollapsed={files.length > 20}
      uploading={uploading}
      error={error}
      headerActions={
        <button
          type="button"
          disabled={disabled}
          onClick={() => folderInputRef.current?.click()}
          className="inline-flex items-center gap-1.5 text-sm text-blue-600 hover:text-blue-700 disabled:opacity-40"
        >
          {uploading ? <Loader2 className="h-4 w-4 animate-spin" /> : <FolderPlus className="h-4 w-4" />}
          Upload folder
        </button>
      }
    >
      {notice && (
        <div className="mb-2 flex items-start gap-2 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
          {notice}
        </div>
      )}

      <p className="mb-3 text-xs text-gray-500">
        Files can sit at the root or inside a single container folder. Upload a folder (or drop
        folders) to create containers; nested sub-folders aren't allowed.
      </p>

      {/* Root drop zone */}
      <div
        onDragOver={(e) => {
          e.preventDefault()
          if (!disabled) setDragRoot(true)
        }}
        onDragLeave={() => setDragRoot(false)}
        onDrop={(e) => {
          e.preventDefault()
          setDragRoot(false)
          if (disabled) return
          collectDropItems(e.dataTransfer).then((items) =>
            submit(items.filter((i) => matchesAccept(i.file.name, accept)))
          )
        }}
        onClick={() => !disabled && rootInputRef.current?.click()}
        className={`flex items-center justify-center gap-2 py-3 rounded-md border-2 border-dashed cursor-pointer text-sm transition-colors ${
          disabled
            ? 'border-gray-200 text-gray-300 cursor-not-allowed'
            : dragRoot
            ? 'border-blue-400 bg-blue-50 text-blue-600'
            : 'border-gray-300 text-gray-500 hover:border-blue-300 hover:text-blue-500'
        }`}
      >
        <Upload className="w-4 h-4" />
        <span>Drop files or folders here{accept ? ` (${accept})` : ''}</span>
      </div>

      {error && (
        <div className="mt-2 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
          {error}
        </div>
      )}

      {/* Hidden inputs */}
      <input
        ref={rootInputRef}
        type="file"
        multiple
        accept={accept}
        className="hidden"
        onChange={(e) => {
          if (e.target.files?.length) submit(toItems(e.target.files, (f) => f.name))
          e.target.value = ''
        }}
      />
      <input
        ref={folderInputRef}
        type="file"
        multiple
        className="hidden"
        onChange={(e) => {
          if (e.target.files?.length) {
            submit(toItems(e.target.files, (f) => (f as File & { webkitRelativePath?: string }).webkitRelativePath || f.name))
          }
          e.target.value = ''
        }}
      />
      <input
        ref={containerInputRef}
        type="file"
        multiple
        accept={accept}
        className="hidden"
        onChange={(e) => {
          const target = containerTarget
          if (target && e.target.files?.length) {
            submit(toItems(e.target.files, (f) => `${target}/${f.name}`))
          }
          e.target.value = ''
          setContainerTarget(null)
        }}
      />

      {/* Root files */}
      {rootFiles.length > 0 && (
        <ul className={patchFileListClassName(rootFiles.length)}>
          {rootFiles.map((file) => (
            <PatchFileListRow
              key={file.name}
              label={file.name}
              size={file.size}
              description={file.description}
              disabled={disabled}
              showEdit={!!onEdit && isTextEditable(file.name)}
              onEdit={onEdit ? () => onEdit(file.name) : undefined}
              onDelete={() => onDelete(file.name)}
            />
          ))}
        </ul>
      )}

      {/* Containers */}
      {[...containers.entries()]
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([sub, subFiles]) => (
          <div
            key={sub}
            onDragOver={(e) => {
              e.preventDefault()
              if (!disabled) setDragContainer(sub)
            }}
            onDragLeave={() => setDragContainer((cur) => (cur === sub ? null : cur))}
            onDrop={(e) => {
              e.preventDefault()
              setDragContainer(null)
              if (disabled) return
              // Drop into a container: prefix the container name; a dropped folder becomes too deep
              // (container/sub/file) and is rejected by the guard.
              collectDropItems(e.dataTransfer).then((items) =>
                submit(
                  items
                    .filter((i) => matchesAccept(i.file.name, accept))
                    .map((i) => ({ file: i.file, path: `${sub}/${i.path}` }))
                )
              )
            }}
            className={`mt-3 rounded-md border bg-slate-50/80 transition-colors ${
              dragContainer === sub ? 'border-blue-400 bg-blue-50' : 'border-gray-200'
            }`}
          >
            <div className="flex items-center justify-between border-b border-gray-200 bg-white px-3 py-2.5">
              <span className="inline-flex items-center gap-1.5 text-sm font-semibold text-gray-800">
                <Folder className="h-4 w-4 text-amber-500" />
                {sub}
                <span className="font-normal text-gray-500">({subFiles.length})</span>
              </span>
              <button
                type="button"
                disabled={disabled}
                onClick={() => {
                  setContainerTarget(sub)
                  containerInputRef.current?.click()
                }}
                className="inline-flex items-center gap-1 text-xs text-blue-600 hover:text-blue-700 disabled:opacity-40"
              >
                <Upload className="w-3.5 h-3.5" /> Add files
              </button>
            </div>
            <ul className={`space-y-2 p-3 ${subFiles.length > 12 ? 'max-h-64 overflow-y-auto' : ''}`}>
              {subFiles.map((file) => (
                <PatchFileListRow
                  key={file.name}
                  label={file.name.slice(sub.length + 1)}
                  size={file.size}
                  description={file.description}
                  disabled={disabled}
                  showEdit={!!onEdit && isTextEditable(file.name)}
                  onEdit={onEdit ? () => onEdit(file.name) : undefined}
                  onDelete={() => onDelete(file.name)}
                />
              ))}
            </ul>
            <p className="px-3 pb-2 text-[11px] text-gray-500">Drop files here to add to this container.</p>
          </div>
        ))}
    </CollapsiblePatchSection>
  )
}
