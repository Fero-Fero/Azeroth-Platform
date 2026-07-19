import { Globe, Package } from 'lucide-react'
import { cn } from '@/lib/utils'

export type ModuleBrowseTab = 'curated' | 'community'

interface ModuleBrowseTabsProps {
  active: ModuleBrowseTab
  onChange: (tab: ModuleBrowseTab) => void
  curatedCount?: number
  communityCount?: number
  className?: string
}

export default function ModuleBrowseTabs({
  active,
  onChange,
  curatedCount,
  communityCount,
  className,
}: ModuleBrowseTabsProps) {
  return (
    <div
      className={cn(
        'inline-flex rounded-lg border border-slate-200 bg-slate-100 p-1 shadow-sm',
        className,
      )}
      role="tablist"
      aria-label="Module source"
    >
      <TabButton
        active={active === 'curated'}
        onClick={() => onChange('curated')}
        icon={<Package className="h-4 w-4" aria-hidden="true" />}
        label="Curated modules"
        hint={curatedCount !== undefined ? `${curatedCount} available` : 'Platform tested'}
        activeClassName="bg-white text-blue-900 shadow-sm ring-1 ring-blue-100"
      />
      <TabButton
        active={active === 'community'}
        onClick={() => onChange('community')}
        icon={<Globe className="h-4 w-4" aria-hidden="true" />}
        label="Community catalogue"
        hint={communityCount !== undefined ? `${communityCount}+ on GitHub` : 'AzerothCore.org'}
        activeClassName="bg-slate-900 text-amber-100 shadow-sm ring-1 ring-amber-500/30"
      />
    </div>
  )
}

function TabButton({
  active,
  onClick,
  icon,
  label,
  hint,
  activeClassName,
}: {
  active: boolean
  onClick: () => void
  icon: React.ReactNode
  label: string
  hint: string
  activeClassName: string
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={cn(
        'flex min-w-[9.5rem] flex-col items-start rounded-md px-3 py-2 text-left transition-colors sm:min-w-[11rem]',
        active ? activeClassName : 'text-slate-600 hover:bg-white/60 hover:text-slate-800',
      )}
    >
      <span className="inline-flex items-center gap-1.5 text-sm font-semibold">
        {icon}
        {label}
      </span>
      <span className={cn('mt-0.5 text-[11px] font-normal', active ? 'opacity-80' : 'text-slate-500')}>
        {hint}
      </span>
    </button>
  )
}
