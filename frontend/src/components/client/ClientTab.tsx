import { useEffect, useRef, useState } from 'react'
import { Download, Loader2, Upload, CheckCircle2, XCircle, RefreshCw, HardDrive, Trash2 } from 'lucide-react'
import {
  useClientBaseInfo,
  useUploadBaseClient,
  useRescanBaseClient,
  useDownloadBaseClient,
  usePurgeClientContent,
} from '@/hooks/useClient'
import { useClientJobContext } from '@/contexts/ClientJobContext'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import ClientFileBrowser from '@/components/client/ClientFileBrowser'
import RebuildClientManifestButton from '@/components/client/RebuildClientManifestButton'
import ForceVerifyButton from '@/components/launcher/ForceVerifyButton'
import ConfigWtfTemplateEditor from '@/components/client/ConfigWtfTemplateEditor'
import DownloadClientUrlDialog from '@/components/client/DownloadClientUrlDialog'
import ClientJobProgress from '@/components/client/ClientJobProgress'
import PurgeClientDataDialog from '@/components/client/PurgeClientDataDialog'

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

function formatBuiltAt(iso?: string | null): string {
  if (!iso) return 'not built yet'
  const built = new Date(iso)
  if (Number.isNaN(built.getTime())) return 'unknown'
  const secondsAgo = Math.max(0, Math.round((Date.now() - built.getTime()) / 1000))
  if (secondsAgo < 60) return 'just now'
  if (secondsAgo < 3600) return `${Math.round(secondsAgo / 60)} min ago`
  if (secondsAgo < 86400) return `${Math.round(secondsAgo / 3600)} h ago`
  return built.toLocaleString()
}

