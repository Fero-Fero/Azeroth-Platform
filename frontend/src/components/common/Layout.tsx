import { Outlet, Link, useNavigate } from 'react-router-dom'
import { Plus } from 'lucide-react'
import { useAuth } from '@/contexts/AuthContext'
import { useAssertServerTypeRegistry } from '@/server-types'

const navLinkClass =
  'whitespace-nowrap text-sm text-gray-600 hover:text-gray-900 transition'

export default function Layout() {
  useAssertServerTypeRegistry()
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
          <div className="flex min-h-14 flex-wrap items-center justify-between gap-x-4 gap-y-2 py-2 md:h-16 md:flex-nowrap md:py-0">
            <div className="flex min-w-0 flex-1 flex-wrap items-center gap-x-4 gap-y-1 md:gap-x-6 lg:gap-x-8">
              <Link to="/" className="shrink-0 text-lg font-bold text-gray-900 md:text-xl">
                Azeroth Platform
              </Link>
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1 md:gap-x-5 lg:gap-x-6">
                <Link to="/stacks" className={navLinkClass}>
                  Stacks
                </Link>
                <Link to="/launcher" className={navLinkClass}>
                  Launcher
                </Link>
                <Link to="/news" className={navLinkClass}>
                  Global News
                </Link>
                <Link to="/docker" className={navLinkClass}>
                  Docker
                </Link>
                <Link to="/cloud" className={navLinkClass}>
                  Cloud
                </Link>
              </div>
            </div>
            <div className="flex shrink-0 items-center gap-2">
              <Link
                to="/stacks/new"
                className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-2.5 py-1.5 text-xs font-medium text-white transition hover:bg-blue-700 md:text-sm"
              >
                <Plus className="h-3.5 w-3.5 md:h-4 md:w-4" />
                <span className="hidden sm:inline">Create Stack</span>
                <span className="sm:hidden">New</span>
              </Link>
              <button
                onClick={handleLogout}
                className="rounded-md px-2 py-1.5 text-xs text-gray-600 transition hover:text-gray-900 md:text-sm"
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
