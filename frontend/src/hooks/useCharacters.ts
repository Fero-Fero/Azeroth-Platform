import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { characterApi } from '@/services/accountApi'
import type {
  BanCharacterRequest,
  MuteCharacterRequest,
  ModifyMoneyRequest,
  AddItemRequest,
} from '@/types/account.types'

export const characterKeys = {
  all: ['characters'] as const,
  lists: () => [...characterKeys.all, 'list'] as const,
  list: (stackId: string) => [...characterKeys.lists(), stackId] as const,
  inventory: (stackId: string, characterGuid: number) =>
    [...characterKeys.all, 'inventory', stackId, characterGuid] as const,
}

// All characters for a stack
export function useAllCharacters(stackId: string) {
  return useQuery({
    queryKey: characterKeys.list(stackId),
    queryFn: async () => {
      const response = await characterApi.getAll(stackId)
      return response.data
    },
    enabled: !!stackId,
    // Poll every 10s normally; while the query is failing (e.g. the DB is still coming up) poll faster
    // so the list recovers promptly once MySQL starts accepting connections.
    refetchInterval: (query) => (query.state.status === 'error' ? 3000 : 10_000),
  })
}

// Character inventory
export function useCharacterInventory(stackId: string, characterGuid: number | null) {
  return useQuery({
    queryKey: characterKeys.inventory(stackId, characterGuid ?? 0),
    queryFn: async () => {
      const response = await characterApi.getInventory(stackId, characterGuid!)
      return response.data
    },
    enabled: !!stackId && !!characterGuid,
  })
}

// Ban character
export function useBanCharacter(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ characterName, request }: { characterName: string; request: BanCharacterRequest }) =>
      characterApi.ban(stackId, characterName, request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: characterKeys.list(stackId) }),
  })
}

// Unban character
export function useUnbanCharacter(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (characterName: string) => characterApi.unban(stackId, characterName),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: characterKeys.list(stackId) }),
  })
}

// Mute character
export function useMuteCharacter(stackId: string) {
  return useMutation({
    mutationFn: ({ characterName, request }: { characterName: string; request: MuteCharacterRequest }) =>
      characterApi.mute(stackId, characterName, request),
  })
}

export function useUnmuteCharacter(stackId: string) {
  return useMutation({
    mutationFn: (characterName: string) => characterApi.unmute(stackId, characterName),
  })
}

// Revive character
export function useReviveCharacter(stackId: string) {
  return useMutation({
    mutationFn: (characterName: string) => characterApi.revive(stackId, characterName),
  })
}

// Modify money
export function useModifyMoney(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ characterName, request }: { characterName: string; request: ModifyMoneyRequest }) =>
      characterApi.modifyMoney(stackId, characterName, request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: characterKeys.list(stackId) }),
  })
}

// Add item to inventory
export function useAddItem(stackId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({
      characterName,
      request,
    }: {
      characterGuid: number
      characterName: string
      request: AddItemRequest
    }) => characterApi.addItem(stackId, characterName, request),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: characterKeys.list(stackId) })
      queryClient.invalidateQueries({
        queryKey: characterKeys.inventory(stackId, variables.characterGuid),
      })
    },
  })
}
