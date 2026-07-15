import { useEffect, useRef, useState } from 'react'
import {
  Loader2,
  Save,
  RotateCw,
  Upload,
  Trash2,
  FileCode,
  Folder,
  FilePlus,
  AlertCircle,
} from 'lucide-react'
import {
  useLuaScripts,
  useLuaScript,
  useSaveLuaScript,
  useUploadLuaScript,
  useDeleteLuaScript,
  useApplyLua,
} from '@/hooks/useServerFiles'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

const isEditable = (path: string) => /\.(lua|txt|conf|json|ext)$/i.test(path)

export default function LuaScriptsTab({ stackId }: { stackId: string }) {
  const { data: list, isLoading, error } = useLuaScripts(stackId)
  const [selected, setSelected] = useState<string | null>(null)
  const { data: file, isFetching } = useLuaScript(stackId, selected)
  const saveScript = useSaveLuaScript(stackId)
  const uploadScript = useUploadLuaScript(stackId)
  const deleteScript = useDeleteLuaScript(stackId)
  const applyLua = useApplyLua(stackId)

  const fileInputRef = useRef<HTMLInputElement>(null)
  const [text, setText] = useState('')
  const [dirty, setDirty] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [dragOver, setDragOver] = useState(false)

  useEffect(() => {
    if (file) {
      setText(file.content)
      setDirty(false)
    }
  }, [file])

  const uploadFiles = async (files: FileList | File[]) => {
    setNotice(null)
    try {
      for (const f of Array.from(files)) {
        // Preserve folder structure when the browser provides a relative path (folder upload).
        const rel = (f as File & { webkitRelativePath?: string }).webkitRelativePath
        await uploadScript.mutateAsync({ file: f, path: rel && rel.length > 0 ? rel : undefined })
      }
    } catch (err) {
      setNotice(errorMessage(err))
    }
  }

  const handleNewFile = async () => {
    const name = window.prompt('New Lua script path (e.g. hello.lua or events/onlogin.lua):')
    if (!name) return
    const path = /\.\w+$/.test(name) ? name : `${name}.lua`
    setNotice(null)
    try {
      await saveScript.mutateAsync({ path, content: '-- New Eluna script\n' })
      setSelected(path)
    } catch (err) {
      setNotice(errorMessage(err))
    }
  }

  const handleSave = async () => {
    if (!selected) return
    setNotice(null)
    try {
      await saveScript.mutateAsync({ path: selected, content: text })
      setDirty(false)
      setNotice('Saved. Click “Apply & reload” to load it into the running server.')
    } catch (err) {
      setNotice(errorMessage(err))
    }
  }

  const handleDelete = async (path: string) => {
    if (!window.confirm(`Delete "${path}"?`)) return
    try {
      await deleteScript.mutateAsync(path)
      if (selected === path) setSelected(null)
    } catch (err) {
      window.alert(errorMessage(err))
    }
  }

  const handleApply = async () => {
    if (!window.confirm('Restart the worldserver to load the current Lua scripts?')) return
    setNotice(null)
    try {
      await applyLua.mutateAsync()
      setNotice('Worldserver restarting — scripts are being loaded.')
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
        Failed to load Lua scripts.
      </div>
    )
  }

  return (
    <div className="space-y-4">
      {list && !list.elunaPresent && (
        <div className="rounded-md border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
          <div className="flex items-start gap-2">
            <AlertCircle className="mt-0.5 h-5 w-5 shrink-0 text-amber-600" />
            <div>
              <p className="font-medium">No Lua engine detected in this stack.</p>
              <p className="mt-1">
                Lua scripts only run when a Lua engine (<code>mod-ale</code>, the AzerothCore Lua
                Engine) is compiled into the worldserver. Add it from the module catalog, select it
                for this stack, and rebuild. You can still manage scripts here in the meantime.
              </p>
            </div>
          </div>
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <button
          onClick={() => fileInputRef.current?.click()}
          className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
        >
          <Upload className="h-4 w-4" /> Upload (.zip or files)
        </button>
        <button
          onClick={handleNewFile}
          className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50"
        >
          <FilePlus className="h-4 w-4" /> New script
        </button>
        <button
          onClick={handleApply}
          disabled={applyLua.isPending}
          className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
        >
          {applyLua.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <RotateCw className="h-4 w-4" />}
          Apply &amp; reload
        </button>
        <input
          ref={fileInputRef}
          type="file"
          multiple
          className="hidden"
          onChange={(e) => {
            if (e.target.files) uploadFiles(e.target.files)
            e.target.value = ''
          }}
        />
      </div>

      <div
        onDragOver={(e) => {
          e.preventDefault()
          setDragOver(true)
        }}
        onDragLeave={() => setDragOver(false)}
        onDrop={(e) => {
          e.preventDefault()
          setDragOver(false)
          if (e.dataTransfer.files?.length) uploadFiles(e.dataTransfer.files)
        }}
        className={`rounded-lg border-2 border-dashed px-4 py-3 text-center text-sm ${
          dragOver ? 'border-blue-400 bg-blue-50 text-blue-700' : 'border-gray-300 text-gray-500'
        }`}
      >
        Drag &amp; drop a <code>.zip</code> (keeps folder structure) or individual{' '}
        <code>.lua</code> files here.
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-[260px_1fr]">
        <div className="rounded-lg border border-gray-200 bg-white">
          <div className="border-b border-gray-100 px-3 py-2 text-xs font-semibold uppercase tracking-wide text-gray-500">
            Scripts {list && list.files.length > 0 && `(${formatSize(list.totalSize)})`}
          </div>
          {list && list.files.length === 0 ? (
            <p className="px-3 py-6 text-center text-sm text-gray-400">No scripts yet.</p>
          ) : (
            <ul className="max-h-[60vh] overflow-y-auto py-1">
              {list?.files.map((f) => (
                <li key={f.path} className="group flex items-center">
                  <button
                    onClick={() => (f.isDirectory ? undefined : setSelected(f.path))}
                    disabled={f.isDirectory}
                    className={`flex min-w-0 flex-1 items-center gap-2 px-3 py-1.5 text-left text-sm ${
                      selected === f.path ? 'bg-blue-50 text-blue-700' : 'text-gray-700 hover:bg-gray-50'
                    } ${f.isDirectory ? 'cursor-default' : ''}`}
                  >
                    {f.isDirectory ? (
                      <Folder className="h-4 w-4 shrink-0 text-gray-400" />
                    ) : (
                      <FileCode className="h-4 w-4 shrink-0 text-gray-400" />
                    )}
                    <span className="truncate font-mono text-xs">{f.path}</span>
                  </button>
                  <button
                    onClick={() => handleDelete(f.path)}
                    className="px-2 text-gray-300 hover:text-red-600 group-hover:text-gray-400"
                    aria-label={`Delete ${f.path}`}
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>

        <div className="min-w-0">
          {!selected ? (
            <div className="rounded-lg border border-dashed border-gray-300 py-16 text-center text-sm text-gray-500">
              Select a script to edit, or create a new one.
            </div>
          ) : !isEditable(selected) ? (
            <div className="rounded-lg border border-gray-200 bg-gray-50 px-4 py-8 text-center text-sm text-gray-500">
              This file type can't be edited here. You can still delete it or replace it via upload.
            </div>
          ) : (
            <div className="rounded-lg border border-gray-200 bg-white">
              <div className="flex items-center justify-between border-b border-gray-100 px-4 py-2">
                <span className="truncate font-mono text-sm text-gray-700">{selected}</span>
                <button
                  onClick={handleSave}
                  disabled={!dirty || saveScript.isPending}
                  className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
                >
                  {saveScript.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                  Save
                </button>
              </div>
              {isFetching && !file ? (
                <div className="flex items-center justify-center py-16">
                  <Loader2 className="h-6 w-6 animate-spin text-blue-500" />
                </div>
              ) : (
                <textarea
                  value={text}
                  onChange={(e) => {
                    setText(e.target.value)
                    setDirty(true)
                  }}
                  spellCheck={false}
                  className="block h-[55vh] w-full resize-y rounded-b-lg border-0 bg-gray-900 px-4 py-3 font-mono text-xs leading-relaxed text-gray-100 focus:outline-none focus:ring-0"
                />
              )}
            </div>
          )}
          {notice && <p className="mt-2 text-sm text-gray-600">{notice}</p>}
        </div>
      </div>
    </div>
  )
}
