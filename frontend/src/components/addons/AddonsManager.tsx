import { useMemo, useRef, useState } from 'react'
import {
  Upload,
  Trash2,
  Loader2,
  Package,
  Download,
  Check,
  ExternalLink,
  ChevronDown,
  Sparkles,
  Library,
} from 'lucide-react'
import AddonSectionTabs, { type AddonSection } from '@/components/addons/AddonSectionTabs'
import {
  useAddons,
  useUploadAddon,
  useDeleteAddon,
  useAddonCatalog,
  useInstallCatalogAddon,
} from '@/hooks/useAddons'
import {
  catalogEntrySort,
  orderCatalogForDisplay,
  sortCatalogIdsForInstall,
  toggleCatalogSelection,
} from '@/lib/addon-catalog'
import { cn } from '@/lib/utils'
import type { AddonCatalogEntryDto } from '@/types/addon.types'

interface AddonsManagerProps {
  stackId: string
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
  const [section, setSection] = useState<AddonSection>('installed')
  const [dragOver, setDragOver] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const [pendingDelete, setPendingDelete] = useState<string | null>(null)
  const [pendingInstall, setPendingInstall] = useState<string | null>(null)
  const [selectedCatalogIds, setSelectedCatalogIds] = useState<Set<string>>(() => new Set())
  const [batchInstallProgress, setBatchInstallProgress] = useState<string | null>(null)

  const busy = upload.isPending || remove.isPending || install.isPending || batchInstallProgress !== null

  const handleInstall = async (addonId: string) => {
    setMessage(null)
    setPendingInstall(addonId)
    try {
      await install.mutateAsync(addonId)
      setSelectedCatalogIds((current) => {
        const next = new Set(current)
        next.delete(addonId)
        return next
      })
    } catch (e) {
      setMessage(extractError(e, `Failed to install addon.`))
    } finally {
      setPendingInstall(null)
    }
  }

  const handleToggleCatalogSelection = (addonId: string) => {
    const catalogEntries = catalog.data ?? []
    setSelectedCatalogIds((current) => toggleCatalogSelection(addonId, catalogEntries, current))
  }

