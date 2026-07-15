import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate, useLocation } from 'react-router-dom'
import { useAuth } from '@/contexts/AuthContext'

interface LocationState {
  from?: { pathname: string }
}

export default function LoginPage() {
  const { login, isAuthenticated } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  // `from` is a router-relative path (basename /admin already stripped), so it's used as-is.
  const redirectTo = (location.state as LocationState | null)?.from?.pathname ?? '/'

  // Already signed in (e.g. navigated to /login manually) → bounce to the dashboard.
  useEffect(() => {
    if (isAuthenticated) {
      navigate(redirectTo, { replace: true })
    }
  }, [isAuthenticated, navigate, redirectTo])

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await login(password)
      navigate(redirectTo, { replace: true })
    } catch {
      setError('Invalid password. Please try again.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-gray-950 px-4">
      <div className="w-full max-w-sm rounded-xl border border-gray-800 bg-gray-900 p-8 shadow-2xl">
        <div className="mb-6 text-center">
          <h1 className="text-2xl font-bold text-white">Azeroth Platform</h1>
          <p className="mt-1 text-sm text-gray-400">Admin sign in</p>
        </div>

        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="password" className="mb-1 block text-sm font-medium text-gray-300">
              Admin password
            </label>
            <input
              id="password"
              type="password"
              autoFocus
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full rounded-md border border-gray-700 bg-gray-950 px-3 py-2 text-white placeholder-gray-500 focus:border-blue-500 focus:outline-none"
              placeholder="Enter admin password"
            />
          </div>

          {error && <div className="rounded-md bg-red-500/10 px-3 py-2 text-sm text-red-400">{error}</div>}

          <button
            type="submit"
            disabled={submitting || password.length === 0}
            className="w-full rounded-md bg-blue-600 py-2 font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {submitting ? 'Signing in...' : 'Sign in'}
          </button>
        </form>
      </div>
    </div>
  )
}
