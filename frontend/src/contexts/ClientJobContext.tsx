import { createContext, useCallback, useContext, useState, type ReactNode } from 'react'
import { useClientJob } from '@/hooks/useClientJob'
import type { ClientJobStatus } from '@/types/stack.types'

interface ClientJobContextValue {
  job: ClientJobStatus | null
  isClientBusy: boolean
  applyStatus: (status: ClientJobStatus | null | undefined) => void
  /**
   * True while a base-client archive is streaming from this browser to the manager. Unlike the
   * background job, this upload lives in the page: reloading or closing the tab aborts it.
   */
  isUploading: boolean
  setUploading: (uploading: boolean) => void
}

const ClientJobContext = createContext<ClientJobContextValue | null>(null)

export function ClientJobProvider({ stackId, children }: { stackId: string; children: ReactNode }) {
  const { job, isClientBusy, applyStatus } = useClientJob(stackId)
  const [isUploading, setIsUploading] = useState(false)

  const setUploading = useCallback((uploading: boolean) => setIsUploading(uploading), [])

  return (
    <ClientJobContext.Provider value={{ job, isClientBusy, applyStatus, isUploading, setUploading }}>
      {children}
    </ClientJobContext.Provider>
  )
}

export function useClientJobContext() {
  const context = useContext(ClientJobContext)
  if (!context) {
    throw new Error('useClientJobContext must be used within ClientJobProvider')
  }
  return context
}