  const handleInstallSelected = async () => {
    const catalogEntries = catalog.data ?? []
    const toInstall = sortCatalogIdsForInstall(
      [...selectedCatalogIds].filter((id) => {
        const entry = catalogEntries.find((item) => item.id === id)
        return entry && !entry.installed
      }),
      catalogEntries,
    )

    if (toInstall.length === 0) {
      return
    }

    setMessage(null)
    for (let index = 0; index < toInstall.length; index++) {
      const addonId = toInstall[index]!
      const entry = catalogEntries.find((item) => item.id === addonId)
      setBatchInstallProgress(`Installing ${entry?.name ?? addonId} (${index + 1}/${toInstall.length})…`)
      setPendingInstall(addonId)
      try {
        await install.mutateAsync(addonId)
        setSelectedCatalogIds((current) => {
          const next = new Set(current)
          next.delete(addonId)
          return next
        })
      } catch (e) {
        setMessage(extractError(e, `Failed to install ${entry?.name ?? addonId}.`))
        break
      } finally {
        setPendingInstall(null)
      }
    }
    setBatchInstallProgress(null)
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
  const catalogEntries = useMemo(
    () => [...(catalog.data ?? [])].sort(catalogEntrySort),
    [catalog.data],
  )
  const visibleCatalogEntries = useMemo(
    () => orderCatalogForDisplay(catalogEntries, selectedCatalogIds),
    [catalogEntries, selectedCatalogIds],
  )
  const selectedPendingCount = useMemo(
    () =>
      [...selectedCatalogIds].filter((id) => {
        const entry = catalogEntries.find((item) => item.id === id)
        return entry && !entry.installed
      }).length,
    [catalogEntries, selectedCatalogIds],
  )
  const suggestedCallout = catalogEntries.find((entry) => entry.suggested && !entry.installed)
  const uploadHandlers = {
    dragOver,
    setDragOver,
    busy,
    inputRef,
    uploadPending: upload.isPending,
    onDrop: handleDrop,
    onFiles: handleFiles,
  }

  return (
    <div className="space-y-5">
      <div>
        <h2 className="text-xl font-semibold text-slate-900">Addons</h2>
        <p className="mt-1 text-sm text-slate-500">
          Upload custom packs or install from the catalog - synced to players via the launcher.
        </p>
      </div>

      <div className="grid gap-4 lg:grid-cols-5">
        <UploadHeroCard {...uploadHandlers} className="lg:col-span-3" />
        <AddonCollectionsCard className="lg:col-span-2" />
      </div>

      <details className="rounded-lg border border-blue-100 bg-blue-50/60 text-sm text-blue-900">
        <summary className="cursor-pointer list-none px-4 py-3 font-medium [&::-webkit-details-marker]:hidden">
          <span className="inline-flex items-center gap-2">
            <Sparkles className="h-4 w-4 text-blue-600" />
            How launcher addons work
            <ChevronDown className="h-4 w-4 text-blue-500" />
          </span>
        </summary>
        <p className="border-t border-blue-100 px-4 py-3 text-blue-800/90">
          Addons uploaded here are served through the launcher and installed into every player&apos;s{' '}
          <code className="rounded bg-blue-100/80 px-1 font-mono text-xs">Interface/AddOns</code> folder.
          Updates and deletes sync on the next launch. Player-installed addons are never touched.
        </p>
      </details>

      {message && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{message}</div>
      )}

      <AddonSectionTabs
        active={section}
        onChange={setSection}
        installedCount={addons.length}
        catalogCount={catalogEntries.length}
      />

      <div role="tabpanel">
        {section === 'installed' ? (
          <InstalledAddonsPanel
            addons={addons}
            isLoading={isLoading}
            error={error}
            totalSize={data?.totalSize ?? 0}
            busy={busy}
            pendingDelete={pendingDelete}
            onDelete={handleDelete}
          />
        ) : (
          <CatalogPanel
            catalog={catalog}
            visibleCatalogEntries={visibleCatalogEntries}
            suggestedCallout={suggestedCallout}
            selectedPendingCount={selectedPendingCount}
            selectedCatalogIds={selectedCatalogIds}
            busy={busy}
            pendingInstall={pendingInstall}
            batchInstallProgress={batchInstallProgress}
            onInstallSelected={() => void handleInstallSelected()}
            onToggleSelect={handleToggleCatalogSelection}
            onInstall={(id) => void handleInstall(id)}
          />
        )}
      </div>
    </div>
  )
}

const ADDON_COLLECTIONS = [
  {
    name: 'TrinityCore collection',
    description: 'Official TrinityCore 3.3.5a addon pack',
    href: 'https://github.com/TrinityCore/wow_335a_addons',
  },
  {
    name: 'NoM0Re collection',
    description: 'Curated community 3.3.5a addons',
    href: 'https://github.com/NoM0Re/WoW-3.3.5a-Addons',
  },
] as const

