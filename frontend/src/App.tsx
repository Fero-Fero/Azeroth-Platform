import { Suspense } from 'react'
import { BrowserRouter, Routes, Route } from 'react-router-dom'
import { Toaster } from 'sonner'
import Layout from '@/components/common/Layout'
import PageLoader from '@/components/common/PageLoader'
import ProtectedRoute from '@/components/common/ProtectedRoute'
import { AuthProvider } from '@/contexts/AuthContext'
import { lazyWithRetry } from '@/lib/lazyWithRetry'
import BuildProgressPage from '@/pages/BuildProgressPage'
import CloudSettingsPage from '@/pages/CloudSettingsPage'
import HomePage from '@/pages/HomePage'
import StackDetailsPage from '@/pages/StackDetailsPage'
import StackListPage from '@/pages/StackListPage'

const CreateStackWizardPage = lazyWithRetry(() => import('@/pages/CreateStackWizardPage'))
const ContainerLogsPage = lazyWithRetry(() => import('@/pages/ContainerLogsPage'))
const LauncherPage = lazyWithRetry(() => import('@/pages/LauncherPage'))
const GlobalNewsPage = lazyWithRetry(() => import('@/pages/GlobalNewsPage'))
const GlobalDockerPage = lazyWithRetry(() => import('@/pages/GlobalDockerPage'))
const CloudOAuthCallbackPage = lazyWithRetry(() => import('@/pages/CloudOAuthCallbackPage'))
const NotFoundPage = lazyWithRetry(() => import('@/pages/NotFoundPage'))
const LoginPage = lazyWithRetry(() => import('@/pages/LoginPage'))

// The whole React app is the admin panel; it lives under /admin. Absolute in-app links like
// "/stacks" resolve to "/admin/stacks" thanks to the router basename.
function App() {
  return (
    <AuthProvider>
      <Toaster richColors position="top-right" />
      <BrowserRouter basename="/admin">
        <Routes>
          <Route path="/login" element={<Suspense fallback={<PageLoader />}><LoginPage /></Suspense>} />
          <Route
            path="/cloud/oauth-callback"
            element={<Suspense fallback={<PageLoader />}><CloudOAuthCallbackPage /></Suspense>}
          />
          <Route
            path="/"
            element={
              <ProtectedRoute>
                <Layout />
              </ProtectedRoute>
            }
          >
            <Route index element={<HomePage />} />
            <Route path="stacks" element={<StackListPage />} />
            <Route path="stacks/new" element={<Suspense fallback={<PageLoader />}><CreateStackWizardPage /></Suspense>} />
            <Route path="stacks/:stackId" element={<StackDetailsPage />} />
            <Route path="stacks/:stackId/build" element={<BuildProgressPage />} />
            <Route path="stacks/:stackId/containers/:containerName/logs" element={<Suspense fallback={<PageLoader />}><ContainerLogsPage /></Suspense>} />
            <Route path="launcher" element={<Suspense fallback={<PageLoader />}><LauncherPage /></Suspense>} />
            <Route path="news" element={<Suspense fallback={<PageLoader />}><GlobalNewsPage /></Suspense>} />
            <Route path="docker" element={<Suspense fallback={<PageLoader />}><GlobalDockerPage /></Suspense>} />
            <Route path="cloud" element={<CloudSettingsPage />} />
            <Route path="*" element={<Suspense fallback={<PageLoader />}><NotFoundPage /></Suspense>} />
          </Route>
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  )
}

export default App
