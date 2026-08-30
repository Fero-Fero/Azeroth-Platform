import { Navigate, useLocation } from 'react-router-dom'
import { useAuth } from '@/contexts/AuthContext'
import PageLoader from '@/components/common/PageLoader'

/**
 * Gates admin routes: while the initial token check runs, shows a loader; unauthenticated users are
 * redirected to the login page (preserving where they were headed).
 */
export default function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const { isAuthenticated, loading } = useAuth()
  const location = useLocation()

  if (loading) {
    return <PageLoader />
  }

  if (!isAuthenticated) {
    // Path is relative to the router basename ("/admin"), so this resolves to /admin/login.
    return <Navigate to="/login" replace state={{ from: location }} />
  }

  return <>{children}</>
}