function UploadHeroCard({
  dragOver,
  setDragOver,
  busy,
  inputRef,
  uploadPending,
  onDrop,
  onFiles,
  className,
}: {
  dragOver: boolean
  setDragOver: (value: boolean) => void
  busy: boolean
  inputRef: React.RefObject<HTMLInputElement | null>
  uploadPending: boolean
  onDrop: (e: React.DragEvent) => void
  onFiles: (files: FileList | File[]) => Promise<void>
  className?: string
}) {
  return (
    <section
      className={cn(
        'overflow-hidden rounded-xl border-2 border-dashed shadow-sm transition-colors',
        busy
          ? 'cursor-not-allowed border-slate-200 bg-slate-50'
          : dragOver
            ? 'cursor-pointer border-blue-500 bg-blue-50 ring-2 ring-blue-200'
            : 'cursor-pointer border-blue-300 bg-linear-to-br from-blue-50 via-indigo-50/80 to-white hover:border-blue-400 hover:shadow-md',
        className,
      )}
      onDragOver={(e) => {
        e.preventDefault()
        if (!busy) setDragOver(true)
      }}
      onDragLeave={() => setDragOver(false)}
      onDrop={onDrop}
      onClick={() => !busy && inputRef.current?.click()}
      onKeyDown={(e) => {
        if ((e.key === 'Enter' || e.key === ' ') && !busy) {
          e.preventDefault()
          inputRef.current?.click()
        }
      }}
      role="button"
      tabIndex={busy ? -1 : 0}
      aria-disabled={busy}
    >
      <div className="flex h-full flex-col items-center justify-center px-6 py-8 text-center sm:py-10">
        <div
          className={cn(
            'mb-4 flex h-14 w-14 items-center justify-center rounded-full',
            busy ? 'bg-slate-100 text-slate-400' : 'bg-blue-600 text-white shadow-md shadow-blue-600/25',
          )}
        >
          {uploadPending ? <Loader2 className="h-7 w-7 animate-spin" /> : <Upload className="h-7 w-7" />}
        </div>
        <h3 className="text-lg font-semibold text-slate-900">
          {uploadPending ? 'Uploading addon…' : 'Upload custom addon'}
        </h3>
        <p className="mt-1 max-w-md text-sm text-slate-600">
          Drop one or more <strong className="font-semibold text-slate-800">.zip</strong> archives here, or click to
          browse your files.
        </p>
        {!uploadPending && !busy && (
          <span className="mt-4 inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow-sm">
            <Upload className="h-4 w-4" />
            Choose .zip files
          </span>
        )}
        <p className="mt-4 text-xs text-slate-500">
          Large packs (storyline / voice-over) can be several GB - uploads stream to disk and may take a while.
        </p>
      </div>
      <input
        ref={inputRef}
        type="file"
        multiple
        accept=".zip"
        className="hidden"
        onChange={(e) => {
          if (e.target.files?.length) void onFiles(e.target.files)
          e.target.value = ''
        }}
      />
    </section>
  )
}

function AddonCollectionsCard({ className }: { className?: string }) {
  return (
    <section
      className={cn(
        'flex h-full flex-col rounded-xl border border-slate-800 bg-linear-to-br from-slate-900 via-slate-900 to-slate-800 p-5 text-white shadow-md',
        className,
      )}
    >
      <div className="flex items-start gap-3">
        <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-white/10 ring-1 ring-white/15">
          <Library className="h-5 w-5" />
        </div>
        <div>
          <h3 className="font-semibold text-white">Find addons online</h3>
          <p className="mt-1 text-sm text-slate-300">
            Browse community collections on GitHub, download a <strong className="text-white">.zip</strong>, then upload
            it with the card on the left.
          </p>
        </div>
      </div>
      <div className="mt-5 flex flex-1 flex-col gap-2">
        {ADDON_COLLECTIONS.map((collection) => (
          <a
            key={collection.href}
            href={collection.href}
            target="_blank"
            rel="noopener noreferrer"
            className="group flex items-center justify-between gap-3 rounded-lg border border-white/15 bg-white/5 px-4 py-3 transition-colors hover:border-amber-400/40 hover:bg-white/10"
          >
            <span className="min-w-0 text-left">
              <span className="block text-sm font-semibold text-white group-hover:text-amber-100">
                {collection.name}
              </span>
              <span className="mt-0.5 block text-xs text-slate-400 group-hover:text-slate-300">
                {collection.description}
              </span>
            </span>
            <ExternalLink className="h-4 w-4 shrink-0 text-slate-400 group-hover:text-amber-300" aria-hidden="true" />
          </a>
        ))}
      </div>
    </section>
  )
}

