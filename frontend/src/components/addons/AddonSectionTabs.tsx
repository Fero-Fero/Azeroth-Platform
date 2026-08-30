import { Download, Package } from 'lucide-react'
import { cn } from '@/lib/utils'

export type AddonSection = 'installed' | 'catalog'

interface AddonSectionTabsProps {
  active: AddonSection
  onChange: (section: AddonSection) => void
  installedCount: number
  catalogCount: number
  className?: string
}

export default function AddonSectionTabs({
  active,
  onChange,
  installedCount,
  catalogCount,
  className,
}: AddonSectionTabsProps) {
  return (
    <div
      className={cn(
        'flex flex-wrap gap-1 rounded-lg border border-slate-200 bg-slate-100 p-1 shadow-sm',
        className,
      )}
      role="tablist"
      aria-label="Addon sections"
    >
      <TabButton
        active={active === 'installed'}
        onClick={() => onChange('installed')}
        icon={<Package className="h-4 w-4" />}
        label="Installed"
        hint={`${installedCount} served to players`}
      />
      <TabButton
        active={active === 'catalog'}
        onClick={() => onChange('catalog')}
        icon={<Download className="h-4 w-4" />}
        label="Browse catalog"
        hint={`${catalogCount} one-click installs`}
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
}: {
  active: boolean
  onClick: () => void
  icon: React.ReactNode
  label: string
  hint: string
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={cn(
        'flex min-w-[9rem] flex-1 flex-col items-start rounded-md px-3 py-2 text-left transition-colors sm:min-w-[11rem]',
        active
          ? 'bg-white text-slate-900 shadow-sm ring-1 ring-slate-200'
          : 'text-slate-600 hover:bg-white/60 hover:text-slate-800',
      )}
    >
      <span className="inline-flex items-center gap-1.5 text-sm font-semibold">
        {icon}
        {label}
      </span>
      <span className={cn('mt-0.5 text-[11px] font-normal', active ? 'text-slate-500' : 'text-slate-400')}>
        {hint}
      </span>
    </button>
  )
}
