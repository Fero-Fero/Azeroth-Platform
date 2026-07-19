import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  AlertCircle,
  Check,
  ExternalLink,
  Loader2,
  Plus,
  Search,
  Star,
} from 'lucide-react'
import { useCommunityModules, useImportCommunityModule } from '@/hooks/useModules'
import type { CommunityModuleDto } from '@/types/stack.types'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

interface CommunityModulesBrowserProps {
  selectedIds: string[]
  onAdd: (moduleId: string) => void
  disabled?: boolean
}

const PAGE_SIZE = 12

function formatUpdatedAt(value?: string | null): string {
  if (!value) return 'Unknown'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return 'Unknown'
  return date.toLocaleDateString()
}

export default function CommunityModulesBrowser({
  selectedIds,
  onAdd,
  disabled = false,
}: CommunityModulesBrowserProps) {
  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState('stars')
  const [page, setPage] = useState(1)
  const [actionError, setActionError] = useState<string | null>(null)
  const [pendingId, setPendingId] = useState<string | null>(null)

  const importModule = useImportCommunityModule()
  const queryClient = useQueryClient()
  const { data, isLoading, isError, error } = useCommunityModules({
    search,
    sort,
    page,
    pageSize: PAGE_SIZE,
    enabled: true,
  })

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setSearch(searchInput.trim())
      setPage(1)
    }, 300)

    return () => window.clearTimeout(timer)
  }, [searchInput])

  const totalPages = Math.max(1, Math.ceil((data?.total ?? 0) / PAGE_SIZE))
  const busy = disabled || importModule.isPending || pendingId !== null

  const handleAdd = async (entry: CommunityModuleDto) => {
    if (selectedIds.includes(entry.id)) {
      return
    }

    setActionError(null)
    setPendingId(entry.id)
    try {
      if (!entry.inPlatformCatalog) {
        await importModule.mutateAsync(entry.repository)
        await queryClient.refetchQueries({ queryKey: ['modules'] })
      }
      onAdd(entry.id)
    } catch (err) {
      setActionError(errorMessage(err))
    } finally {
      setPendingId(null)
    }
  }

  return (
    <div className="overflow-hidden rounded-xl border border-amber-500/25 bg-linear-to-b from-slate-900 via-slate-900 to-slate-950 shadow-lg shadow-slate-900/20">
      <div className="border-b border-amber-500/20 bg-slate-900/90 px-4 py-4 sm:px-5">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <div className="flex flex-wrap items-center gap-2">
              <span className="rounded-md bg-amber-500/15 px-2 py-0.5 text-[10px] font-bold uppercase tracking-[0.18em] text-amber-300 ring-1 ring-inset ring-amber-400/30">
                AzerothCore
              </span>
              <h3 className="text-lg font-semibold text-amber-50">Community module catalogue</h3>
            </div>
            <p className="mt-1 max-w-2xl text-sm text-slate-300">
              Browse modules tagged on GitHub and listed at{' '}
              <a
                href="https://www.azerothcore.org/catalogue.html#/"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1 font-medium text-amber-300 hover:text-amber-200"
              >
                azerothcore.org/catalogue
                <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
              </a>
              . These are community-maintained — they may need extra setup or fail to compile.
            </p>
          </div>
          {data?.total !== undefined && (
            <div className="rounded-lg border border-slate-700 bg-slate-800/80 px-3 py-2 text-right">
              <p className="text-[11px] uppercase tracking-wide text-slate-400">Indexed modules</p>
              <p className="text-lg font-semibold tabular-nums text-amber-200">{data.total}</p>
            </div>
          )}
        </div>
      </div>

      <div className="space-y-4 px-4 py-4 sm:px-5">
        <div className="flex flex-col gap-3 sm:flex-row">
          <div className="relative flex-1">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-500" />
            <input
              type="search"
              value={searchInput}
              onChange={(event) => setSearchInput(event.target.value)}
              placeholder="Search community modules…"
              className="block w-full rounded-lg border border-slate-700 bg-slate-800 py-2 pl-9 pr-3 text-sm text-slate-100 placeholder:text-slate-500 focus:border-amber-500/50 focus:outline-none focus:ring-2 focus:ring-amber-500/30"
            />
          </div>
          <select
            value={sort}
            onChange={(event) => {
              setSort(event.target.value)
              setPage(1)
            }}
            className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2 text-sm text-slate-100 focus:border-amber-500/50 focus:outline-none focus:ring-2 focus:ring-amber-500/30"
          >
            <option value="stars">Sort by stars</option>
            <option value="updated">Sort by recently updated</option>
            <option value="name">Sort by name</option>
          </select>
        </div>

        {actionError && (
          <div className="flex items-center gap-2 rounded-lg border border-red-400/30 bg-red-950/40 px-3 py-2 text-sm text-red-200">
            <AlertCircle className="h-4 w-4 shrink-0" />
            {actionError}
          </div>
        )}

        {isLoading && (
          <div className="flex items-center justify-center gap-2 py-12 text-sm text-slate-400">
            <Loader2 className="h-5 w-5 animate-spin text-amber-400" />
            Loading community catalogue…
          </div>
        )}

        {isError && (
          <div className="flex items-center gap-2 rounded-lg border border-amber-500/30 bg-amber-950/30 px-4 py-3 text-sm text-amber-100">
            <AlertCircle className="h-4 w-4 shrink-0" />
            {errorMessage(error) || 'Failed to load the community module catalogue.'}
          </div>
        )}

        {!isLoading && !isError && (data?.items.length ?? 0) === 0 && (
          <p className="rounded-lg border border-dashed border-slate-700 px-4 py-10 text-center text-sm text-slate-400">
            {search ? 'No community modules match your search.' : 'No community modules found.'}
          </p>
        )}

        {!isLoading && !isError && (data?.items.length ?? 0) > 0 && (
          <>
            <p className="text-xs text-slate-400">
              Showing {(data!.page - 1) * data!.pageSize + 1}–
              {Math.min(data!.page * data!.pageSize, data!.total)} of {data!.total} modules
            </p>
            <ul className="grid gap-3 md:grid-cols-2">
              {data!.items.map((entry) => (
                <CommunityModuleCard
                  key={entry.repository}
                  entry={entry}
                  selected={selectedIds.includes(entry.id)}
                  busy={busy}
                  pending={pendingId === entry.id}
                  onAdd={() => void handleAdd(entry)}
                />
              ))}
            </ul>

            {totalPages > 1 && (
              <div className="flex items-center justify-between gap-3 border-t border-slate-800 pt-4">
                <button
                  type="button"
                  onClick={() => setPage((current) => Math.max(1, current - 1))}
                  disabled={page <= 1 || busy}
                  className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 text-sm text-slate-200 hover:bg-slate-700 disabled:opacity-40"
                >
                  Previous
                </button>
                <span className="text-sm text-slate-400">
                  Page {page} of {totalPages}
                </span>
                <button
                  type="button"
                  onClick={() => setPage((current) => Math.min(totalPages, current + 1))}
                  disabled={page >= totalPages || busy}
                  className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 text-sm text-slate-200 hover:bg-slate-700 disabled:opacity-40"
                >
                  Next
                </button>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  )
}

function CommunityModuleCard({
  entry,
  selected,
  busy,
  pending,
  onAdd,
}: {
  entry: CommunityModuleDto
  selected: boolean
  busy: boolean
  pending: boolean
  onAdd: () => void
}) {
  return (
    <li className="rounded-lg border border-slate-700/80 bg-slate-800/60 p-4 backdrop-blur-sm transition-colors hover:border-amber-500/25 hover:bg-slate-800">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <p className="font-medium text-slate-100">{entry.name}</p>
            {entry.isBuiltIn && (
              <span className="rounded-full bg-amber-500/15 px-2 py-0.5 text-[11px] font-medium text-amber-200 ring-1 ring-inset ring-amber-400/30">
                Also curated
              </span>
            )}
            {entry.inPlatformCatalog && !entry.isBuiltIn && (
              <span className="rounded-full bg-slate-700 px-2 py-0.5 text-[11px] font-medium text-slate-300">
                In your catalog
              </span>
            )}
          </div>
          <p className="mt-0.5 font-mono text-[11px] text-slate-500">{entry.id}</p>
          <p className="mt-1 line-clamp-3 text-sm text-slate-300">
            {entry.description || 'No description provided.'}
          </p>
          <div className="mt-2 flex flex-wrap items-center gap-3 text-xs text-slate-400">
            <span className="inline-flex items-center gap-1 text-amber-200/90">
              <Star className="h-3.5 w-3.5 fill-amber-400/20" aria-hidden="true" />
              {entry.stars}
            </span>
            <span>{entry.forks} forks</span>
            <span>Updated {formatUpdatedAt(entry.updatedAt)}</span>
          </div>
          <a
            href={entry.repository}
            target="_blank"
            rel="noopener noreferrer"
            className="mt-2 inline-flex items-center gap-1 text-xs text-amber-300 hover:text-amber-200"
          >
            Repository <ExternalLink className="h-3 w-3" />
          </a>
        </div>
        <button
          type="button"
          onClick={onAdd}
          disabled={busy || selected}
          className={`inline-flex shrink-0 items-center gap-1.5 rounded-lg px-3 py-1.5 text-xs font-semibold disabled:cursor-not-allowed disabled:opacity-50 ${
            selected
              ? 'bg-emerald-500/15 text-emerald-300 ring-1 ring-inset ring-emerald-400/30'
              : 'bg-amber-500 text-slate-950 hover:bg-amber-400'
          }`}
        >
          {pending ? (
            <Loader2 className="h-3.5 w-3.5 animate-spin" />
          ) : selected ? (
            <Check className="h-3.5 w-3.5" />
          ) : (
            <Plus className="h-3.5 w-3.5" />
          )}
          {pending ? 'Adding…' : selected ? 'Added' : 'Add'}
        </button>
      </div>
    </li>
  )
}
