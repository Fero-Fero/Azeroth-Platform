import { useRef, useState } from 'react'
import { Upload, Trash2, Loader2, Package, Download, Check, ExternalLink } from 'lucide-react'
import {
  useAddons,
  useUploadAddon,
  useDeleteAddon,
  useAddonCatalog,
  useInstallCatalogAddon,
} from '@/hooks/useAddons'
import type { AddonCatalogEntryDto } from '@/types/addon.types'

interface AddonsManagerProps {
  /** Omit for the global client; pass a stack id to manage that stack's client. */
  stackId?: string
}

function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value < 10 && unit > 0 ? 1 : 0)} ${units[unit]}`
}

function extractError(e: unknown, fallback: string): string {
  const err = e as { response?: { data?: { error?: string } } }
  return err?.response?.data?.error ?? fallback
}

export default function AddonsManager({ stackId }: AddonsManagerProps) {
  const { data, isLoading, error } = useAddons(stackId)
  const upload = useUploadAddon(stackId)
  const remove = useDeleteAddon(stackId)
  const catalog = useAddonCatalog(stackId)
  const install = useInstallCatalogAddon(stackId)

  const inputRef = useRef<HTMLInputElement>(null)
  const [dragOver, setDragOver] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const [pendingDelete, setPendingDelete] = useState<string | null>(null)
  const [pendingInstall, setPendingInstall] = useState<string | null>(null)

  const busy = upload.isPending || remove.isPending || install.isPending

  const handleInstall = async (addonId: string) => {
    setMessage(null)
    setPendingInstall(addonId)
    try {
      await install.mutateAsync(addonId)
    } catch (e) {
      setMessage(extractError(e, `Failed to install addon.`))
    } finally {
      setPendingInstall(null)
    }
  }

  const handleFiles = async (files: FileList | File[]) => {
    setMessage(null)
    const zips = Array.from(files).filter((f) => f.name.toLowerCase().endsWith('.zip'))
    if (zips.length === 0) {
      setMessage('Addons must be uploaded as .zip archives.')
      return
    }
    for (const file of zips) {
      try {
        await upload.mutateAsync(file)
      } catch (e) {
        setMessage(extractError(e, `Failed to upload ${file.name}.`))
        return
      }
    }
  }

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setDragOver(false)
    if (busy) return
    if (e.dataTransfer.files?.length) {
      void handleFiles(e.dataTransfer.files)
    }
  }

  const handleDelete = async (name: string) => {
    setMessage(null)
    setPendingDelete(name)
    try {
      await remove.mutateAsync(name)
    } catch (e) {
      setMessage(extractError(e, `Failed to delete ${name}.`))
    } finally {
      setPendingDelete(null)
    }
  }

  const addons = [...(data?.addons ?? [])].sort(recommendedFirst)
  const catalogEntries = [...(catalog.data ?? [])].sort(catalogEntrySort)
  const suggestedCallout = catalogEntries.find((entry) => entry.suggested && !entry.installed)

  return (
    <div className="space-y-4">
      <div className="bg-blue-50 border border-blue-200 rounded-lg p-4 text-sm text-blue-800">
        Addons uploaded here are served through the launcher and installed into every player's{' '}
        <code className="font-mono">Interface/AddOns</code> folder. They are kept in sync automatically —
        updating or deleting an addon here updates or removes it for players on their next launch. A
        player's own manually-installed addons are never touched.
      </div>

      {/* Upload dropzone */}
      <div
        onDragOver={(e) => {
          e.preventDefault()
          if (!busy) setDragOver(true)
        }}
        onDragLeave={() => setDragOver(false)}
        onDrop={handleDrop}
        onClick={() => !busy && inputRef.current?.click()}
        className={`flex items-center justify-center gap-2 py-6 rounded-md border-2 border-dashed cursor-pointer text-sm transition-colors ${
          busy
            ? 'border-gray-200 text-gray-300 cursor-not-allowed'
            : dragOver
            ? 'border-blue-400 bg-blue-50 text-blue-600'
            : 'border-gray-300 text-gray-500 hover:border-blue-300 hover:text-blue-500'
        }`}
      >
        {upload.isPending ? <Loader2 className="w-5 h-5 animate-spin" /> : <Upload className="w-5 h-5" />}
        <span>
          {upload.isPending ? 'Uploading…' : 'Drop addon .zip archive(s) here or click to upload'}
        </span>
        <input
          ref={inputRef}
          type="file"
          multiple
          accept=".zip"
          className="hidden"
          onChange={(e) => {
            if (e.target.files?.length) void handleFiles(e.target.files)
            e.target.value = ''
          }}
        />
      </div>

      <p className="text-xs text-gray-400">
        Large addons are supported — some (e.g. storyline or voice-over packs) can be several GB.
        Big uploads are streamed to disk and may take a while depending on your connection.
      </p>

      {message && (
        <div className="bg-red-50 border border-red-200 rounded-md px-3 py-2 text-sm text-red-700">
          {message}
        </div>
      )}

      {/* Addon list */}
      <div className="border border-gray-200 rounded-lg">
        <div className="flex items-center justify-between px-4 py-3 border-b border-gray-100">
          <h4 className="font-semibold text-gray-800">
            Served addons <span className="text-gray-400 font-normal">({addons.length})</span>
          </h4>
          {data && data.totalSize > 0 && (
            <span className="text-xs text-gray-400">{formatBytes(data.totalSize)} total</span>
          )}
        </div>

        {isLoading ? (
          <div className="px-4 py-6 text-sm text-gray-500 flex items-center gap-2">
            <Loader2 className="w-4 h-4 animate-spin" /> Loading addons…
          </div>
        ) : error ? (
          <div className="px-4 py-6 text-sm text-red-600">Failed to load addons.</div>
        ) : addons.length === 0 ? (
          <div className="px-4 py-6 text-sm text-gray-500">
            No addons are being served yet. Upload a .zip to get started.
          </div>
        ) : (
          <ul className="divide-y divide-gray-100">
            {addons.map((addon) => (
              <li key={addon.name} className="flex items-center justify-between px-4 py-2.5 text-sm">
                <span className="flex items-center gap-2 min-w-0">
                  <Package className="w-4 h-4 text-gray-400 shrink-0" />
                  <span className="font-medium text-gray-800 truncate">{addon.name}</span>
                  {addon.recommended && <RecommendedBadge />}
                </span>
                <div className="flex items-center gap-4 shrink-0">
                  <span className="text-xs text-gray-400">
                    {addon.fileCount} file{addon.fileCount === 1 ? '' : 's'} · {formatBytes(addon.totalSize)}
                  </span>
                  <button
                    onClick={() => handleDelete(addon.name)}
                    disabled={busy}
                    className="text-gray-400 hover:text-red-600 disabled:opacity-30"
                    title="Delete addon"
                  >
                    {pendingDelete === addon.name ? (
                      <Loader2 className="w-4 h-4 animate-spin" />
                    ) : (
                      <Trash2 className="w-4 h-4" />
                    )}
                  </button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>

      {/* Addon catalog */}
      <div className="border border-gray-200 rounded-lg">
        <div className="flex items-center justify-between px-4 py-3 border-b border-gray-100">
          <h4 className="font-semibold text-gray-800">
            Addon catalog{' '}
            <span className="text-gray-400 font-normal">
              ({catalog.data?.length ?? 0})
            </span>
          </h4>
          <span className="text-xs text-gray-400">One-click install of popular 3.3.5a addons</span>
        </div>

        {suggestedCallout && (
          <div className="border-b border-violet-100 bg-violet-50 px-4 py-3 text-sm text-violet-900">
            <strong>{suggestedCallout.name}</strong> pairs with a module installed on this stack — install it
            for in-game control of dungeon clears.
          </div>
        )}

        {catalog.isLoading ? (
          <div className="px-4 py-6 text-sm text-gray-500 flex items-center gap-2">
            <Loader2 className="w-4 h-4 animate-spin" /> Loading catalog…
          </div>
        ) : catalog.error ? (
          <div className="px-4 py-6 text-sm text-red-600">Failed to load the addon catalog.</div>
        ) : (
          <ul className="divide-y divide-gray-100">
            {catalogEntries.map((entry) => (
              <li key={entry.id} className="flex items-start justify-between gap-4 px-4 py-3 text-sm">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="font-medium text-gray-800">{entry.name}</span>
                    {entry.recommended && <RecommendedBadge />}
                    {entry.suggested && <SuggestedBadge />}
                    <span className="text-[10px] uppercase tracking-wide bg-gray-100 text-gray-500 rounded px-1.5 py-0.5">
                      {entry.category}
                    </span>
                    {entry.website && (
                      <a
                        href={entry.website}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="text-gray-400 hover:text-blue-600"
                        title="Open project page"
                      >
                        <ExternalLink className="w-3.5 h-3.5" />
                      </a>
                    )}
                  </div>
                  <p className="text-gray-500 mt-0.5 line-clamp-2">{entry.description}</p>
                </div>
                <button
                  onClick={() => handleInstall(entry.id)}
                  disabled={busy}
                  className={`shrink-0 inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md text-xs font-medium transition-colors ${
                    entry.installed
                      ? 'bg-green-50 text-green-700 hover:bg-green-100'
                      : 'bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-40'
                  }`}
                  title={entry.installed ? 'Reinstall / update' : 'Install addon'}
                >
                  {pendingInstall === entry.id ? (
                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  ) : entry.installed ? (
                    <Check className="w-3.5 h-3.5" />
                  ) : (
                    <Download className="w-3.5 h-3.5" />
                  )}
                  {pendingInstall === entry.id
                    ? 'Installing…'
                    : entry.installed
                    ? 'Installed'
                    : 'Install'}
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  )
}

function recommendedFirst<T extends { recommended?: boolean; name: string }>(a: T, b: T) {
  if (!!a.recommended !== !!b.recommended) return a.recommended ? -1 : 1
  return a.name.localeCompare(b.name)
}

function catalogEntrySort(a: AddonCatalogEntryDto, b: AddonCatalogEntryDto) {
  if (!!a.recommended !== !!b.recommended) return a.recommended ? -1 : 1
  if (!!a.suggested !== !!b.suggested) return a.suggested ? -1 : 1
  return a.name.localeCompare(b.name)
}

function RecommendedBadge() {
  return (
    <span className="rounded-full bg-sky-50 px-2 py-0.5 text-[11px] font-medium text-sky-700 ring-1 ring-inset ring-sky-200">
      Recommended
    </span>
  )
}

function SuggestedBadge() {
  return (
    <span className="rounded-full bg-violet-50 px-2 py-0.5 text-[11px] font-medium text-violet-700 ring-1 ring-inset ring-violet-200">
      Suggested
    </span>
  )
}