function InstalledAddonsPanel({
  addons,
  isLoading,
  error,
  totalSize,
  busy,
  pendingDelete,
  onDelete,
}: {
  addons: Array<{ name: string; recommended?: boolean; fileCount: number; totalSize: number }>
  isLoading: boolean
  error: unknown
  totalSize: number
  busy: boolean
  pendingDelete: string | null
  onDelete: (name: string) => void
}) {
  return (
    <section className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm">
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-100 px-4 py-3 sm:px-5">
        <h4 className="font-medium text-slate-900">Served to players</h4>
        {totalSize > 0 && (
          <span className="text-xs text-slate-500">{formatBytes(totalSize)} total</span>
        )}
      </div>

      {isLoading ? (
        <div className="flex items-center gap-2 px-4 py-8 text-sm text-slate-500 sm:px-5">
          <Loader2 className="h-4 w-4 animate-spin" />
          Loading addons…
        </div>
      ) : error ? (
        <div className="px-4 py-8 text-sm text-red-600 sm:px-5">Failed to load addons.</div>
      ) : addons.length === 0 ? (
        <div className="px-4 py-10 text-center sm:px-5">
          <Package className="mx-auto h-8 w-8 text-slate-300" />
          <p className="mt-3 text-sm font-medium text-slate-700">No addons served yet</p>
          <p className="mt-1 text-sm text-slate-500">
            Upload a .zip above, or switch to <strong>Browse catalog</strong> for one-click installs.
          </p>
        </div>
      ) : (
        <ul className="divide-y divide-slate-100">
          {addons.map((addon) => (
            <li key={addon.name} className="flex items-center justify-between gap-3 px-4 py-3 sm:px-5">
              <span className="flex min-w-0 items-center gap-2">
                <Package className="h-4 w-4 shrink-0 text-slate-400" />
                <span className="truncate font-medium text-slate-900">{addon.name}</span>
                {addon.recommended && <RecommendedBadge />}
              </span>
              <div className="flex shrink-0 items-center gap-3">
                <span className="hidden text-xs text-slate-500 sm:inline">
                  {addon.fileCount} file{addon.fileCount === 1 ? '' : 's'} · {formatBytes(addon.totalSize)}
                </span>
                <button
                  type="button"
                  onClick={() => onDelete(addon.name)}
                  disabled={busy}
                  className="rounded-md p-1.5 text-slate-400 hover:bg-red-50 hover:text-red-600 disabled:opacity-30"
                  title={`Delete ${addon.name}`}
                >
                  {pendingDelete === addon.name ? (
                    <Loader2 className="h-4 w-4 animate-spin" />
                  ) : (
                    <Trash2 className="h-4 w-4" />
                  )}
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function CatalogPanel({
  catalog,
  visibleCatalogEntries,
  suggestedCallout,
  selectedPendingCount,
  selectedCatalogIds,
  busy,
  pendingInstall,
  batchInstallProgress,
  onInstallSelected,
  onToggleSelect,
  onInstall,
}: {
  catalog: ReturnType<typeof useAddonCatalog>
  visibleCatalogEntries: AddonCatalogEntryDto[]
  suggestedCallout?: AddonCatalogEntryDto
  selectedPendingCount: number
  selectedCatalogIds: Set<string>
  busy: boolean
  pendingInstall: string | null
  batchInstallProgress: string | null
  onInstallSelected: () => void
  onToggleSelect: (id: string) => void
  onInstall: (id: string) => void
}) {
  return (
    <section className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm">
      {suggestedCallout && (
        <div className="border-b border-violet-200 bg-linear-to-r from-violet-50 to-fuchsia-50 px-4 py-3 sm:px-5">
          <p className="text-sm text-violet-950">
            <strong>{suggestedCallout.name}</strong> is recommended for this stack - select it below and install.
          </p>
        </div>
      )}

      {selectedPendingCount > 0 && (
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-100 bg-slate-50 px-4 py-3 sm:px-5">
          <span className="text-sm font-medium text-slate-700">
            {selectedPendingCount} addon{selectedPendingCount === 1 ? '' : 's'} selected
          </span>
          <button
            type="button"
            onClick={onInstallSelected}
            disabled={busy}
            className="inline-flex items-center gap-1.5 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-40"
          >
            {batchInstallProgress ? (
              <>
                <Loader2 className="h-4 w-4 animate-spin" />
                {batchInstallProgress}
              </>
            ) : (
              <>
                <Download className="h-4 w-4" />
                Install selected
              </>
            )}
          </button>
        </div>
      )}

      {catalog.isLoading ? (
        <div className="flex items-center gap-2 px-4 py-8 text-sm text-slate-500 sm:px-5">
          <Loader2 className="h-4 w-4 animate-spin" />
          Loading catalog…
        </div>
      ) : catalog.error ? (
        <div className="px-4 py-8 text-sm text-red-600 sm:px-5">Failed to load the addon catalog.</div>
      ) : (
        <ul className="divide-y divide-slate-100">
          {visibleCatalogEntries.map((entry) => (
            <CatalogEntryRow
              key={entry.id}
              entry={entry}
              busy={busy}
              pendingInstall={pendingInstall}
              selected={selectedCatalogIds.has(entry.id)}
              onToggleSelect={() => onToggleSelect(entry.id)}
              onInstall={() => onInstall(entry.id)}
            />
          ))}
        </ul>
      )}
    </section>
  )
}

interface CatalogEntryRowProps {
  entry: AddonCatalogEntryDto
  busy: boolean
  pendingInstall: string | null
  selected: boolean
  onToggleSelect: () => void
  onInstall: () => void
}

function CatalogEntryRow({
  entry,
  busy,
  pendingInstall,
  selected,
  onToggleSelect,
  onInstall,
}: CatalogEntryRowProps) {
  const isChild = !!entry.parentAddonId

  return (
    <li
      className={cn(
        'flex items-start justify-between gap-4 px-4 py-3 text-sm sm:px-5',
        isChild && 'bg-slate-50/80 pl-10 sm:pl-12',
      )}
    >
      <div className="flex min-w-0 items-start gap-3">
        <input
          type="checkbox"
          checked={entry.installed || selected}
          disabled={busy || entry.installed}
          onChange={onToggleSelect}
          className="mt-1 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500 disabled:opacity-50"
          aria-label={`Select ${entry.name}`}
        />
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-medium text-gray-800">{entry.name}</span>
            {entry.recommended && <RecommendedBadge />}
            {entry.suggested && <SuggestedBadge />}
            <span className="rounded bg-gray-100 px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-gray-500">
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
                <ExternalLink className="h-3.5 w-3.5" />
              </a>
            )}
          </div>
          <p className="mt-0.5 line-clamp-2 text-slate-600">{entry.description}</p>
        </div>
      </div>
      <button
        type="button"
        onClick={onInstall}
        disabled={busy || (!entry.installed && !!entry.parentAddonId && !selected)}
        className={`inline-flex shrink-0 items-center gap-1.5 rounded-md px-3 py-1.5 text-xs font-medium transition-colors ${
          entry.installed
            ? 'bg-green-50 text-green-700 hover:bg-green-100'
            : 'bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-40'
        }`}
        title={
          entry.installed
            ? 'Reinstall / update'
            : selected
              ? 'Install this addon'
              : 'Select the checkbox first'
        }
      >
        {pendingInstall === entry.id ? (
          <Loader2 className="h-3.5 w-3.5 animate-spin" />
        ) : entry.installed ? (
          <Check className="h-3.5 w-3.5" />
        ) : (
          <Download className="h-3.5 w-3.5" />
        )}
        {pendingInstall === entry.id ? 'Installing…' : entry.installed ? 'Installed' : 'Install'}
      </button>
    </li>
  )
}

function recommendedFirst<T extends { recommended?: boolean; name: string }>(a: T, b: T) {
  if (!!a.recommended !== !!b.recommended) return a.recommended ? -1 : 1
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
