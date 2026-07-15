import { useState } from 'react'
import { Hammer, Loader2 } from 'lucide-react'
import ConfigMigrationModeChoice, { type ConfigMigrationMode } from './config/ConfigMigrationModeChoice'

interface RebuildStackDialogProps {
  stackName: string
  onConfirm: (configMigrationMode: ConfigMigrationMode) => void
  onCancel: () => void
  isRebuilding: boolean
}

/**
 * Confirms a manual rebuild and lets the operator decide how the existing server .conf files are
 * reconciled with the freshly built version (merge/preserve or reset to new defaults).
 */
export default function RebuildStackDialog({
  stackName,
  onConfirm,
  onCancel,
  isRebuilding,
}: RebuildStackDialogProps) {
  // Rebuilds default to preserving the operator's existing configuration verbatim.
  const [configMigrationMode, setConfigMigrationMode] = useState<ConfigMigrationMode>('Merge')

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-md rounded-lg bg-white shadow-xl">
        <div className="border-b border-gray-200 px-6 py-4">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-full bg-amber-100">
              <Hammer className="h-5 w-5 text-amber-600" />
            </div>
            <h2 className="text-xl font-semibold text-gray-900">Rebuild Stack</h2>
          </div>
        </div>

        <div className="space-y-4 px-6 py-4">
          <p className="text-gray-700">
            You are about to rebuild <strong>{stackName}</strong> from its current configuration.
          </p>

          <ConfigMigrationModeChoice
            value={configMigrationMode}
            onChange={setConfigMigrationMode}
            disabled={isRebuilding}
          />
        </div>

        <div className="flex justify-end gap-3 border-t border-gray-200 px-6 py-4">
          <button
            onClick={onCancel}
            disabled={isRebuilding}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={() => onConfirm(configMigrationMode)}
            disabled={isRebuilding}
            className="flex items-center gap-2 rounded-md bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-50"
          >
            {isRebuilding ? (
              <>
                <Loader2 className="h-4 w-4 animate-spin" />
                Starting Rebuild...
              </>
            ) : (
              'Rebuild Stack'
            )}
          </button>
        </div>
      </div>
    </div>
  )
}
