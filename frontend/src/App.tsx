import { lazy, Suspense } from 'react'
import { BrowserRouter, Routes, Route } from 'react-router-dom'
import Layout from '@/components/Layout'
import PageLoader from '@/components/PageLoader'
import ProtectedRoute from '@/components/ProtectedRoute'
import { AuthProvider } from '@/contexts/AuthContext'

const HomePage = lazy(() => import('@/pages/HomePage'))
const StackListPage = lazy(() => import('@/pages/StackListPage'))
const StackDetailsPage = lazy(() => import('@/pages/StackDetailsPage'))
const CreateStackWizardPage = lazy(() => import('@/pages/CreateStackWizardPage'))
const BuildProgressPage = lazy(() => import('@/pages/BuildProgressPage'))
const ContainerLogsPage = lazy(() => import('@/pages/ContainerLogsPage'))
const LauncherPage = lazy(() => import('@/pages/LauncherPage'))
const GlobalNewsPage = lazy(() => import('@/pages/GlobalNewsPage'))
const NotFoundPage = lazy(() => import('@/pages/NotFoundPage'))
const LoginPage = lazy(() => import('@/pages/LoginPage'))

// The whole React app is the admin panel; it lives under /admin. Absolute in-app links like
// "/stacks" resolve to "/admin/stacks" thanks to the router basename.
function App() {
  return (
    <AuthProvider>
      <BrowserRouter basename="/admin">
        <Routes>
          <Route path="/login" element={<Suspense fallback={<PageLoader />}><LoginPage /></Suspense>} />
          <Route
            path="/"
            element={
              <ProtectedRoute>
                <Layout />
              </ProtectedRoute>
            }
          >
            <Route index element={<Suspense fallback={<PageLoader />}><HomePage /></Suspense>} />
            <Route path="stacks" element={<Suspense fallback={<PageLoader />}><StackListPage /></Suspense>} />
            <Route path="stacks/new" element={<Suspense fallback={<PageLoader />}><CreateStackWizardPage /></Suspense>} />
            <Route path="stacks/:stackId" element={<Suspense fallback={<PageLoader />}><StackDetailsPage /></Suspense>} />
            <Route path="stacks/:stackId/build" element={<Suspense fallback={<PageLoader />}><BuildProgressPage /></Suspense>} />
            <Route path="stacks/:stackId/containers/:containerName/logs" element={<Suspense fallback={<PageLoader />}><ContainerLogsPage /></Suspense>} />
            <Route path="launcher" element={<Suspense fallback={<PageLoader />}><LauncherPage /></Suspense>} />
            <Route path="news" element={<Suspense fallback={<PageLoader />}><GlobalNewsPage /></Suspense>} />
            <Route path="*" element={<Suspense fallback={<PageLoader />}><NotFoundPage /></Suspense>} />
          </Route>
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  )
}

export default App
