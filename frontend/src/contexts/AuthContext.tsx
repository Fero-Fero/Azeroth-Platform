import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { authApi, authToken } from '@/services/api'

interface AuthContextValue {
  isAuthenticated: boolean
  /** Still resolving the initial token check. */
  loading: boolean
  login: (password: string) => Promise<void>
  logout: () => void
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [isAuthenticated, setIsAuthenticated] = useState(false)
  const [loading, setLoading] = useState(true)

  // Validate any stored token on mount so a refresh keeps the session.
  useEffect(() => {
    let cancelled = false
    const token = authToken.get()
    if (!token) {
      setLoading(false)
      return
    }
    authApi
      .me()
      .then(() => {
        if (!cancelled) setIsAuthenticated(true)
      })
      .catch(() => {
        authToken.clear()
        if (!cancelled) setIsAuthenticated(false)
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [])

  const value = useMemo<AuthContextValue>(
    () => ({
      isAuthenticated,
      loading,
      login: async (password: string) => {
        const { data } = await authApi.login(password)
        authToken.set(data.token)
        setIsAuthenticated(true)
      },
      logout: () => {
        authApi.logout().catch(() => {})
        authToken.clear()
        setIsAuthenticated(false)
      },
    }),
    [isAuthenticated, loading],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext)
  if (!ctx) {
    throw new Error('useAuth must be used within an AuthProvider')
  }
  return ctx
}
