import { useMutation, useQueryClient } from '@tanstack/react-query'
import { stackKeys } from '@/hooks/useStacks'
import { stackApi } from '@/services/api'
import type { StackDetailsDto } from '@/types/stack.types'

function markRunning(
  queryClient: ReturnType<typeof useQueryClient>,
  stackId: string,
  message: string,
  phase?: StackDetailsDto['expressProvisionPhase'],
): StackDetailsDto | undefined {
  const key = stackKeys.detail(stackId)
  const previous = queryClient.getQueryData<StackDetailsDto>(key)
  queryClient.setQueryData<StackDetailsDto>(key, (current) =>
    current
      ? {
          ...current,
          expressProvisionStatus: 'Running',
          expressProvisionMessage: message,
          ...(phase ? { expressProvisionPhase: phase } : {}),
        }
      : current,
  )
  return previous
}

/** Starts, retries, or continues Express Setup with instant Running UI (work continues in the background). */
export function useExpressProvision(stackId: string) {
  const queryClient = useQueryClient()
  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
  }

  const start = useMutation({
    mutationFn: () => stackApi.startExpressProvision(stackId),
    onMutate: () => {
      void queryClient.cancelQueries({ queryKey: stackKeys.detail(stackId) })
      return markRunning(queryClient, stackId, 'Starting Express Setup…', 'SaveChoices')
    },
    onError: (_error, _vars, previous) => {
      if (previous) {
        queryClient.setQueryData(stackKeys.detail(stackId), previous)
      }
    },
    onSettled: invalidate,
  })

  const retry = useMutation({
    mutationFn: () => stackApi.retryExpressProvision(stackId),
    onMutate: () => {
      void queryClient.cancelQueries({ queryKey: stackKeys.detail(stackId) })
      return markRunning(queryClient, stackId, 'Retrying Express Setup…')
    },
    onError: (_error, _vars, previous) => {
      if (previous) {
        queryClient.setQueryData(stackKeys.detail(stackId), previous)
      }
    },
    onSettled: invalidate,
  })

  const continueAfterClient = useMutation({
    mutationFn: () => stackApi.continueExpressProvision(stackId),
    onMutate: () => {
      void queryClient.cancelQueries({ queryKey: stackKeys.detail(stackId) })
      return markRunning(queryClient, stackId, 'Continuing Express Setup…', 'WaitClient')
    },
    onError: (_error, _vars, previous) => {
      if (previous) {
        queryClient.setQueryData(stackKeys.detail(stackId), previous)
      }
    },
    onSettled: invalidate,
  })

  return { start, retry, continueAfterClient }
}
