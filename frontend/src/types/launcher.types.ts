export interface LauncherDistributionConfigDto {
  appName: string
  publicBaseUrl: string
  brandingTitle: string
  gameExecutable: string
  launchArguments: string
  clientVersion: string
  hasBackground: boolean
  hasLogo: boolean
  hasIcon: boolean
  template: string
  /**
   * When true, the compiled launcher requires players to log in with a game account before they can
   * download or play. Baked in at build time, so changing it requires rebuilding the launcher.
   */
  requireLogin: boolean
}

export interface LauncherNewsItemDto {
  id: string
  title: string
  /** ISO date (yyyy-MM-dd) or free text shown on the card. */
  date: string
  /** Sanitized HTML body. */
  html: string
  sortOrder: number
  /** Draft articles are saved but hidden from the launcher feed until published. */
  isDraft?: boolean
  /**
   * Optional content category shown as a colored corner ribbon on the news cards
   * (patch/announcement/expansion/event/update/hotfix). Empty means no ribbon.
   */
  tag?: string
  hasImage: boolean
  imageUrl?: string | null
}

export interface LauncherTemplateDto {
  id: string
  name: string
  description: string
  accentColor: string
  backgroundUrl?: string | null
  logoUrl?: string | null
  iconUrl?: string | null
}

export type LauncherBuildPhase =
  | 'Idle'
  | 'Preparing'
  | 'Publishing'
  | 'Packaging'
  | 'Completed'
  | 'Failed'

/**
 * Which segment of the launcher's four-part Release.Update.Minor.Patch version to bump on the next
 * build. Bumping a segment resets all less-significant segments to zero.
 */
export type LauncherVersionPart = 'Release' | 'Update' | 'Minor' | 'Patch'

export interface LauncherBuildStatusDto {
  phase: LauncherBuildPhase
  message: string
  isBuilding: boolean
  error?: string | null
  log: string[]
  availableVersion?: string | null
  availableBuiltAt?: string | null
  availableSizeBytes: number
  downloadAvailable: boolean
}

/** The launcher version a single stack currently serves, vs. the manager's most recent build. */
export interface LauncherStackVersionDto {
  stackId: string
  stackName: string
  portalUrl?: string | null
  deployedVersion?: string | null
  reachable: boolean
  upToDate: boolean
  launcherVisible: boolean
}

/** Propagation snapshot: the manager's built version plus each stack's currently-served version. */
export interface LauncherPropagationDto {
  builtVersion?: string | null
  stacks: LauncherStackVersionDto[]
}

export interface LauncherProfileConfigDto {
  stackId: string
  visible: boolean
  displayName: string
  description: string
  sortOrder: number
  realmlistHostOverride: string
  effectiveRealmlistHost: string
  realmlistPort: number
  /** Informational WoW client version label for this stack (blank inherits the global default). */
  clientVersion: string
  /** Whether this stack uploaded a wallpaper that overrides the global theme's background. */
  hasBackground: boolean
  /** Whether this stack uploaded a logo that overrides the global theme's logo. */
  hasLogo: boolean
}
