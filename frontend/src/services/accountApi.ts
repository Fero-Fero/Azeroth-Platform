import apiClient from './api'
import type {
  AccountDto,
  CharacterDto,
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
  BanCharacterRequest,
  MuteCharacterRequest,
  ModifyMoneyRequest,
  AddItemRequest,
  AccountActionResponse,
  CharacterActionResponse,
} from '@/types/account.types'
import type { CharacterInventoryDto } from '@/types/character.types'

// Account API
export const accountApi = {
  // List all accounts
  list: (stackId: string) =>
    apiClient.get<AccountDto[]>(`/stacks/${stackId}/accounts`),

  // Create account
  create: (stackId: string, request: CreateAccountRequest) =>
    apiClient.post<AccountActionResponse>(`/stacks/${stackId}/accounts`, request),

  // Set GM level
  setGmLevel: (stackId: string, accountId: number, request: SetGmLevelRequest) =>
    apiClient.post<AccountActionResponse>(
      `/stacks/${stackId}/accounts/${accountId}/set-gm-level`,
      request
    ),

  // Ban account
  ban: (stackId: string, accountId: number, request: BanAccountRequest) =>
    apiClient.post<AccountActionResponse>(
      `/stacks/${stackId}/accounts/${accountId}/ban`,
      request
    ),

  // Delete account
  delete: (stackId: string, accountId: number, request: DeleteAccountRequest) =>
    apiClient.delete<AccountActionResponse>(`/stacks/${stackId}/accounts/${accountId}`, { data: request }),

  // Set password
  setPassword: (stackId: string, accountId: number, request: SetPasswordRequest) =>
    apiClient.post<AccountActionResponse>(
      `/stacks/${stackId}/accounts/${accountId}/set-password`,
      request
    ),

  // Unban account
  unban: (stackId: string, accountId: number, request: UnbanAccountRequest) =>
    apiClient.post<AccountActionResponse>(
      `/stacks/${stackId}/accounts/${accountId}/unban`,
      request
    ),
}

// Character API
export const characterApi = {
  // Send message
  sendMessage: (stackId: string, characterName: string, request: SendMessageRequest) =>
    apiClient.post<CharacterActionResponse>(
      `/stacks/${stackId}/characters/${characterName}/send-message`,
      request
    ),

  // Send items
  sendItems: (stackId: string, characterName: string, request: SendItemsRequest) =>
    apiClient.post<CharacterActionResponse>(
      `/stacks/${stackId}/characters/${characterName}/send-items`,
      request
    ),

  // Send money
  sendMoney: (stackId: string, characterName: string, request: SendMoneyRequest) =>
    apiClient.post<CharacterActionResponse>(
      `/stacks/${stackId}/characters/${characterName}/send-money`,
      request
    ),

  // Kick player
  kick: (stackId: string, characterName: string) =>
    apiClient.post<CharacterActionResponse>(
      `/stacks/${stackId}/characters/${characterName}/kick`
    ),

  // Force rename
  rename: (stackId: string, characterName: string) =>
    apiClient.post<CharacterActionResponse>(
      `/stacks/${stackId}/characters/${characterName}/rename`
    ),

  // Force customize
  customize: (stackId: string, characterName: string) =>
    apiClient.post<CharacterActionResponse>(
      `/stacks/${stackId}/characters/${characterName}/customize`
    ),

  // Set level
  setLevel: (stackId: string, characterName: string, request: SetLevelRequest) =>
    apiClient.post<CharacterActionResponse>(
      `/stacks/${stackId}/characters/${characterName}/set-level`,
      request
    ),

  // Get full inventory (equipment, bags, bank) by character GUID
  getInventory: (stackId: string, characterGuid: number) =>
    apiClient.get<CharacterInventoryDto>(`/stacks/${stackId}/characters/${characterGuid}/inventory`),

  // Get all characters for the stack
  getAll: (stackId: string) =>
    apiClient.get<CharacterDto[]>(`/stacks/${stackId}/characters`),

  // Ban character
  ban: (stackId: string, characterName: string, request: BanCharacterRequest) =>
    apiClient.post<CharacterActionResponse>(`/stacks/${stackId}/characters/${characterName}/ban`, request),

  // Unban character
  unban: (stackId: string, characterName: string) =>
    apiClient.delete<CharacterActionResponse>(`/stacks/${stackId}/characters/${characterName}/ban`),

  // Mute character
  mute: (stackId: string, characterName: string, request: MuteCharacterRequest) =>
    apiClient.post<CharacterActionResponse>(`/stacks/${stackId}/characters/${characterName}/mute`, request),

  // Unmute character
  unmute: (stackId: string, characterName: string) =>
    apiClient.post<CharacterActionResponse>(`/stacks/${stackId}/characters/${characterName}/unmute`),

  // Revive character
  revive: (stackId: string, characterName: string) =>
    apiClient.post<CharacterActionResponse>(`/stacks/${stackId}/characters/${characterName}/revive`),

  // Modify gold (positive = add, negative = remove)
  modifyMoney: (stackId: string, characterName: string, request: ModifyMoneyRequest) =>
    apiClient.post<CharacterActionResponse>(`/stacks/${stackId}/characters/${characterName}/modify-money`, request),

  // Add item directly to inventory
  addItem: (stackId: string, characterName: string, request: AddItemRequest) =>
    apiClient.post<CharacterActionResponse>(`/stacks/${stackId}/characters/${characterName}/add-item`, request),
}
