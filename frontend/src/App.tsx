import { Suspense } from 'react'
import { BrowserRouter, Routes, Route } from 'react-router-dom'
import Layout from '@/components/Layout'
import PageLoader from '@/components/PageLoader'
import ProtectedRoute from '@/components/ProtectedRoute'
import { AuthProvider } from '@/contexts/AuthContext'
import { lazyWithRetry } from '@/lib/lazyWithRetry'
import BuildProgressPage from '@/pages/BuildProgressPage'

const HomePage = lazyWithRetry(() => import('@/pages/HomePage'))
const StackListPage = lazyWithRetry(() => import('@/pages/StackListPage'))
const StackDetailsPage = lazyWithRetry(() => import('@/pages/StackDetailsPage'))
const CreateStackWizardPage = lazyWithRetry(() => import('@/pages/CreateStackWizardPage'))
const ContainerLogsPage = lazyWithRetry(() => import('@/pages/ContainerLogsPage'))
const LauncherPage = lazyWithRetry(() => import('@/pages/LauncherPage'))
const GlobalNewsPage = lazyWithRetry(() => import('@/pages/GlobalNewsPage'))
const GlobalDockerPage = lazyWithRetry(() => import('@/pages/GlobalDockerPage'))
const CloudSettingsPage = lazyWithRetry(() => import('@/pages/CloudSettingsPage'))
const NotFoundPage = lazyWithRetry(() => import('@/pages/NotFoundPage'))
const LoginPage = lazyWithRetry(() => import('@/pages/LoginPage'))

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
            <Route path="stacks/:stackId/build" element={<BuildProgressPage />} />
            <Route path="stacks/:stackId/containers/:containerName/logs" element={<Suspense fallback={<PageLoader />}><ContainerLogsPage /></Suspense>} />
            <Route path="launcher" element={<Suspense fallback={<PageLoader />}><LauncherPage /></Suspense>} />
            <Route path="news" element={<Suspense fallback={<PageLoader />}><GlobalNewsPage /></Suspense>} />
            <Route path="docker" element={<Suspense fallback={<PageLoader />}><GlobalDockerPage /></Suspense>} />
            <Route path="cloud" element={<Suspense fallback={<PageLoader />}><CloudSettingsPage /></Suspense>} />
            <Route path="*" element={<Suspense fallback={<PageLoader />}><NotFoundPage /></Suspense>} />
          </Route>
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  )
}

export default App
