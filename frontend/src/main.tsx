import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClientProvider } from '@tanstack/react-query'
import { queryClient } from './lib/queryClient'
import './index.css'
import App from './App.tsx'

// The site is admin-only and served under /admin. Any other path (e.g. "/" or an old bookmark) is
// rewritten under /admin so the router picks it up; unauthenticated users then land on /admin/login.
if (!window.location.pathname.startsWith('/admin')) {
  const rest = window.location.pathname === '/' ? '' : window.location.pathname
  window.history.replaceState(null, '', `/admin${rest}${window.location.search}${window.location.hash}`)
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>
  </StrictMode>,
)
