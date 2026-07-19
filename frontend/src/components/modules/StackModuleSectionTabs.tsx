import { CheckCircle2, PlusCircle } from 'lucide-react'
import { cn } from '@/lib/utils'

export type StackModulesSectionTab = 'installed' | 'add'

interface StackModuleSectionTabsProps {
  active: StackModulesSectionTab
  onChange: (tab: StackModulesSectionTab) => void
  installedCount: number
  availableCount: number
  className?: string
}

export default function StackModuleSectionTabs({
  active,
  onChange,
  installedCount,
  availableCount,
  className,
}: StackModuleSectionTabsProps) {
  return (
    <div
      className={cn(
        'flex rounded-lg border border-slate-200 bg-slate-100 p-1 shadow-sm',
        className,
      )}
      role="tablist"
      aria-label="Modules section"
    >
      <SectionTabButton
        active={active === 'installed'}
        onClick={() => onChange('installed')}
        icon={<CheckCircle2 className="h-4 w-4" aria-hidden="true" />}
        label="Installed modules"
        hint={`${installedCount} on this stack`}
        activeClassName="bg-white text-slate-900 shadow-sm ring-1 ring-slate-200"
      />
      <SectionTabButton
        active={active === 'add'}
        onClick={() => onChange('add')}
        icon={<PlusCircle className="h-4 w-4" aria-hidden="true" />}
        label="Add modules"
        hint={`${availableCount} available`}
        activeClassName="bg-white text-blue-900 shadow-sm ring-1 ring-blue-100"
      />
    </div>
  )
}

function SectionTabButton({
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
        'flex flex-1 flex-col items-start rounded-md px-3 py-2 text-left transition-colors sm:px-4',
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
