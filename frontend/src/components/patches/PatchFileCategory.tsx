import { useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { Upload, Loader2, X } from 'lucide-react'
import type { PatchFileDto } from '@/types/patch.types'
import PatchFileListRow, { formatBytes } from './PatchFileListRow'
import CollapsiblePatchSection, { patchFileListClassName } from './CollapsiblePatchSection'

interface PatchFileCategoryProps {
  title: string
  category: string
  accept?: string
  files: PatchFileDto[]
  disabled?: boolean
  uploading?: boolean
  notice?: ReactNode
  collapseStorageKey?: string
  requireDescription?: boolean
  error?: string | null
  onUpload: (category: string, files: File[], descriptions?: string[]) => void | Promise<void>
  onDelete: (category: string, fileName: string) => void
  onEdit?: (fileName: string) => void
  headerActions?: ReactNode
}

export default function PatchFileCategory({
  title,
  category,
  accept,
  files,
  disabled,
  uploading,
  notice,
  collapseStorageKey,
  requireDescription,
  error,
  onUpload,
  onDelete,
  onEdit,
  headerActions,
}: PatchFileCategoryProps) {
  const inputRef = useRef<HTMLInputElement>(null)
  const [dragOver, setDragOver] = useState(false)
  const [pending, setPending] = useState<{ file: File; description: string }[] | null>(null)

  const uploadBlocked = disabled || uploading

  const startUpload = (fileList: FileList | File[]) => {
    const arr = Array.from(fileList)
    if (arr.length === 0) return
    if (requireDescription) {
      setPending(arr.map((file) => ({ file, description: '' })))
      return
    }
    onUpload(category, arr)
  }

  const confirmPending = () => {
    if (!pending) return
    const uploadFiles = pending.map((p) => p.file)
    const descriptions = pending.map((p) => p.description.trim())
    setPending(null)
    onUpload(category, uploadFiles, descriptions)
  }

  const pendingReady = !!pending && pending.every((p) => p.description.trim().length > 0)

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setDragOver(false)
    if (uploadBlocked) return
    if (e.dataTransfer.files?.length) {
      startUpload(e.dataTransfer.files)
    }
  }

  return (
    <CollapsiblePatchSection
      title={title}
      count={files.length}
      storageKey={collapseStorageKey}
      defaultCollapsed={files.length > 20}
      uploading={uploading}
      error={error}
      headerActions={headerActions}
    >
      {notice && (
        <div className="mb-2 flex items-start gap-2 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
          {notice}
        </div>
      )}

      <div
        onDragOver={(e) => {
          e.preventDefault()
          if (!uploadBlocked) setDragOver(true)
        }}
        onDragLeave={() => setDragOver(false)}
        onDrop={handleDrop}
        onClick={() => !uploadBlocked && inputRef.current?.click()}
        className={`flex items-center justify-center gap-2 rounded-md border-2 border-dashed py-3 text-sm transition-colors ${
          uploadBlocked
            ? 'cursor-not-allowed border-gray-200 text-gray-300'
            : dragOver
            ? 'cursor-pointer border-blue-400 bg-blue-50 text-blue-600'
            : 'cursor-pointer border-gray-300 text-gray-500 hover:border-blue-300 hover:text-blue-500'
        }`}
      >
        {uploading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Upload className="h-4 w-4" />}
        <span>
          {uploading
            ? 'Uploading, please wait...'
            : `Drop files or click to upload${accept ? ` (${accept})` : ''} - up to 8 GB`}
        </span>
        <input
          ref={inputRef}
          type="file"
          multiple
          accept={accept}
          className="hidden"
          onChange={(e) => {
            if (e.target.files?.length) startUpload(e.target.files)
            e.target.value = ''
          }}
        />
      </div>

      {error && (
        <div className="mt-2 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
          {error}
        </div>
      )}

      {files.length > 0 && (
        <ul className={patchFileListClassName(files.length)}>
          {files.map((file) => (
            <PatchFileListRow
              key={file.name}
              label={file.name}
              size={file.size}
              description={file.description}
              disabled={disabled}
              showEdit={!!onEdit}
              onEdit={onEdit ? () => onEdit(file.name) : undefined}
              onDelete={() => onDelete(category, file.name)}
            />
          ))}
        </ul>
      )}

      {pending && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="w-full max-w-lg rounded-lg bg-white shadow-xl">
            <div className="flex items-center justify-between border-b border-gray-100 px-4 py-3">
              <h3 className="font-semibold text-gray-800">
                Describe {pending.length === 1 ? 'the file' : `${pending.length} files`} before uploading
              </h3>
              <button
                type="button"
                onClick={() => setPending(null)}
                className="text-gray-400 hover:text-gray-600"
                title="Cancel"
              >
                <X className="h-5 w-5" />
              </button>
            </div>

            <div className="max-h-[60vh] space-y-4 overflow-y-auto px-4 py-3">
              <p className="text-xs text-gray-500">
                A description is required for each file. It's stored alongside the file and shown in
                this list.
              </p>
              {pending.map((p, i) => (
                <div key={`${p.file.name}-${i}`}>
                  <label className="mb-1 flex items-center justify-between text-sm font-medium text-gray-700">
                    <span className="mr-2 truncate font-mono">{p.file.name}</span>
                    <span className="shrink-0 text-xs font-normal text-gray-400">{formatBytes(p.file.size)}</span>
                  </label>
                  <textarea
                    value={p.description}
                    autoFocus={i === 0}
                    onChange={(e) =>
                      setPending((cur) =>
                        cur ? cur.map((x, idx) => (idx === i ? { ...x, description: e.target.value } : x)) : cur
                      )
                    }
                    rows={2}
                    placeholder="Describe what this file contains (required), e.g. custom loading screens + interface art."
                    className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
              ))}
            </div>

            <div className="flex items-center justify-end gap-2 border-t border-gray-100 px-4 py-3">
              <button
                type="button"
                onClick={() => setPending(null)}
                className="rounded-md px-3 py-1.5 text-sm text-gray-600 hover:bg-gray-100"
              >
                Cancel
              </button>
              <button
                type="button"
                disabled={!pendingReady}
                onClick={confirmPending}
                className="rounded-md bg-blue-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-40"
              >
                Upload {pending.length > 1 ? `${pending.length} files` : ''}
              </button>
            </div>
          </div>
        </div>
      )}
    </CollapsiblePatchSection>
  )
}
