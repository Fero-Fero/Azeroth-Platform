import { AlertTriangle } from 'lucide-react'

interface LeaveClientUploadDialogProps {
  onStay: () => void
  onLeave: () => void
}

/**
 * Shown when the operator navigates away from the Client tab while a base-client archive is still
 * uploading from this browser.
 */
export default function LeaveClientUploadDialog({ onStay, onLeave }: LeaveClientUploadDialogProps) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-md rounded-lg bg-white p-6 shadow-xl">
        <div className="flex items-start gap-3">
          <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-500" />
          <div>
            <h3 className="text-lg font-semibold text-gray-900">Upload still in progress</h3>
            <p className="mt-2 text-sm text-gray-600">
              The client archive is still uploading. Leaving this tab hides the progress bar, and you
              will not see whether the upload finished or failed until you come back.
            </p>
            <p className="mt-2 text-sm text-gray-600">
              The upload itself keeps running in the background. Reloading or closing the browser is
              what cancels it, and then nothing is installed.
            </p>
          </div>
        </div>

        <div className="mt-6 flex justify-end gap-2">
          <button
            type="button"
            onClick={onStay}
            className="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
          >
            Stay on Client
          </button>
          <button
            type="button"
            onClick={onLeave}
            className="rounded-md border px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
          >
            Leave anyway
          </button>
        </div>
      </div>
    </div>
  )
}
