import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { accountApi, characterApi } from '@/services/accountApi'
import type {
  CreateAccountRequest,
  SetGmLevelRequest,
  BanAccountRequest,
  SetPasswordRequest,
  DeleteAccountRequest,
  UnbanAccountRequest,
  SendMessageRequest,
  SendItemsRequest,
  SendMoneyRequest,
  SetLevelRequest,
} from '@/types/account.types'

export const accountKeys = {
  all: ['accounts'] as const,
  lists: () => [...accountKeys.all, 'list'] as const,
  list: (stackId: string) => [...accountKeys.lists(), stackId] as const,
}

// Query hooks
export function useAccounts(stackId: string) {
  return useQuery({
    queryKey: accountKeys.list(stackId),
    queryFn: async () => {
      const response = await accountApi.list(stackId)
      return response.data
    },
    enabled: !!stackId,
    // Poll every 5s normally; while the query is failing (e.g. the DB is still coming up) poll faster
    // so the list recovers promptly once MySQL starts accepting connections.
    refetchInterval: (query) => (query.state.status === 'error' ? 3000 : 5000),
  })
}

// Account mutation hooks
export function useCreateAccount(stackId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (request: CreateAccountRequest) => accountApi.create(stackId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: accountKeys.list(stackId) })
    },
  })
}

export function useSetGmLevel(stackId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accountId, request }: { accountId: number; request: SetGmLevelRequest }) =>
      accountApi.setGmLevel(stackId, accountId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: accountKeys.list(stackId) })
    },
  })
}

export function useBanAccount(stackId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accountId, request }: { accountId: number; request: BanAccountRequest }) =>
      accountApi.ban(stackId, accountId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: accountKeys.list(stackId) })
    },
  })
}

export function useDeleteAccount(stackId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accountId, request }: { accountId: number; request: DeleteAccountRequest }) =>
      accountApi.delete(stackId, accountId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: accountKeys.list(stackId) })
    },
  })
}

export function useSetPassword(stackId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accountId, request }: { accountId: number; request: SetPasswordRequest }) =>
      accountApi.setPassword(stackId, accountId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: accountKeys.list(stackId) })
    },
  })
}

export function useUnbanAccount(stackId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accountId, request }: { accountId: number; request: UnbanAccountRequest }) =>
      accountApi.unban(stackId, accountId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: accountKeys.list(stackId) })
    },
  })
}

// Character mutation hooks
export function useSendMessage(stackId: string) {
  return useMutation({
    mutationFn: ({ characterName, request }: { characterName: string; request: SendMessageRequest }) =>
      characterApi.sendMessage(stackId, characterName, request),
  })
}

export function useSendItems(stackId: string) {
  return useMutation({
    mutationFn: ({ characterName, request }: { characterName: string; request: SendItemsRequest }) =>
      characterApi.sendItems(stackId, characterName, request),
  })
}

export function useSendMoney(stackId: string) {
  return useMutation({
    mutationFn: ({ characterName, request }: { characterName: string; request: SendMoneyRequest }) =>
      characterApi.sendMoney(stackId, characterName, request),
  })
}

export function useKickPlayer(stackId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (characterName: string) => characterApi.kick(stackId, characterName),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: accountKeys.list(stackId) })
    },
  })
}

export function useRenameCharacter(stackId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (characterName: string) => characterApi.rename(stackId, characterName),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: accountKeys.list(stackId) })
    },
  })
}

export function useCustomizeCharacter(stackId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (characterName: string) => characterApi.customize(stackId, characterName),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: accountKeys.list(stackId) })
    },
  })
}

export function useSetLevel(stackId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ characterName, request }: { characterName: string; request: SetLevelRequest }) =>
      characterApi.setLevel(stackId, characterName, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: accountKeys.list(stackId) })
    },
  })
}
