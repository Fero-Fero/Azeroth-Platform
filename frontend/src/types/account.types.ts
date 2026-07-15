// Account DTOs
export interface AccountDto {
  id: number
  username: string
  gmLevel: number
  lastLogin: string | null
  characterCount: number
  isOnline: boolean
  isBanned: boolean
  banExpiry: string | null
  banReason: string | null
  bannedBy: string | null
}

export interface CharacterDto {
  guid: number
  name: string
  race: number
  class: number
  level: number
  gender: number
  online: boolean
  totaltime: number
  leveltime: number
  money: number
  account: number
  accountUsername?: string
  map: number
  zone: number
  guild?: string
}

// Request DTOs
export interface CreateAccountRequest {
  username: string
  password: string
  expansion?: number
}

export interface SetGmLevelRequest {
  username: string
  level: number
  realmId: number
}

export interface BanAccountRequest {
  username: string
  duration: string // "30m" | "1h" | "12h" | "1d" | "7d" | "30d" | "-1" (permanent)
  reason: string
}

export interface SetPasswordRequest {
  username: string
  password: string
}

export interface DeleteAccountRequest {
  username: string
}

export interface UnbanAccountRequest {
  username: string
}

export interface UnbanIpRequest {
  ipAddress: string
}

export interface SendMessageRequest {
  subject: string
  body: string
}

export interface SendItemsRequest {
  itemId: number
  count: number
  subject: string
  body: string
}

export interface SendMoneyRequest {
  copperAmount: number
  subject: string
  body: string
}

export interface SetLevelRequest {
  level: number
}

export interface BanCharacterRequest {
  duration: string // e.g. "30m", "7d", "-1" for permanent
  reason: string
}

export interface MuteCharacterRequest {
  minutes: number
  reason: string
}

export interface ModifyMoneyRequest {
  copperAmount: number
}

export interface AddItemRequest {
  itemId: number
  count: number
}

// Response DTOs
export interface AccountActionResponse {
  success: boolean
  message: string
}

export interface CharacterActionResponse {
  success: boolean
  message: string
}

export interface AhBotSetupResultDto {
  accountId: number
  allianceGuid: number
  hordeGuid: number
  charactersCreated: boolean
}