export default function ClientTab({ stackId }: { stackId: string }) {
  const { data: info, isLoading, error, refetch, isFetching } = useClientBaseInfo(stackId)
  const uploadBase = useUploadBaseClient(stackId)
  const rescanBase = useRescanBaseClient(stackId)
  const downloadBase = useDownloadBaseClient(stackId)
  const purgeContent = usePurgeClientContent(stackId)
  const { job: clientJob, isClientBusy, applyStatus: applyClientStatus, setUploading } = useClientJobContext()
  const downloading = isClientBusy && clientJob?.action === 'DownloadBase'
  const installing = isClientBusy && clientJob?.action === 'InstallBase'
  const purging = isClientBusy && clientJob?.action === 'PurgeContent'

  useEffect(() => {
    if (!installing && !downloading && !purging) {
      return
    }
    const timer = window.setInterval(() => {
      void refetch()
    }, 3000)
    return () => window.clearInterval(timer)
  }, [installing, downloading, purging, refetch])

  useEffect(() => {
    const action = clientJob?.action
    if (action !== 'DownloadBase' && action !== 'InstallBase' && action !== 'PurgeContent') {
      return
    }
    if (clientJob?.success) {
      void refetch()
      setShowPurgeDialog(false)
      setMessage(
        action === 'InstallBase'
          ? 'Base client uploaded and installed. This stack will serve it as its base layer.'
          : action === 'DownloadBase'
            ? 'Base client downloaded and installed.'
            : 'Client data purged. Upload a base client below, then reapply this stack\u2019s patches from the Patches tab.',
      )
      setTimeout(() => setMessage(null), action === 'PurgeContent' ? 20000 : 5000)
    }
    if (clientJob?.error) {
      setPageError(clientJob.error)
    }
  }, [clientJob, refetch])

  const [uploadPercent, setUploadPercent] = useState<number | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [pageError, setPageError] = useState<string | null>(null)
  const [dragActive, setDragActive] = useState(false)
  const [showDownloadDialog, setShowDownloadDialog] = useState(false)
  const [showPurgeDialog, setShowPurgeDialog] = useState(false)
  const [purgeError, setPurgeError] = useState<string | null>(null)
  const fileRef = useRef<HTMLInputElement | null>(null)

  const uploading = uploadBase.isPending || (uploadPercent !== null && uploadPercent < 100)
  const busy = uploading || installing || downloading || purging

  // The archive streams from this browser, so a reload or tab close aborts it mid-body and the
  // manager never gets far enough to queue the install job. The background job that follows survives
  // on its own, which is why only the upload leg is guarded.
  useEffect(() => {
    if (!uploading) return
    const handler = (e: BeforeUnloadEvent) => {
      e.preventDefault()
      e.returnValue = ''
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [uploading])

  // Published so the stack page can warn before a tab switch unmounts this component and takes the
  // progress bar with it.
  useEffect(() => {
    setUploading(uploading)
    return () => setUploading(false)
  }, [uploading, setUploading])

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
      const job = await uploadBase.mutateAsync({ file, onProgress: setUploadPercent })
      applyClientStatus(job)
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
      setMessage('Launcher manifest refreshed.')
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

  const onPurge = async () => {
    setPurgeError(null)
    setPageError(null)
    setMessage(null)
    try {
      applyClientStatus(await purgeContent.mutateAsync())
    } catch (err) {
      setPurgeError(errorMessage(err))
    }
  }

  const onDragOver = (e: React.DragEvent) => {
    e.preventDefault()
    if (!busy) setDragActive(true)
  }

  const onDragLeave = (e: React.DragEvent) => {
    e.preventDefault()
    setDragActive(false)
  }

  const onDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setDragActive(false)
    if (busy) return
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

      {info?.manifestWarning && (
        <div className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
          {info.manifestWarning}
        </div>
      )}

      {pageError && <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{pageError}</div>}

      <section className="rounded-lg border bg-white p-6 shadow-sm">
        <h3 className="mb-1 text-lg font-semibold text-gray-900">Base client</h3>
        <p className="mb-4 text-sm text-gray-500">
          Upload an archive of a clean 3.3.5a client (zip, rar, 7z or tar), or download one from a
          direct URL. Nested folders are searched until{' '}
          <span className="font-mono">Wow.exe</span> and <span className="font-mono">Data/*.MPQ</span>{' '}
          are found. This replaces any previous base. Uploads can be large (~17 GB).
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

            {/* The volume figures above say what is stored; this says what players are offered. They
                diverge whenever a manifest refresh fails, which is the case worth spotting. */}
            <div className="mt-4 border-t pt-3">
              <div className="text-sm font-medium text-gray-700">Served to launchers</div>
              {info?.manifest ? (
                <div className="mt-1 space-y-1 text-sm text-gray-600">
                  <div>
                    {info.manifest.fileCount.toLocaleString()} files ·{' '}
                    {formatBytes(info.manifest.totalSize)}
                  </div>
                  <div className="text-xs text-gray-500">
                    {info.manifest.baseFileCount.toLocaleString()} base ·{' '}
                    {info.manifest.managedFileCount.toLocaleString()} overlay
                  </div>
                  <div className="text-xs text-gray-500">
                    Version <span className="font-mono">{info.manifest.version.slice(0, 12) || '—'}</span> ·
                    built {formatBuiltAt(info.manifest.builtAtUtc)}
                  </div>
                  {!info.manifest.signed && (
                    <div className="text-xs text-amber-700">
                      Unsigned: launchers cannot verify this manifest. Provision a signing key.
                    </div>
                  )}
                </div>
              ) : (
                <p className="mt-1 text-sm text-gray-400">
                  The client container is not running or could not be reached, so what launchers
                  currently receive is unknown.
                </p>
              )}
            </div>
          </div>

          <div className="flex flex-col justify-center gap-3">
            <label
              onDragOver={onDragOver}
              onDragLeave={onDragLeave}
              onDrop={onDrop}
              className={`flex cursor-pointer flex-col items-center justify-center gap-2 rounded-md border-2 border-dashed px-4 py-6 text-center text-sm transition-colors ${
                dragActive
                  ? 'border-blue-500 bg-blue-50 text-blue-700'
                  : busy
                    ? 'border-gray-200 bg-gray-50 text-gray-400'
                    : 'border-gray-300 bg-gray-50/50 text-gray-600 hover:border-blue-400 hover:bg-blue-50/40'
              }`}
            >
              {busy ? (
                <Loader2 className="h-6 w-6 animate-spin text-blue-600" />
              ) : (
                <Upload className="h-6 w-6 text-blue-600" />
              )}
              <span className="font-medium">
                {installing
                  ? clientJob?.message || 'Installing into the client volume…'
                  : uploading
                    ? `Uploading… ${uploadPercent ?? 0}%`
                    : downloading
                      ? clientJob?.message || 'Downloading…'
                      : purging
                        ? clientJob?.message || 'Purging client data…'
                        : dragActive
                          ? 'Drop the archive to upload'
                          : 'Drag & drop a client archive here, or click to browse'}
              </span>
              {!busy && (
                <span className="text-xs text-gray-400">zip · rar · 7z · tar / tar.gz</span>
              )}
              <input
                ref={fileRef}
                type="file"
                accept={ACCEPT_ATTR}
                className="hidden"
                disabled={busy}
                onChange={(e) => onFile(e.target.files?.[0])}
              />
            </label>

            <button
              type="button"
              onClick={() => {
                setPageError(null)
                setMessage(null)
                setShowDownloadDialog(true)
              }}
              disabled={busy || downloadBase.isPending}
              title="Download a 3.3.5a client archive from a direct URL"
              className="inline-flex items-center justify-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {downloading || downloadBase.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <Download className="h-4 w-4" />
              )}
              {downloading ? clientJob?.message || 'Downloading…' : 'Download from URL'}
            </button>

            <button
              onClick={onRescan}
              disabled={rescanBase.isPending || busy}
              title="Re-read the volume and rebuild the manifest launchers download against. Unchanged files are not re-hashed."
              className="inline-flex items-center justify-center gap-2 rounded-md border bg-white px-4 py-2 text-sm hover:bg-gray-50 disabled:opacity-50"
            >
              {rescanBase.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <RefreshCw className="h-4 w-4" />
              )}
              Refresh launcher manifest
            </button>
          </div>
        </div>

        {uploadPercent !== null && uploadPercent < 100 && (
          <div className="mb-4 h-2 w-full overflow-hidden rounded bg-gray-200">
            <div className="h-full bg-blue-600 transition-all" style={{ width: `${uploadPercent}%` }} />
          </div>
        )}

        {(installing || downloading || purging) && (
          <div className="mb-4">
            <ClientJobProgress
              message={clientJob?.message}
              bytesCompleted={clientJob?.bytesCompleted}
              bytesTotal={clientJob?.bytesTotal}
            />
          </div>
        )}

        {message && (
          <div className="inline-flex items-center gap-1 text-sm text-green-600">
            <CheckCircle2 className="h-4 w-4" /> {message}
          </div>
        )}
      </section>

      {showDownloadDialog ? (
        <DownloadClientUrlDialog
          onClose={() => setShowDownloadDialog(false)}
          onSubmit={async (url) => {
            const job = await downloadBase.mutateAsync(url)
            applyClientStatus(job)
          }}
        />
      ) : null}

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

      <section className="rounded-lg border border-red-200 bg-red-50/40 p-6">
        <h3 className="text-lg font-semibold text-red-900">Danger zone</h3>
        <p className="mt-1 text-sm text-red-800">
          Purging deletes the base client, every published patch MPQ and addon, and the cached
          manifest, leaving the stack with nothing to serve. Use it when the client is in a state
          uploading over the top will not fix. Afterwards, upload a base client and reapply this
          stack&rsquo;s patches.
        </p>
        <button
          type="button"
          onClick={() => {
            setPurgeError(null)
            setShowPurgeDialog(true)
          }}
          disabled={busy}
          className="mt-4 inline-flex items-center gap-2 rounded-md border border-red-300 bg-white px-4 py-2 text-sm font-medium text-red-700 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {purging ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
          {purging ? 'Purging…' : 'Purge client data'}
        </button>
      </section>

      {showPurgeDialog ? (
        <PurgeClientDataDialog
          isPurging={purging || purgeContent.isPending}
          error={purgeError}
          onCancel={() => setShowPurgeDialog(false)}
          onConfirm={() => void onPurge()}
        />
      ) : null}
    </div>
  )
}
