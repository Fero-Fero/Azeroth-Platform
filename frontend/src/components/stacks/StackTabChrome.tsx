import type { ReactNode } from 'react'
import { ChevronDown, Loader2, Sparkles, Upload } from 'lucide-react'
import { cn } from '@/lib/utils'

export function StackTabHeader({ title, subtitle }: { title: string; subtitle?: string }) {
  return (
    <div>
      <h2 className="text-xl font-semibold text-slate-900">{title}</h2>
      {subtitle && <p className="mt-1 max-w-2xl text-sm text-slate-500">{subtitle}</p>}
    </div>
  )
}

export function StackTabInfoDetails({ summary, children }: { summary: string; children: ReactNode }) {
  return (
    <details className="rounded-lg border border-blue-100 bg-blue-50/60 text-sm text-blue-900">
      <summary className="cursor-pointer list-none px-4 py-3 font-medium [&::-webkit-details-marker]:hidden">
        <span className="inline-flex items-center gap-2">
          <Sparkles className="h-4 w-4 text-blue-600" />
          {summary}
          <ChevronDown className="h-4 w-4 text-blue-500" />
        </span>
      </summary>
      <div className="border-t border-blue-100 px-4 py-3 text-blue-800/90">{children}</div>
    </details>
  )
}

export function StackTabPanel({
  children,
  className,
}: {
  children: ReactNode
  className?: string
}) {
  return (
    <section className={cn('overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm', className)}>
      {children}
    </section>
  )
}

export interface StackSectionTab<T extends string> {
  id: T
  label: string
  hint: string
  icon: ReactNode
}

export function StackSectionTabs<T extends string>({
  tabs,
  active,
  onChange,
  className,
}: {
  tabs: StackSectionTab<T>[]
  active: T
  onChange: (id: T) => void
  className?: string
}) {
  return (
    <div
      className={cn(
        'flex flex-wrap gap-1 rounded-lg border border-slate-200 bg-slate-100 p-1 shadow-sm',
        className,
      )}
      role="tablist"
    >
      {tabs.map((tab) => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          aria-selected={active === tab.id}
          onClick={() => onChange(tab.id)}
          className={cn(
            'flex min-w-36 flex-1 flex-col items-start rounded-md px-3 py-2 text-left transition-colors sm:min-w-44',
            active === tab.id
              ? 'bg-white text-slate-900 shadow-sm ring-1 ring-slate-200'
              : 'text-slate-600 hover:bg-white/60 hover:text-slate-800',
          )}
        >
          <span className="inline-flex items-center gap-1.5 text-sm font-semibold">
            {tab.icon}
            {tab.label}
          </span>
          <span
            className={cn(
              'mt-0.5 text-[11px] font-normal',
              active === tab.id ? 'text-slate-500' : 'text-slate-400',
            )}
          >
            {tab.hint}
          </span>
        </button>
      ))}
    </div>
  )
}

export function StackTabUploadHero({
  title,
  description,
  actionLabel,
  footnote,
  busy,
  dragOver,
  uploading,
  uploadLabel,
  onDragOver,
  onDragLeave,
  onDrop,
  onClick,
  inputRef,
  accept,
  onFileChange,
  className,
}: {
  title: string
  description: ReactNode
  actionLabel: string
  footnote?: string
  busy?: boolean
  dragOver?: boolean
  uploading?: boolean
  uploadLabel?: string
  onDragOver: (e: React.DragEvent) => void
  onDragLeave: () => void
  onDrop: (e: React.DragEvent) => void
  onClick: () => void
  inputRef: React.RefObject<HTMLInputElement | null>
  accept?: string
  onFileChange: (files: FileList | null) => void
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
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
      onClick={onClick}
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
          {uploading ? <Loader2 className="h-7 w-7 animate-spin" /> : <Upload className="h-7 w-7" />}
        </div>
        <h3 className="text-lg font-semibold text-slate-900">{uploadLabel ?? title}</h3>
        <p className="mt-1 max-w-md text-sm text-slate-600">{description}</p>
        {!uploading && !busy && (
          <span className="mt-4 inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow-sm">
            <Upload className="h-4 w-4" />
            {actionLabel}
          </span>
        )}
        {footnote && <p className="mt-4 text-xs text-slate-500">{footnote}</p>}
      </div>
      <input
        ref={inputRef}
        type="file"
        accept={accept}
        className="hidden"
        disabled={busy}
        onChange={(e) => onFileChange(e.target.files)}
      />
    </section>
  )
}

export function StackTabSideCard({
  title,
  description,
  icon,
  children,
  className,
  variant = 'dark',
}: {
  title: string
  description?: string
  icon?: ReactNode
  children: ReactNode
  className?: string
  variant?: 'dark' | 'light'
}) {
  return (
    <section
      className={cn(
        'flex h-full flex-col rounded-xl border p-5 shadow-md',
        variant === 'dark'
          ? 'border-slate-800 bg-linear-to-br from-slate-900 via-slate-900 to-slate-800 text-white'
          : 'border-slate-200 bg-linear-to-br from-slate-50 to-white text-slate-900',
        className,
      )}
    >
      <div className="flex items-start gap-3">
        {icon && (
          <div
            className={cn(
              'flex h-10 w-10 shrink-0 items-center justify-center rounded-lg ring-1',
              variant === 'dark' ? 'bg-white/10 ring-white/15' : 'bg-blue-100 text-blue-700 ring-blue-200',
            )}
          >
            {icon}
          </div>
        )}
        <div>
          <h3 className={cn('font-semibold', variant === 'dark' ? 'text-white' : 'text-slate-900')}>{title}</h3>
          {description && (
            <p className={cn('mt-1 text-sm', variant === 'dark' ? 'text-slate-300' : 'text-slate-600')}>
              {description}
            </p>
          )}
        </div>
      </div>
      <div className="mt-4 flex flex-1 flex-col">{children}</div>
    </section>
  )
}

export function StackTabPanelHeader({
  title,
  subtitle,
  actions,
}: {
  title: string
  subtitle?: string
  actions?: ReactNode
}) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-100 px-4 py-3 sm:px-5">
      <div>
        <h4 className="font-medium text-slate-900">{title}</h4>
        {subtitle && <p className="mt-0.5 text-xs text-slate-500">{subtitle}</p>}
      </div>
      {actions}
    </div>
  )
}
