import { AlertTriangle, Trash2 } from 'lucide-react'
import { useState } from 'react'

interface PurgeClientDataDialogProps {
  onConfirm: () => void
  onCancel: () => void
  isPurging: boolean
  error?: string | null
}

const CONFIRM_PHRASE = 'purge'

export default function PurgeClientDataDialog({
  onConfirm,
  onCancel,
  isPurging,
  error = null,
}: PurgeClientDataDialogProps) {
  const [typed, setTyped] = useState('')
  const confirmed = typed.trim().toLowerCase() === CONFIRM_PHRASE

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-lg rounded-lg bg-white shadow-xl">
        <div className="border-b border-gray-200 px-6 py-4">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-full bg-red-100">
              <AlertTriangle className="h-5 w-5 text-red-600" />
            </div>
            <h2 className="text-xl font-semibold text-gray-900">Purge client data</h2>
          </div>
        </div>

        <div className="space-y-4 px-6 py-4">
          <p className="text-sm text-gray-700">
            Empties everything this stack serves to launchers, so a broken or half-uploaded client can
            be rebuilt from scratch.
          </p>

          <div className="rounded-md border border-red-200 bg-red-50 p-3">
            <p className="text-sm font-medium text-red-900">Deleted</p>
            <ul className="mt-2 list-inside list-disc space-y-1 text-sm text-red-800">
              <li>The uploaded base client (Wow.exe, Data/*.MPQ, everything else)</li>
              <li>Published patch MPQs and addons in the client overlay</li>
              <li>The cached manifest, file hashes and verify token</li>
            </ul>
          </div>

          <div className="rounded-md border border-gray-200 bg-gray-50 p-3">
            <p className="text-sm font-medium text-gray-800">Kept</p>
            <ul className="mt-2 list-inside list-disc space-y-1 text-sm text-gray-600">
              <li>The built launcher and its download link</li>
              <li>Portal registry, branding and news</li>
              <li>Your patch definitions, so they can be reapplied</li>
              <li>Player installs: launchers refuse to delete a whole install off an empty manifest</li>
            </ul>
          </div>

          <div>
            <label htmlFor="purge-confirm" className="block text-sm text-gray-700">
              Type <span className="font-mono font-semibold">{CONFIRM_PHRASE}</span> to confirm.
            </label>
            <input
              id="purge-confirm"
              type="text"
              value={typed}
              autoFocus
              disabled={isPurging}
              onChange={(event) => setTyped(event.target.value)}
              className="mt-1 w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-red-500 focus:outline-none disabled:bg-gray-100"
            />
          </div>

          {error ? <p className="text-sm text-red-700">{error}</p> : null}
        </div>

        <div className="flex justify-end gap-3 border-t border-gray-200 px-6 py-4">
          <button
            onClick={onCancel}
            disabled={isPurging}
            className="rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-200 disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={onConfirm}
            disabled={isPurging || !confirmed}
            className="flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <Trash2 className="h-4 w-4" />
            {isPurging ? 'Purging…' : 'Purge client data'}
          </button>
        </div>
      </div>
    </div>
  )
}
