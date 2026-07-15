import { createContext, useCallback, useContext, type ReactNode } from 'react'
import { armoryAssetsApi } from '@/services/api'
import { useArmoryJob } from '@/hooks/useArmoryJob'
import type { ArmoryJobStatus } from '@/types/stack.types'

interface ArmoryJobContextValue {
  job: ArmoryJobStatus | null
  isArmoryBusy: boolean
  isRebuildRunning: boolean
  applyStatus: (status: ArmoryJobStatus | null | undefined) => void
  enqueueRebuild: () => Promise<ArmoryJobStatus | null>
}

const ArmoryJobContext = createContext<ArmoryJobContextValue | null>(null)

export function ArmoryJobProvider({ stackId, children }: { stackId: string; children: ReactNode }) {
  const { job, isArmoryBusy, applyStatus } = useArmoryJob(stackId)

  const enqueueRebuild = useCallback(async () => {
    const response = await armoryAssetsApi.rebuildImage(stackId)
    applyStatus(response.data)
    return response.data
  }, [stackId, applyStatus])

  const isRebuildRunning = isArmoryBusy && job?.action === 'Rebuild'

  return (
    <ArmoryJobContext.Provider
      value={{ job, isArmoryBusy, isRebuildRunning, applyStatus, enqueueRebuild }}
    >
      {children}
    </ArmoryJobContext.Provider>
  )
}

export function useArmoryJobContext() {
  const context = useContext(ArmoryJobContext)
  if (!context) {
    throw new Error('useArmoryJobContext must be used within ArmoryJobProvider')
  }
  return context
}
