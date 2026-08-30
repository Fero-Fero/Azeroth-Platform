import { ClipboardCheck, HardDrive, Image as ImageIcon, LayoutDashboard } from 'lucide-react'
import { cn } from '@/lib/utils'

export type StackDockerSection = 'overview' | 'resources' | 'audit' | 'disk'

interface StackDockerSectionTabsProps {
  active: StackDockerSection
  onChange: (section: StackDockerSection) => void
  imageCount: number
  volumeCount: number
  className?: string
}

export default function StackDockerSectionTabs({
  active,
  onChange,
  imageCount,
  volumeCount,
  className,
}: StackDockerSectionTabsProps) {
  return (
    <div
      className={cn(
        'flex flex-wrap gap-1 rounded-lg border border-slate-200 bg-slate-100 p-1 shadow-sm',
        className,
      )}
      role="tablist"
      aria-label="Stack Docker sections"
    >
      <TabButton
        active={active === 'overview'}
        onClick={() => onChange('overview')}
        icon={<LayoutDashboard className="h-4 w-4" />}
        label="Overview"
        hint="Disk & cleanup"
      />
      <TabButton
        active={active === 'resources'}
        onClick={() => onChange('resources')}
        icon={<ImageIcon className="h-4 w-4" />}
        label="Resources"
        hint={`${imageCount} images · ${volumeCount} volumes`}
      />
      <TabButton
        active={active === 'audit'}
        onClick={() => onChange('audit')}
        icon={<ClipboardCheck className="h-4 w-4" />}
        label="Volume audit"
        hint="Orphans & drift"
      />
      <TabButton
        active={active === 'disk'}
        onClick={() => onChange('disk')}
        icon={<HardDrive className="h-4 w-4" />}
        label="Disk breakdown"
        hint="Engine-wide detail"
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
        'flex min-w-[7.5rem] flex-1 flex-col items-start rounded-md px-3 py-2 text-left transition-colors sm:min-w-[9rem]',
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
