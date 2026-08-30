import { useState } from 'react'
import { Loader2, Terminal } from 'lucide-react'

interface CreateAccountDialogProps {
  onClose: () => void
  onSubmit: (username: string, password: string) => Promise<void>
}

export default function CreateAccountDialog({ onClose, onSubmit }: CreateAccountDialogProps) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [error, setError] = useState('')

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')

    if (!username || !password) {
      setError('Username and password are required')
      return
    }

    if (password !== confirmPassword) {
      setError('Passwords do not match')
      return
    }

    if (username.length < 3 || username.length > 16) {
      setError('Username must be 3-16 characters')
      return
    }

    if (password.length < 4 || password.length > 16) {
      setError('Password must be 4-16 characters')
      return
    }

    if (/\s/.test(username) || /\s/.test(password)) {
      setError('Username and password cannot contain spaces (SOAP command is space-delimited)')
      return
    }

    setIsSubmitting(true)
    try {
      await onSubmit(username, password)
      onClose()
    } catch (err: any) {
      // The backend returns the SOAP fault under `error`; fall back to `message` for other errors.
      setError(
        err.response?.data?.error ||
          err.response?.data?.message ||
          'Failed to create account. Make sure the worldserver is running with SOAP enabled.'
      )
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-md">
        <div className="border-b border-gray-200 px-6 py-4">
          <div className="flex items-center gap-2">
            <h2 className="text-xl font-semibold text-gray-900">Create Account</h2>
            <span className="inline-flex items-center gap-1 px-1.5 py-0.5 bg-blue-100 text-blue-700 rounded text-[10px] font-semibold uppercase tracking-wide">
              <Terminal className="w-3 h-3" />
              SOAP
            </span>
          </div>
          <p className="mt-1 text-sm text-gray-500">
            Creates a game account through the worldserver's SOAP interface.
          </p>
        </div>

        <form onSubmit={handleSubmit} className="px-6 py-4 space-y-4">
          {error && (
            <div className="bg-red-50 text-red-600 p-3 rounded-md text-sm">
              {error}
            </div>
          )}

          <div className="bg-blue-50 border border-blue-100 rounded-md p-3 text-xs text-blue-800 space-y-1">
            <p className="font-semibold flex items-center gap-1">
              <Terminal className="w-3.5 h-3.5" />
              SOAP account requirements
            </p>
            <ul className="list-disc list-inside space-y-0.5">
              <li>The worldserver must be running with SOAP enabled</li>
              <li>The SOAP admin account must be initialized for this stack</li>
              <li>Username: 3-16 characters, no spaces</li>
              <li>Password: 4-16 characters, no spaces</li>
            </ul>
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Username
            </label>
            <input
              type="text"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="Enter username (3-16 characters)"
              disabled={isSubmitting}
              autoFocus
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Password
            </label>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="Enter password (4-16 characters)"
              disabled={isSubmitting}
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Confirm Password
            </label>
            <input
              type="password"
              value={confirmPassword}
              onChange={(e) => setConfirmPassword(e.target.value)}
              className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
              placeholder="Confirm password"
              disabled={isSubmitting}
            />
          </div>
        </form>

        <div className="border-t border-gray-200 px-6 py-4 flex justify-end gap-3">
          <button
            type="button"
            onClick={onClose}
            className="px-4 py-2 border border-gray-300 rounded-md hover:bg-gray-50 text-sm font-medium text-gray-700"
            disabled={isSubmitting}
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={handleSubmit}
            className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed flex items-center gap-2 text-sm font-medium"
            disabled={isSubmitting}
          >
            {isSubmitting ? (
              <>
                <Loader2 className="w-4 h-4 animate-spin" />
                Creating...
              </>
            ) : (
              'Create Account'
            )}
          </button>
        </div>
      </div>
    </div>
  )
}
