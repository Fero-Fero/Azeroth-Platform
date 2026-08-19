import { useEffect, useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { dbcStoreApi, moduleExtraDataApi } from '@/services/api'
import type { ApplyModuleExtraDataRequest } from '@/types/module-extra-data.types'

export const dbcStoreKeys = {
  status: ['dbc-store'] as const,
}

export const moduleExtraDataKeys = {
  all: ['module-extra-data'] as const,
  choices: (stackId: string) => [...moduleExtraDataKeys.all, 'choices', stackId] as const,
  status: (stackId: string) => [...moduleExtraDataKeys.all, 'status', stackId] as const,
  stackStatus: (stackId: string) => [...moduleExtraDataKeys.all, 'stack-status', stackId] as const,
}

export function useDbcStoreStatus(pollWhenBusy = true) {
  return useQuery({
    queryKey: dbcStoreKeys.status,
    queryFn: async () => (await dbcStoreApi.status()).data,
    refetchInterval: (query) => {
      if (!pollWhenBusy) return false
      return query.state.data?.inProgress ? 2000 : false
    },
  })
}

export function useSyncDbcStore() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (force: boolean) => (await dbcStoreApi.sync(force)).data,
    onSuccess: (data) => {
      queryClient.setQueryData(dbcStoreKeys.status, data)
    },
  })
}

export function useModuleExtraDataChoices(stackId: string, enabled = true) {
  return useQuery({
    queryKey: moduleExtraDataKeys.choices(stackId),
    queryFn: async () => (await moduleExtraDataApi.choices(stackId)).data,
    enabled: enabled && !!stackId,
  })
}

export function useModuleExtraDataStackStatus(stackId: string, enabled = true) {
  return useQuery({
    queryKey: moduleExtraDataKeys.stackStatus(stackId),
    queryFn: async () => (await moduleExtraDataApi.stackStatus(stackId)).data,
    enabled: enabled && !!stackId,
  })
}

export function useModuleExtraDataJob(stackId: string, active: boolean) {
  return useQuery({
    queryKey: moduleExtraDataKeys.status(stackId),
    queryFn: async () => (await moduleExtraDataApi.status(stackId)).data,
    enabled: !!stackId,
    refetchInterval: active ? 1500 : false,
  })
}

export function useSaveModuleExtraDataChoices(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (request: ApplyModuleExtraDataRequest) =>
      (await moduleExtraDataApi.saveChoices(stackId, request)).data,
    onSuccess: (data) => {
      queryClient.setQueryData(moduleExtraDataKeys.stackStatus(stackId), data)
      queryClient.invalidateQueries({ queryKey: moduleExtraDataKeys.choices(stackId) })
    },
  })
}

export function usePrepareModuleExtraData(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (request: ApplyModuleExtraDataRequest) =>
      (await moduleExtraDataApi.prepare(stackId, request)).data,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: moduleExtraDataKeys.status(stackId) })
      queryClient.invalidateQueries({ queryKey: moduleExtraDataKeys.stackStatus(stackId) })
    },
  })
}

export function useDepositModuleExtraData(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async () => (await moduleExtraDataApi.deposit(stackId)).data,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: moduleExtraDataKeys.status(stackId) })
      queryClient.invalidateQueries({ queryKey: moduleExtraDataKeys.stackStatus(stackId) })
    },
  })
}

export function useApplyModuleExtraData(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (request: ApplyModuleExtraDataRequest) =>
      (await moduleExtraDataApi.apply(stackId, request)).data,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: moduleExtraDataKeys.status(stackId) })
      queryClient.invalidateQueries({ queryKey: moduleExtraDataKeys.stackStatus(stackId) })
    },
  })
}

export function useAutoPrepareModuleExtras(stackId: string, dbcReady: boolean) {
  const choices = useModuleExtraDataChoices(stackId, dbcReady)
  const stackStatus = useModuleExtraDataStackStatus(stackId, dbcReady)
  const prepare = usePrepareModuleExtraData(stackId)
  const job = useModuleExtraDataJob(stackId, prepare.isPending || prepare.data?.isRunning === true)
  const started = useRef(false)

  const prepared = stackStatus.data?.prepared === true || stackStatus.data?.deposited === true
  const hasHookedModules = (choices.data?.modules?.length ?? 0) > 0
  const saved = choices.data?.saved ?? { selectionsByModuleId: {} }

  useEffect(() => {
    if (started.current) return
    if (!stackId || !dbcReady || prepared || !hasHookedModules) return
    if (choices.isLoading || stackStatus.isLoading) return
    if (job.data?.phase === 'Running' || job.data?.phase === 'Failed') return
    started.current = true
    prepare.mutate(saved)
  }, [
    stackId,
    dbcReady,
    prepared,
    hasHookedModules,
    choices.isLoading,
    stackStatus.isLoading,
    saved,
    job.data?.phase,
    prepare,
  ])

  return { prepare, job }
}
