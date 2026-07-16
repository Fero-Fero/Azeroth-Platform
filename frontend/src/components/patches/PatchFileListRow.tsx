import { FileJson, FileText, Trash2, Pencil } from 'lucide-react'

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

function iconForExtension(ext: string) {
  if (ext === 'json') {
    return { Icon: FileJson, className: 'text-amber-600 bg-amber-50 border-amber-100' }
  }
  if (ext === 'sql') {
    return { Icon: FileText, className: 'text-violet-600 bg-violet-50 border-violet-100' }
  }
  if (ext === 'mpq') {
    return { Icon: FileText, className: 'text-indigo-600 bg-indigo-50 border-indigo-100' }
  }
  if (ext === 'csv' || ext === 'txt' || ext === 'dbc') {
    return { Icon: FileText, className: 'text-emerald-600 bg-emerald-50 border-emerald-100' }
  }
  return { Icon: FileText, className: 'text-slate-600 bg-slate-100 border-slate-200' }
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
  const { Icon, className: iconClassName } = iconForExtension(ext)
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
                <span className="rounded border border-gray-200 bg-white px-1.5 py-0.5 text-[10px] font-semibold uppercase leading-tight tracking-wide text-gray-500">
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
