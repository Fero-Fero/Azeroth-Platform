import { Trash2, AlertTriangle } from 'lucide-react'

interface DeleteStackDialogProps {
  stackName: string
  isExternal?: boolean
  onConfirm: () => void
  onCancel: () => void
  isDeleting: boolean
}

export default function DeleteStackDialog({
  stackName,
  isExternal = false,
  onConfirm,
  onCancel,
  isDeleting,
}: DeleteStackDialogProps) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-md rounded-lg bg-white shadow-xl">
        <div className="border-b border-gray-200 px-6 py-4">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-full bg-red-100">
              <AlertTriangle className="h-5 w-5 text-red-600" />
            </div>
            <h2 className="text-xl font-semibold text-gray-900">
              {isExternal ? 'Remove VPC stack from manager' : 'Delete Stack'}
            </h2>
          </div>
        </div>

        <div className="px-6 py-4">
          <p className="text-gray-700">
            Are you sure you want to remove <strong className="font-semibold text-gray-900">{stackName}</strong>
            {isExternal ? ' from this manager' : ''}?
          </p>
          <div className="mt-4 rounded-md border border-yellow-200 bg-yellow-50 p-3">
            {isExternal ? (
              <>
                <p className="text-sm text-yellow-800">This removes the stack from the manager only:</p>
                <ul className="mt-2 list-inside list-disc space-y-1 text-sm text-yellow-700">
                  <li>Manager database record and SSH connection</li>
                  <li>Local build/config files on this PC</li>
                </ul>
                <p className="mt-2 text-sm text-yellow-800">
                  Containers, volumes, and images on the remote VPC are <strong>not</strong> stopped or deleted.
                </p>
              </>
            ) : (
              <>
                <p className="text-sm text-yellow-800">This will permanently delete:</p>
                <ul className="mt-2 list-inside list-disc space-y-1 text-sm text-yellow-700">
                  <li>All containers and volumes</li>
                  <li>Built Docker images</li>
                  <li>Stack configuration and files</li>
                </ul>
              </>
            )}
          </div>
          <p className="mt-4 text-sm text-gray-600">
            {isExternal
              ? 'You can re-import the stack later if the remote server is still running.'
              : 'This action cannot be undone.'}
          </p>
        </div>

        <div className="flex justify-end gap-3 border-t border-gray-200 px-6 py-4">
          <button
            onClick={onCancel}
            disabled={isDeleting}
            className="rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-200 disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={onConfirm}
            disabled={isDeleting}
            className="flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50"
          >
            <Trash2 className="h-4 w-4" />
            {isDeleting ? 'Removing…' : isExternal ? 'Remove from manager' : 'Delete Stack'}
          </button>
        </div>
      </div>
    </div>
  )
}
