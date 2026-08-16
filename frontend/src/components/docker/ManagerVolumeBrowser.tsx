import { useState } from 'react'
import { ChevronRight, Folder, Loader2, Trash2 } from 'lucide-react'
import { formatBytes } from '@/components/docker/DockerDiskUsage'
import {
  useDeleteManagerFile,
  useManagerFiles,
  usePlatformKeys,
} from '@/hooks/useStackDocker'
import { apiErrorMessage } from '@/lib/utils'
import type { DockerManagerFileEntryDto } from '@/types/docker.types'

export function ManagerVolumeBrowser() {
  const [path, setPath] = useState('')
  const [confirmDelete, setConfirmDelete] = useState<DockerManagerFileEntryDto | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const { data: files, isLoading, refetch } = useManagerFiles(path)
  const { data: platformKeys } = usePlatformKeys()
  const deleteFile = useDeleteManagerFile()

  const crumbs = path ? path.split('/') : []

  const navigateTo = (index: number) => {
    if (index < 0) {
      setPath('')
      return
    }
    setPath(crumbs.slice(0, index + 1).join('/'))
  }

  const openDir = (entry: DockerManagerFileEntryDto) => {
    if (entry.isDirectory) {
      setPath(entry.relativePath)
    }
  }

  const handleDelete = async () => {
    if (!confirmDelete) return
    const target = confirmDelete
    setConfirmDelete(null)
    setNotice(null)
    setError(null)
    try {
      const res = await deleteFile.mutateAsync(target.relativePath)
      setNotice(res.data.message + (res.data.freedBytes ? ` Freed ${formatBytes(res.data.freedBytes)}.` : ''))
      await refetch()
    } catch (err) {
      setError(apiErrorMessage(err))
    }
  }

  return (
    <div className="border-t border-blue-100 px-4 py-4 space-y-4">
      {platformKeys && (
        <div className="rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-700">
          <p className="font-medium text-gray-900">Platform keys on manager volume</p>
          <p className="mt-1 text-gray-600">{platformKeys.detail}</p>
          <ul className="mt-2 space-y-1">
            {platformKeys.keys.map((key) => (
              <li key={key.name} className="flex items-center gap-2">
                <span className={`inline-block h-2 w-2 rounded-full ${key.present ? 'bg-green-500' : 'bg-red-500'}`} />
                <span className="font-mono">{key.name}</span>
                <span className="text-gray-500">{key.present ? 'present' : 'missing — may require re-login or external stack reconnect'}</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-1 text-sm text-gray-700">
          <button type="button" onClick={() => navigateTo(-1)} className="font-medium hover:underline">
            /
          </button>
          {crumbs.map((part, i) => (
            <span key={i} className="inline-flex items-center gap-1">
              <ChevronRight className="h-3 w-3 text-gray-400" />
              <button type="button" onClick={() => navigateTo(i)} className="hover:underline">
                {part}
              </button>
            </span>
          ))}
        </div>
      </div>

      {notice && <p className="text-sm text-green-800">{notice}</p>}
      {error && <p className="text-sm text-red-700">{error}</p>}

      {isLoading ? (
        <div className="flex items-center gap-2 text-sm text-gray-600">
          <Loader2 className="h-4 w-4 animate-spin" />
          Loading…
        </div>
      ) : (
        <div className="overflow-x-auto rounded-md border border-gray-200">
          <table className="min-w-full text-left text-sm">
            <thead className="bg-gray-50 text-xs uppercase text-gray-500">
              <tr>
                <th className="px-3 py-2">Name</th>
                <th className="px-3 py-2">Size</th>
                <th className="px-3 py-2">Notes</th>
                <th className="px-3 py-2" />
              </tr>
            </thead>
            <tbody>
              {(files?.entries ?? []).map((entry) => (
                <tr key={entry.relativePath} className="border-t border-gray-100">
                  <td className="px-3 py-2">
                    <button
                      type="button"
                      onClick={() => openDir(entry)}
                      disabled={!entry.isDirectory}
                      className={`inline-flex items-center gap-2 ${entry.isDirectory ? 'font-medium text-blue-700 hover:underline' : 'text-gray-900'}`}
                    >
                      {entry.isDirectory && <Folder className="h-4 w-4 shrink-0 text-amber-600" />}
                      {entry.name}
                    </button>
                  </td>
                  <td className="px-3 py-2 text-gray-600">
                    {entry.isDirectory ? '—' : formatBytes(entry.sizeBytes)}
                  </td>
                  <td className="px-3 py-2 text-xs text-gray-500">{entry.detail ?? '—'}</td>
                  <td className="px-3 py-2 text-right">
                    {entry.isDeletable ? (
                      <button
                        type="button"
                        onClick={() => setConfirmDelete(entry)}
                        className="inline-flex items-center gap-1 text-xs text-red-700 hover:underline"
                      >
                        <Trash2 className="h-3 w-3" />
                        Delete
                      </button>
                    ) : (
                      <span className="text-xs text-gray-400">Protected</span>
                    )}
                  </td>
                </tr>
              ))}
              {(files?.entries ?? []).length === 0 && (
                <tr>
                  <td colSpan={4} className="px-3 py-6 text-center text-sm text-gray-500">
                    {files?.exists ? 'Empty directory.' : 'Directory not found.'}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {confirmDelete && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="max-w-md rounded-lg bg-white p-5 shadow-lg">
            <h3 className="font-medium text-gray-900">Delete from manager volume?</h3>
            <p className="mt-2 text-sm text-gray-600">
              Remove <span className="font-mono">{confirmDelete.relativePath}</span>? This cannot be undone.
            </p>
            <div className="mt-4 flex justify-end gap-2">
              <button type="button" onClick={() => setConfirmDelete(null)} className="rounded-md border px-3 py-1.5 text-sm">
                Cancel
              </button>
              <button
                type="button"
                onClick={() => void handleDelete()}
                className="rounded-md bg-red-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-red-700"
              >
                Delete
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
