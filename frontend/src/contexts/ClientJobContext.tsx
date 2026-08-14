import { createContext, useContext, type ReactNode } from 'react'
import { useClientJob } from '@/hooks/useClientJob'
import type { ClientJobStatus } from '@/types/stack.types'

interface ClientJobContextValue {
  job: ClientJobStatus | null
  isClientBusy: boolean
  applyStatus: (status: ClientJobStatus | null | undefined) => void
}

const ClientJobContext = createContext<ClientJobContextValue | null>(null)

export function ClientJobProvider({ stackId, children }: { stackId: string; children: ReactNode }) {
  const { job, isClientBusy, applyStatus } = useClientJob(stackId)

  return (
    <ClientJobContext.Provider value={{ job, isClientBusy, applyStatus }}>
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
