import { useMemo } from 'react'
import { Package, Trash2, FileText } from 'lucide-react'
import type { PatchFileDto } from '@/types/patch.types'

interface MpqManifestPanelProps {
  files: PatchFileDto[]
  mpqRemovals: string[]
}

/**
 * Displays a summary of MPQ construction rules when a patch has an mpq.json manifest.
 * Shows which MPQs will be constructed from raw content, which will be removed, and
 * which are pre-built uploads.
 */
export default function MpqManifestPanel({ files, mpqRemovals }: MpqManifestPanelProps) {
  const mpqFiles = useMemo(
    () => files.filter((f) => f.category === 'mpq'),
    [files]
  )

  const hasManifest = useMemo(
    () => files.some((f) => f.category === 'mpq' && f.name === 'mpq.json'),
    [files]
  )

  if (!hasManifest && mpqRemovals.length === 0) {
    return null
  }

  const preBuiltMpqs = mpqFiles.filter(
    (f) => f.name.toLowerCase().endsWith('.mpq') && f.name !== 'mpq.json'
  )
  const rawContentFiles = mpqFiles.filter(
    (f) => !f.name.toLowerCase().endsWith('.mpq') && f.name !== 'mpq.json' && !f.name.endsWith('.desc')
  )

  return (
    <div className="mt-3 rounded-md border border-violet-200 bg-violet-50/50 p-3 space-y-2">
      <h5 className="flex items-center gap-2 text-sm font-semibold text-violet-800">
        <Package className="h-4 w-4" />
        MPQ Construction
      </h5>

      <p className="text-xs text-violet-700">
        This patch has an <span className="font-mono">mpq.json</span> manifest (comment-only templates are
        treated as empty). Pre-built <span className="font-mono">.mpq</span> files in this folder are
        uploaded as-is and are never packed into a constructed archive — describe each one under{' '}
        <span className="font-mono">description</span> (required), not{' '}
        <span className="font-mono">add</span>. Names in <span className="font-mono">add</span> are the
        single archive built from loose files and folders; each must also have a matching{' '}
        <span className="font-mono">description</span> entry. Names in{' '}
        <span className="font-mono">remove</span> are deleted from the client overlay on apply.
      </p>

      {preBuiltMpqs.length > 0 && (
        <div className="space-y-1">
          <p className="text-xs font-medium text-violet-800 flex items-center gap-1">
            <FileText className="h-3.5 w-3.5" /> Pre-built archives
          </p>
          <ul className="space-y-0.5">
            {preBuiltMpqs.map((f) => (
              <li key={f.name} className="flex items-center gap-2 text-xs text-violet-700">
                <span className="font-mono">{f.name}</span>
                {f.description && (
                  <span className="text-violet-500">- {f.description}</span>
                )}
              </li>
            ))}
          </ul>
        </div>
      )}

      {rawContentFiles.length > 0 && (
        <div className="space-y-1">
          <p className="text-xs font-medium text-violet-800 flex items-center gap-1">
            <Package className="h-3.5 w-3.5" /> Raw content ({rawContentFiles.length} file(s) to
            be constructed into MPQ on apply)
          </p>
        </div>
      )}

      {mpqRemovals.length > 0 && (
        <div className="space-y-1">
          <p className="text-xs font-medium text-red-700 flex items-center gap-1">
            <Trash2 className="h-3.5 w-3.5" /> Removes on apply
          </p>
          <ul className="space-y-0.5">
            {mpqRemovals.map((name) => (
              <li key={name} className="flex items-center gap-2 text-xs text-red-600">
                <span className="font-mono line-through">{name}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}
