import { useRef, useState } from 'react'
import { Loader2, Upload, CheckCircle2, XCircle, RefreshCw, HardDrive } from 'lucide-react'
import { useClientBaseInfo, useUploadBaseClient, useRescanBaseClient } from '@/hooks/useClient'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import ClientFileBrowser from '@/components/client/ClientFileBrowser'
import RebuildClientManifestButton from '@/components/client/RebuildClientManifestButton'
import ForceVerifyButton from '@/components/launcher/ForceVerifyButton'
import ConfigWtfTemplateEditor from '@/components/client/ConfigWtfTemplateEditor'

// Archive formats the backend can extract (auto-detected from content). Used both for the file
// picker's `accept` hint and to validate drag-and-dropped files before uploading.
const ACCEPTED_EXTENSIONS = ['.zip', '.rar', '.7z', '.tar', '.tar.gz', '.tgz', '.tar.bz2', '.tbz2', '.tar.xz', '.gz', '.bz2', '.xz']
const ACCEPT_ATTR = ACCEPTED_EXTENSIONS.join(',')

function hasAcceptedExtension(name: string): boolean {
  const lower = name.toLowerCase()
  return ACCEPTED_EXTENSIONS.some((ext) => lower.endsWith(ext))
}

function formatBytes(bytes: number): string {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(2)} ${units[unit]}`
}

export default function ClientTab({ stackId }: { stackId: string }) {
  const { data: info, isLoading, error, refetch, isFetching } = useClientBaseInfo(stackId)
  const uploadBase = useUploadBaseClient(stackId)
  const rescanBase = useRescanBaseClient(stackId)

  const [uploadPercent, setUploadPercent] = useState<number | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [pageError, setPageError] = useState<string | null>(null)
  const [dragActive, setDragActive] = useState(false)
  const fileRef = useRef<HTMLInputElement | null>(null)

  const onFile = async (file?: File | null) => {
    if (!file) return
    setPageError(null)
    setMessage(null)
    if (!hasAcceptedExtension(file.name)) {
      setPageError(`Unsupported file type. Accepted archives: ${ACCEPTED_EXTENSIONS.join(', ')}.`)
      return
    }
    setUploadPercent(0)
    try {
      await uploadBase.mutateAsync({ file, onProgress: setUploadPercent })
      setMessage('Base client uploaded and installed. This stack will serve it as its base layer.')
      setTimeout(() => setMessage(null), 5000)
    } catch (err) {
      setPageError(errorMessage(err))
    } finally {
      setUploadPercent(null)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  const onRescan = async () => {
    setPageError(null)
    setMessage(null)
    try {
      await rescanBase.mutateAsync()
      setMessage('Base client volume re-seeded.')
      setTimeout(() => setMessage(null), 4000)
    } catch (err) {
      setPageError(errorMessage(err))
    }
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-20 text-gray-500">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    )
  }

  if (error) {
    return <div className="rounded-md bg-red-50 p-4 text-red-700">{errorMessage(error)}</div>
  }

  const uploading = uploadBase.isPending || uploadPercent !== null

  const onDragOver = (e: React.DragEvent) => {
    e.preventDefault()
    if (!uploading) setDragActive(true)
  }

  const onDragLeave = (e: React.DragEvent) => {
    e.preventDefault()
    setDragActive(false)
  }

  const onDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setDragActive(false)
    if (uploading) return
    void onFile(e.dataTransfer.files?.[0])
  }

  return (
    <div className="space-y-8">
      <div>
        <p className="text-sm text-gray-600">
          Upload this stack&rsquo;s <strong>base WoW client</strong>. The stack runs a client container
          that serves this base plus its own patch overlay to the launcher. Each stack keeps its own
          base client in a Docker volume on the stack engine (for VPC stacks, that is the remote host).
          Stopping the client container does <strong>not</strong> remove the uploaded base client.
        </p>
      </div>

      {info?.inspectionWarning && (
        <div className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
          {info.inspectionWarning}
          {info.volumeExists && !info.exists && (
            <span className="mt-1 block text-amber-800">
              If SSH to the VPC was interrupted, open the VPC overview tab and reconnect, then click
              Refresh below.
            </span>
          )}
        </div>
      )}

      {pageError && <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{pageError}</div>}

      <section className="rounded-lg border bg-white p-6 shadow-sm">
        <h3 className="mb-1 text-lg font-semibold text-gray-900">Base client</h3>
        <p className="mb-4 text-sm text-gray-500">
          Upload an archive of a clean 3.3.5a client (zip, rar, 7z or tar - it may be wrapped in a
          single top-level folder). This replaces any previous base and is validated for{' '}
          <span className="font-mono">Wow.exe</span> and <span className="font-mono">Data/*.MPQ</span>.
          Uploads can be large (~17 GB).
        </p>

        <div className="mb-6 grid grid-cols-1 gap-4 sm:grid-cols-2">
          <div className="rounded-md border bg-gray-50/50 p-4">
            <div className="flex items-center justify-between gap-2 text-sm font-medium text-gray-700">
              <span className="inline-flex items-center gap-2">
                <HardDrive className="h-4 w-4" /> Current base
              </span>
              <button
                type="button"
                onClick={() => refetch()}
                disabled={isFetching}
                className="inline-flex items-center gap-1 rounded border bg-white px-2 py-1 text-xs font-normal text-gray-600 hover:bg-gray-50 disabled:opacity-50"
              >
                {isFetching ? <Loader2 className="h-3 w-3 animate-spin" /> : <RefreshCw className="h-3 w-3" />}
                Refresh
              </button>
            </div>
            {info?.exists ? (
              <div className="mt-2 space-y-1 text-sm text-gray-600">
                <div className="font-mono text-xs text-gray-500">{info.gamePath}</div>
                <div>{info.fileCount.toLocaleString()} files · {formatBytes(info.totalSize)}</div>
                <div className="flex items-center gap-1">
                  {info.hasWowExe ? (
                    <CheckCircle2 className="h-4 w-4 text-green-600" />
                  ) : (
                    <XCircle className="h-4 w-4 text-red-500" />
                  )}
                  Wow.exe
                </div>
                <div className="flex items-center gap-1">
                  {info.hasDataMpq ? (
                    <CheckCircle2 className="h-4 w-4 text-green-600" />
                  ) : (
                    <XCircle className="h-4 w-4 text-red-500" />
                  )}
                  Data/*.MPQ
                </div>
              </div>
            ) : (
              <p className="mt-2 text-sm text-gray-400">
                {info?.volumeExists
                  ? 'No client files detected in the client-base volume.'
                  : 'No base client uploaded yet.'}
              </p>
            )}
          </div>

          <div className="flex flex-col justify-center gap-3">
            <label
              onDragOver={onDragOver}
              onDragLeave={onDragLeave}
              onDrop={onDrop}
              className={`flex cursor-pointer flex-col items-center justify-center gap-2 rounded-md border-2 border-dashed px-4 py-6 text-center text-sm transition-colors ${
                dragActive
                  ? 'border-blue-500 bg-blue-50 text-blue-700'
                  : uploading
                    ? 'border-gray-200 bg-gray-50 text-gray-400'
                    : 'border-gray-300 bg-gray-50/50 text-gray-600 hover:border-blue-400 hover:bg-blue-50/40'
              }`}
            >
              {uploading ? (
                <Loader2 className="h-6 w-6 animate-spin text-blue-600" />
              ) : (
                <Upload className="h-6 w-6 text-blue-600" />
              )}
              <span className="font-medium">
                {uploading
                  ? uploadPercent !== null && uploadPercent < 100
                    ? `Uploading… ${uploadPercent}%`
                    : 'Installing…'
                  : dragActive
                    ? 'Drop the archive to upload'
                    : 'Drag & drop a client archive here, or click to browse'}
              </span>
              {!uploading && (
                <span className="text-xs text-gray-400">zip · rar · 7z · tar / tar.gz</span>
              )}
              <input
                ref={fileRef}
                type="file"
                accept={ACCEPT_ATTR}
                className="hidden"
                disabled={uploading}
                onChange={(e) => onFile(e.target.files?.[0])}
              />
            </label>

            <button
              onClick={onRescan}
              disabled={rescanBase.isPending || uploading}
              className="inline-flex items-center justify-center gap-2 rounded-md border bg-white px-4 py-2 text-sm hover:bg-gray-50 disabled:opacity-50"
            >
              {rescanBase.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <RefreshCw className="h-4 w-4" />
              )}
              Re-seed base volume
            </button>
          </div>
        </div>

        {uploadPercent !== null && uploadPercent < 100 && (
          <div className="mb-4 h-2 w-full overflow-hidden rounded bg-gray-200">
            <div className="h-full bg-blue-600 transition-all" style={{ width: `${uploadPercent}%` }} />
          </div>
        )}

        {message && (
          <div className="inline-flex items-center gap-1 text-sm text-green-600">
            <CheckCircle2 className="h-4 w-4" /> {message}
          </div>
        )}
      </section>

      <ConfigWtfTemplateEditor stackId={stackId} />

      {info?.exists && (
        <section className="rounded-lg border bg-white p-6 shadow-sm">
          <ForceVerifyButton stackId={stackId} />
        </section>
      )}

      {info?.exists && (
        <section className="rounded-lg border bg-white p-6 shadow-sm">
          <RebuildClientManifestButton stackId={stackId} />
          <ClientFileBrowser stackId={stackId} />
        </section>
      )}
    </div>
  )
}
