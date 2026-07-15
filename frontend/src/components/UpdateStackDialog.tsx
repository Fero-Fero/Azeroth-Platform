import { useState } from 'react'
import { AlertTriangle, Loader2 } from 'lucide-react'
import type { StackUpdateStatusDto } from '@/types/stack.types'
import { CiBuildStatusBadge } from './CiBuildStatusBadge'
import ConfigMigrationModeChoice, { type ConfigMigrationMode } from './config/ConfigMigrationModeChoice'

interface UpdateStackDialogProps {
  stackName: string
  updateStatus: StackUpdateStatusDto
  onConfirm: (configMigrationMode: ConfigMigrationMode) => void
  onCancel: () => void
  isUpdating: boolean
}

export default function UpdateStackDialog({
  stackName,
  updateStatus,
  onConfirm,
  onCancel,
  isUpdating,
}: UpdateStackDialogProps) {
  const [configMigrationMode, setConfigMigrationMode] = useState<ConfigMigrationMode>('Merge')

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-md rounded-lg bg-white shadow-xl">
        <div className="border-b border-gray-200 px-6 py-4">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-full bg-amber-100">
              <AlertTriangle className="h-5 w-5 text-amber-600" />
            </div>
            <h2 className="text-xl font-semibold text-gray-900">Update Stack</h2>
          </div>
        </div>

        <div className="px-6 py-4 space-y-4">
          <p className="text-gray-700">
            You are about to update <strong>{stackName}</strong> to the latest version.
          </p>

          {/* CI Build Status */}
          {updateStatus.latestCoreBuildStatus && (
            <CiBuildStatusBadge 
              status={updateStatus.latestCoreBuildStatus} 
              showDetails={true}
            />
          )}

          <div className="rounded-md bg-amber-50 border border-amber-200 p-4">
            <h3 className="font-medium text-amber-900 mb-2">What will happen:</h3>
            <ul className="text-sm text-amber-800 space-y-1 list-disc list-inside">
              <li>The stack will be stopped if running</li>
              <li>A snapshot of your databases and config will be taken first</li>
              <li>If the build fails, the stack will automatically roll back to that snapshot</li>
              <li>Latest code will be pulled from GitHub</li>
              {updateStatus.isCoreOutdated && (
                <li>AzerothCore will be updated to latest commit</li>
              )}
              {updateStatus.outdatedModuleCount > 0 && (
                <li>
                  {updateStatus.outdatedModuleCount} module{updateStatus.outdatedModuleCount > 1 ? 's' : ''} will be updated
                </li>
              )}
              <li>The stack will be rebuilt (this may take 10-30 minutes)</li>
              <li>You can restart the stack after the build completes</li>
            </ul>
          </div>

          <ConfigMigrationModeChoice
            value={configMigrationMode}
            onChange={setConfigMigrationMode}
            disabled={isUpdating}
          />

          <div className="rounded-md bg-red-50 border border-red-200 p-4">
            <p className="text-sm text-red-800 font-medium">
              ⚠️ Warning: This will restart your server. Active players will be disconnected.
            </p>
          </div>
        </div>

        <div className="border-t border-gray-200 px-6 py-4 flex justify-end gap-3">
          <button
            onClick={onCancel}
            disabled={isUpdating}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={() => onConfirm(configMigrationMode)}
            disabled={isUpdating}
            className="flex items-center gap-2 rounded-md bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-50"
          >
            {isUpdating ? (
              <>
                <Loader2 className="h-4 w-4 animate-spin" />
                Updating...
              </>
            ) : (
              'Update Stack'
            )}
          </button>
        </div>
      </div>
    </div>
  )
}
