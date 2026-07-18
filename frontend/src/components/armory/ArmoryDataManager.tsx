import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Loader2, Upload, CheckCircle2, XCircle, Boxes, Layers, Database, Download, Trash2 } from 'lucide-react'
import {
  useArmoryAssetsInfo,
  useUploadArmoryData,
  useUploadArmoryStatic,
  useDeleteArmoryStatic,
  useSyncArmoryDbcs,
  armoryAssetsInfoKey,
} from '@/hooks/useArmoryAssets'
import { useArmoryJobContext } from '@/contexts/ArmoryJobContext'
import { apiErrorMessage as errorMessage } from '@/lib/utils'
import ArmoryDataBrowser from '@/components/armory/ArmoryDataBrowser'

const ACCEPTED_EXTENSIONS = ['.zip', '.rar', '.7z', '.tar', '.tar.gz', '.tgz', '.tar.bz2', '.tbz2', '.tar.xz', '.gz', '.bz2', '.xz']
const ACCEPT_ATTR = ACCEPTED_EXTENSIONS.join(',')

const EXPECTED_DATA_FOLDERS = ['bone', 'dbc', 'dbc_transmog', 'meta', 'mo3', 'progression', 'textures']

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

/**
 * Manages a stack's armory asset bundles: the 3D model-viewer dataset (armory.data.zip +
 * armory.textures.zip) and the static web assets (armory.static.zip). Each stack has its own bundles,
 * so armory data is uploaded per stack.
 */
