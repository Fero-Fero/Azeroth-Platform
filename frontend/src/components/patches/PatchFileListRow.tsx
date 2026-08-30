import { Binary, FileJson, FileSpreadsheet, FileText, Pencil, Trash2 } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'

export function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value < 10 && unit > 0 ? 1 : 0)} ${units[unit]}`
}

function fileExtension(name: string): string {
  const base = name.split('/').pop() ?? name
  const dot = base.lastIndexOf('.')
  return dot >= 0 ? base.slice(dot + 1).toLowerCase() : ''
}

type ExtStyle = { Icon: LucideIcon; icon: string; tag: string }

function styleForExtension(ext: string): ExtStyle {
  if (ext === 'json') {
    return { Icon: FileJson, icon: 'text-amber-600 bg-amber-50 border-amber-100', tag: 'border-amber-200 bg-amber-50 text-amber-800' }
  }
  if (ext === 'lua' || ext === 'ext') {
    return { Icon: FileText, icon: 'text-sky-600 bg-sky-50 border-sky-100', tag: 'border-sky-200 bg-sky-50 text-sky-800' }
  }
  if (ext === 'sql') {
    return { Icon: FileText, icon: 'text-violet-600 bg-violet-50 border-violet-100', tag: 'border-violet-200 bg-violet-50 text-violet-800' }
  }
  if (ext === 'mpq') {
    return { Icon: FileText, icon: 'text-indigo-600 bg-indigo-50 border-indigo-100', tag: 'border-indigo-200 bg-indigo-50 text-indigo-800' }
  }
  if (ext === 'csv') {
    return { Icon: FileSpreadsheet, icon: 'text-emerald-600 bg-emerald-50 border-emerald-100', tag: 'border-emerald-200 bg-emerald-50 text-emerald-800' }
  }
  if (ext === 'txt') {
    return { Icon: FileText, icon: 'text-teal-600 bg-teal-50 border-teal-100', tag: 'border-teal-200 bg-teal-50 text-teal-800' }
  }
  if (ext === 'dbc') {
    return { Icon: Binary, icon: 'text-orange-600 bg-orange-50 border-orange-100', tag: 'border-orange-200 bg-orange-50 text-orange-800' }
  }
  return { Icon: FileText, icon: 'text-slate-600 bg-slate-100 border-slate-200', tag: 'border-gray-200 bg-white text-gray-500' }
}

interface PatchFileListRowProps {
  label: string
  size: number
  description?: string | null
  disabled?: boolean
  onDelete: () => void
  onEdit?: () => void
  showEdit?: boolean
}

export default function PatchFileListRow({
  label,
  size,
  description,
  disabled,
  onDelete,
  onEdit,
  showEdit,
}: PatchFileListRowProps) {
  const ext = fileExtension(label)
  const { Icon, icon: iconClassName, tag: tagClassName } = styleForExtension(ext)
  const rowAlign = description ? 'items-start' : 'items-center'

  return (
    <li className="group rounded-md border border-gray-200 bg-slate-50/90 px-3 py-2 shadow-sm transition-colors hover:border-gray-300 hover:bg-slate-100">
      <div className={`flex justify-between gap-3 ${rowAlign}`}>
        <div className={`flex min-w-0 gap-2.5 ${rowAlign}`}>
          <span
            className={`inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md border ${iconClassName}`}
          >
            <Icon className="h-4 w-4" aria-hidden />
          </span>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <span className="truncate font-mono text-sm font-medium leading-snug text-gray-900">{label}</span>
              {ext && (
                <span className={`rounded border px-1.5 py-0.5 text-[10px] font-semibold uppercase leading-tight tracking-wide ${tagClassName}`}>
                  {ext}
                </span>
              )}
            </div>
            {description && (
              <p className="mt-1 text-xs leading-relaxed text-gray-600 whitespace-pre-wrap">{description}</p>
            )}
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <span className="rounded-md bg-white px-2 py-0.5 text-xs font-medium tabular-nums text-gray-600 ring-1 ring-gray-200">
            {formatBytes(size)}
          </span>
          {showEdit && onEdit && (
            <button
              type="button"
              onClick={onEdit}
              className="rounded-md p-1 text-gray-400 transition-colors hover:bg-white hover:text-blue-600"
              title="Edit"
            >
              <Pencil className="h-4 w-4" />
            </button>
          )}
          <button
            type="button"
            onClick={onDelete}
            disabled={disabled}
            className="rounded-md p-1 text-gray-400 transition-colors hover:bg-white hover:text-red-600 disabled:opacity-30"
            title="Delete"
          >
            <Trash2 className="h-4 w-4" />
          </button>
        </div>
      </div>
    </li>
  )
}
