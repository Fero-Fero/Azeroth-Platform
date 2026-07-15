// Realm management types (backed by acore_auth.realmlist)

export interface RealmDto {
  id: number
  name: string
  address: string
  port: number
  /** realmlist `icon` column: 0 Normal, 1 PvP, 6 RP, 8 RP PvP */
  type: number
  /** realmlist `flag` bitmask: 0x02 Offline, 0x20 Recommended, 0x40 New Players */
  flags: number
  timezone: number
  allowedSecurityLevel: number
  population: number
}

export interface UpdateRealmRequest {
  name: string
  type: number
  flags: number
  timezone: number
  allowedSecurityLevel: number
}

export interface CreateRealmRequest {
  name: string
  type: number
  flags: number
  timezone: number
  allowedSecurityLevel: number
}

// Realm type (realmlist `icon` column)
export const REALM_TYPES: { value: number; label: string }[] = [
  { value: 0, label: 'Normal (PvE)' },
  { value: 1, label: 'PvP' },
  { value: 6, label: 'RP' },
  { value: 8, label: 'RP PvP' },
]

export function realmTypeLabel(type: number): string {
  return REALM_TYPES.find(t => t.value === type)?.label ?? `Unknown (${type})`
}

// Realm flag bits (realmlist `flag` column)
export const REALM_FLAG_OFFLINE = 0x02
export const REALM_FLAG_RECOMMENDED = 0x20
export const REALM_FLAG_NEW_PLAYERS = 0x40

// GM security levels that may connect
export const SECURITY_LEVELS: { value: number; label: string }[] = [
  { value: 0, label: 'Everyone' },
  { value: 1, label: 'Moderators & up' },
  { value: 2, label: 'Game Masters & up' },
  { value: 3, label: 'Administrators only' },
]

export function securityLevelLabel(level: number): string {
  return SECURITY_LEVELS.find(l => l.value === level)?.label ?? `Level ${level}`
}

// Client realm region / timezone categories
export const REALM_TIMEZONES: { value: number; label: string }[] = [
  { value: 1, label: 'Development' },
  { value: 2, label: 'United States' },
  { value: 3, label: 'Oceanic' },
  { value: 4, label: 'Latin America' },
  { value: 5, label: 'Tournament' },
  { value: 6, label: 'Korea' },
  { value: 7, label: 'English' },
  { value: 8, label: 'German' },
  { value: 9, label: 'French' },
  { value: 10, label: 'Spanish' },
  { value: 11, label: 'Russian' },
  { value: 13, label: 'Taiwan' },
  { value: 14, label: 'China' },
  { value: 16, label: 'CN1' },
  { value: 17, label: 'CN2' },
]

export function timezoneLabel(timezone: number): string {
  return REALM_TIMEZONES.find(t => t.value === timezone)?.label ?? `Zone ${timezone}`
}