export default function ArmoryDataManager({ stackId }: { stackId: string }) {
  const qc = useQueryClient()
  const { data: info, isLoading, error } = useArmoryAssetsInfo(stackId)
  const uploadData = useUploadArmoryData(stackId)
  const uploadStatic = useUploadArmoryStatic(stackId)
  const deleteStatic = useDeleteArmoryStatic(stackId)
  const syncDbcs = useSyncArmoryDbcs(stackId)
  const { job, isArmoryBusy, applyStatus } = useArmoryJobContext()

  const [message, setMessage] = useState<string | null>(null)
  const [pageError, setPageError] = useState<string | null>(null)

  const flash = (text: string) => {
    setMessage(text)
    setTimeout(() => setMessage(null), 5000)
  }

  // The rebuild / DBC sync run as detached background jobs. When one finishes, refresh the asset info
  // so prompts and the dataset summary (cleared/updated server-side) reflect the new state.
  const prevRunningRef = useRef(false)
  useEffect(() => {
    const running = job?.isRunning ?? false
    const relevant = job?.action === 'Rebuild' || job?.action === 'SyncDbc'
    if (prevRunningRef.current && !running && relevant) {
      qc.invalidateQueries({ queryKey: armoryAssetsInfoKey(stackId) })
      if (job?.success) flash(job.message || 'Done.')
      else if (job?.error) setPageError(job.error)
    }
    prevRunningRef.current = running
  }, [job?.isRunning, job?.action, job?.success, job?.error, job?.message, qc, stackId])

  const dbcSyncing = syncDbcs.isPending || (isArmoryBusy && job?.action === 'SyncDbc')

  // Keep the DBC sync log scrolled to the newest line as it streams in.
  const dbcLogRef = useRef<HTMLPreElement>(null)
  const dbcLogCount = job?.action === 'SyncDbc' ? job.recentLogs?.length ?? 0 : 0
  useEffect(() => {
    const el = dbcLogRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [dbcLogCount])

  const onSyncDbcs = async () => {
    setPageError(null)
    setMessage(null)
    try {
      const status = await syncDbcs.mutateAsync()
      applyStatus(status)
    } catch (err) {
      setPageError(errorMessage(err))
    }
  }

  const onDeleteStatic = async () => {
    if (!window.confirm('Delete uploaded static web assets for this stack? Model-viewer data and generated styling assets are preserved.')) {
      return
    }

    setPageError(null)
    setMessage(null)
    try {
      await deleteStatic.mutateAsync()
      flash('Static web assets deleted. Rebuild the armory image to apply the fallback assets.')
    } catch (err) {
      setPageError(errorMessage(err))
    }
  }

  const dbcCsvPresent = (info?.dataFolders ?? []).includes('dbc')
  const serverDbcsReady = (info?.serverDbcFileCount ?? 0) > 0

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16 text-gray-500">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    )
  }

  if (error) {
    return <div className="rounded-md bg-red-50 p-4 text-red-700">{errorMessage(error)}</div>
  }

  return (
    <div className="space-y-6">
      {pageError && <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{pageError}</div>}
      {message && (
        <div className="inline-flex items-center gap-1 rounded-md bg-green-50 px-3 py-2 text-sm text-green-700">
          <CheckCircle2 className="h-4 w-4" /> {message}
        </div>
      )}

      {/* Armory assets: the model-viewer dataset and the static web bundle now live together under one
          static/ tree, so they share a single card. */}
      <section className="rounded-lg border bg-white p-6 shadow-sm">
        <div className="mb-1 flex items-center gap-2">
          <Boxes className="h-5 w-5 text-blue-600" />
          <h2 className="text-lg font-semibold text-gray-900">Armory Assets</h2>
        </div>
        <p className="mb-4 text-sm text-gray-500">
          Upload this stack&rsquo;s armory data bundle (<span className="font-mono">armory.data.zip</span>
          {', '}
          <span className="font-mono">armory.textures.zip</span>) and its static web bundle (
          <span className="font-mono">armory.static.zip</span>). Data and textures uploads merge on the
          stack&rsquo;s <span className="font-mono">armory-assets</span> volume — uploading one does not
          remove folders from the other. The data bundle can include the full model-viewer dataset and/or
          just the <span className="font-mono">progression/</span> folder (dungeon/raid/world boss card
          artwork for Tracking pages). Data assets are served live by the stack&rsquo;s armory sidecar;
          static web assets are baked into the armory image, so changing them needs an image rebuild.
        </p>

        <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
          {/* Model-viewer dataset */}
          <div className="space-y-3">
            <div className="flex items-center gap-2 text-sm font-medium text-gray-700">
              <Boxes className="h-4 w-4 text-blue-600" /> Model-viewer data
            </div>
            <div className="rounded-md border bg-gray-50/50 p-4 text-sm">
              <div className="font-medium text-gray-700">Current dataset</div>
              {info && (info.dataFileCount > 0 || info.dataUploaded || info.dataOnStackVolume) ? (
                <div className="mt-2 space-y-2 text-gray-600">
                  <div>{info.dataFileCount.toLocaleString()} files · {formatBytes(info.dataSize)}</div>
                  {info.dataOnStackVolume && (
                    <p className="text-xs text-green-700">
                      Stored on this stack&rsquo;s <span className="font-mono">armory-assets</span> Docker volume
                      (served live by the armory sidecar, not on the manager).
                    </p>
                  )}
                  {!info.dataUploaded && (
                    <p className="text-xs text-amber-600">
                      Files are present, but <span className="font-mono">meta/</span> is missing &mdash; the 3D
                      viewer stays disabled until you also upload <span className="font-mono">armory.data.zip</span>.
                    </p>
                  )}
                  <div className="flex flex-wrap gap-1.5">
                    {EXPECTED_DATA_FOLDERS.map((folder) => {
                      const present = info.dataFolders.includes(folder)
                      return (
                        <span
                          key={folder}
                          className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs ${
                            present
                              ? 'border-green-200 bg-green-50 text-green-700'
                              : 'border-gray-200 bg-gray-50 text-gray-400'
                          }`}
                        >
                          {present ? <CheckCircle2 className="h-3 w-3" /> : <XCircle className="h-3 w-3" />}
                          {folder}
                        </span>
                      )
                    })}
                  </div>
                </div>
              ) : (
                <p className="mt-2 text-gray-400">No dataset uploaded yet. The 3D model viewer is disabled.</p>
              )}
            </div>

            <DropZone
              label="Drag & drop armory.data.zip / armory.textures.zip"
              hook={uploadData}
              onError={setPageError}
              onDone={() => flash('Model-viewer dataset updated. This stack now serves it.')}
            />
          </div>

          {/* Static web assets */}
          <div className="space-y-3">
            <div className="flex items-center gap-2 text-sm font-medium text-gray-700">
              <Layers className="h-4 w-4 text-blue-600" /> Static web assets
            </div>
            <div className="rounded-md border bg-gray-50/50 p-4 text-sm">
              <div className="flex items-center justify-between gap-3">
                <div className="font-medium text-gray-700">Current static bundle</div>
                <button
                  type="button"
                  onClick={onDeleteStatic}
                  disabled={!info?.staticUploaded || deleteStatic.isPending}
                  className="inline-flex items-center gap-1 rounded-md border border-red-200 px-2 py-1 text-xs font-medium text-red-700 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {deleteStatic.isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Trash2 className="h-3.5 w-3.5" />}
                  Delete static assets
                </button>
              </div>
              {info?.staticUploaded || info?.staticOnStackVolume ? (
                <div className="mt-2 space-y-1 text-gray-600">
                  <p>
                    {info.staticFileCount.toLocaleString()} files · {formatBytes(info.staticSize)}
                  </p>
                  {info.staticOnStackVolume && (
                    <p className="text-xs text-green-700">
                      Stored on this stack&rsquo;s <span className="font-mono">armory-static</span> Docker volume.
                    </p>
                  )}
                </div>
              ) : (
                <p className="mt-2 text-gray-400">No static bundle uploaded (the image&rsquo;s built-in assets are used).</p>
              )}
            </div>

            <DropZone
              label="Drag & drop armory.static.zip"
              hook={uploadStatic}
              onError={setPageError}
              onDone={() => flash('Static assets uploaded. Rebuild the armory image to apply them.')}
            />
          </div>
        </div>

        <div className="mt-6">
          <ArmoryDataBrowser stackId={stackId} />
        </div>
      </section>

      {/* Server DBCs */}
      <section className="rounded-lg border bg-white p-6 shadow-sm">
        <div className="mb-1 flex items-center gap-2">
          <Database className="h-5 w-5 text-blue-600" />
          <h2 className="text-lg font-semibold text-gray-900">Server DBCs</h2>
        </div>
        <p className="mb-4 text-sm text-gray-500">
          The armory reads DBC data (<span className="font-mono">data/dbc</span>) for richer item, spell
          and talent tooltips. Sync extracts DBCs from this stack&rsquo;s client-data volume, converts them
          to CSVs on the stack engine, writes them into the armory-assets volume, then rebuilds the armory
          image. The stack must have started at least once so its client data is populated.
        </p>

        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="text-sm space-y-1">
            {serverDbcsReady ? (
              <span className="inline-flex items-center gap-1.5 text-green-700">
                <CheckCircle2 className="h-4 w-4" />
                {info?.serverDbcFileCount.toLocaleString()} server DBC file(s) on stack client-data volume
              </span>
            ) : (
              <span className="inline-flex items-center gap-1.5 text-gray-500">
                <XCircle className="h-4 w-4 text-gray-400" /> No server DBCs yet — start the stack and wait for client-data-init
              </span>
            )}
            {dbcCsvPresent ? (
              <span className="block text-green-700">
                <CheckCircle2 className="mr-1 inline h-4 w-4" />
                Armory DBC CSVs present on the stack <span className="font-mono">armory-assets</span> volume
              </span>
            ) : serverDbcsReady ? (
              <span className="block text-gray-500">
                DBC CSVs not synced yet — use Sync below to convert server DBCs for the armory
              </span>
            ) : null}
          </div>
          <button
            onClick={onSyncDbcs}
            disabled={dbcSyncing || !serverDbcsReady}
            title={!serverDbcsReady ? 'Start the stack and wait for client-data-init first' : undefined}
            className="inline-flex items-center gap-1.5 whitespace-nowrap rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {dbcSyncing ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
            {dbcSyncing
              ? job?.message || 'Syncing DBCs…'
              : dbcCsvPresent
                ? 'Update DBC files & CSVs'
                : 'Sync DBCs from server'}
          </button>
        </div>

        {/* Live progress log for the DBC sync background job. Persisted in the job status, so it
            reattaches (and keeps streaming) after a page refresh. */}
        {job?.action === 'SyncDbc' && (job.isRunning || (job.recentLogs?.length ?? 0) > 0) && (
          <div
            className={`mt-4 rounded-md border px-4 py-3 text-sm ${
              job.isRunning
                ? 'border-blue-200 bg-blue-50 text-blue-800'
                : job.success
                  ? 'border-green-200 bg-green-50 text-green-800'
                  : 'border-red-200 bg-red-50 text-red-800'
            }`}
          >
            <div className="mb-1 flex items-center gap-2 font-medium">
              {job.isRunning && <Loader2 className="h-4 w-4 animate-spin" />}
              {job.isRunning
                ? job.message || 'Syncing DBCs…'
                : job.success
                  ? 'Server DBCs synced to the armory and reloaded.'
                  : 'DBC sync failed.'}
            </div>
            {job.isRunning && (
              <p className="mb-1 text-xs opacity-80">
                Running in the background — this cannot be cancelled. You can safely leave or refresh
                this page; the job continues on the server.
              </p>
            )}
            {(job.recentLogs?.length ?? 0) > 0 && (
              <pre
                ref={dbcLogRef}
                className="mt-1 max-h-56 overflow-auto whitespace-pre-wrap font-mono text-xs"
              >
                {(job.recentLogs ?? []).join('\n')}
                {job.error ? `\n${job.error}` : ''}
              </pre>
            )}
          </div>
        )}
      </section>
    </div>
  )
}

interface UploadHook {
  isPending: boolean
  mutateAsync: (vars: { file: File; onProgress?: (percent: number) => void }) => Promise<unknown>
}

function DropZone({
  label,
  hook,
  onError,
  onDone,
}: {
  label: string
  hook: UploadHook
  onError: (msg: string | null) => void
  onDone: () => void
}) {
  const [uploadPercent, setUploadPercent] = useState<number | null>(null)
  const [dragActive, setDragActive] = useState(false)
  const fileRef = useRef<HTMLInputElement | null>(null)

  const uploading = hook.isPending || uploadPercent !== null

  const onFile = async (file?: File | null) => {
    if (!file) return
    onError(null)
    if (!hasAcceptedExtension(file.name)) {
      onError(`Unsupported file type. Accepted archives: ${ACCEPTED_EXTENSIONS.join(', ')}.`)
      return
    }
    setUploadPercent(0)
    try {
      await hook.mutateAsync({ file, onProgress: setUploadPercent })
      onDone()
    } catch (err) {
      onError(errorMessage(err))
    } finally {
      setUploadPercent(null)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  return (
    <div className="flex flex-col justify-center">
      <label
        onDragOver={(e) => {
          e.preventDefault()
          if (!uploading) setDragActive(true)
        }}
        onDragLeave={(e) => {
          e.preventDefault()
          setDragActive(false)
        }}
        onDrop={(e) => {
          e.preventDefault()
          setDragActive(false)
          if (!uploading) void onFile(e.dataTransfer.files?.[0])
        }}
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
              : 'Extracting…'
            : dragActive
              ? 'Drop the archive to upload'
              : label}
        </span>
        {!uploading && <span className="text-xs text-gray-400">zip · rar · 7z · tar / tar.gz</span>}
        <input
          ref={fileRef}
          type="file"
          accept={ACCEPT_ATTR}
          className="hidden"
          disabled={uploading}
          onChange={(e) => onFile(e.target.files?.[0])}
        />
      </label>

      {uploadPercent !== null && uploadPercent < 100 && (
        <div className="mt-2 h-2 w-full overflow-hidden rounded bg-gray-200">
          <div className="h-full bg-blue-600 transition-all" style={{ width: `${uploadPercent}%` }} />
        </div>
      )}
    </div>
  )
}
