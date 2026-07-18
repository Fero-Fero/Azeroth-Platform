import { Outlet, Link, useNavigate } from 'react-router-dom'
import { useAuth } from '@/contexts/AuthContext'

export default function Layout() {
  const { logout } = useAuth()
  const navigate = useNavigate()

  const handleLogout = () => {
    logout()
    // Explicit, router-relative navigation (basename "/admin" is applied once -> /admin/login).
    navigate('/login', { replace: true })
  }

  return (
    <div className="min-h-screen bg-gray-50">
      <nav className="bg-white shadow">
        <div className="container mx-auto px-4">
          <div className="flex h-16 items-center justify-between">
            <div className="flex items-center space-x-8">
              <Link to="/" className="text-xl font-bold text-gray-900">
                Azeroth Platform
              </Link>
              <Link 
                to="/stacks" 
                className="text-gray-600 hover:text-gray-900 transition"
              >
                Stacks
              </Link>
              <Link
                to="/launcher"
                className="text-gray-600 hover:text-gray-900 transition"
              >
                Launcher
              </Link>
              <Link
                to="/news"
                className="text-gray-600 hover:text-gray-900 transition"
              >
                Global News
              </Link>
              <Link
                to="/docker"
                className="text-gray-600 hover:text-gray-900 transition"
              >
                Docker
              </Link>
            </div>
            <div className="flex items-center gap-3">
              <Link
                to="/stacks/new"
                className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition"
              >
                Create Stack
              </Link>
              <button
                onClick={handleLogout}
                className="px-3 py-2 text-gray-600 hover:text-gray-900 transition"
                title="Sign out"
              >
                Sign out
              </button>
            </div>
          </div>
        </div>
      </nav>
      
      <main className="container mx-auto px-4 py-8">
        <Outlet />
      </main>
    </div>
  )
}
