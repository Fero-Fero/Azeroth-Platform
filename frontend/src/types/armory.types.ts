// Armory asset bundle types (shared model-viewer dataset + static web assets).

export interface ArmoryAssetsInfoDto {
  dataUploaded: boolean
  staticUploaded: boolean
  dataSize: number
  staticSize: number
  dataFileCount: number
  staticFileCount: number
  dataFolders: string[]
  staticRebuildPending: boolean
  /** Model-viewer dataset lives on the stack armory-assets Docker volume. */
  dataOnStackVolume: boolean
  /** Static web bundle lives on the stack armory-static Docker volume. */
  staticOnStackVolume: boolean
  /** .dbc binaries in the stack client-data volume (server DBC source). */
  serverDbcFileCount: number
  /** Custom favicon uploaded for the armory site. */
  faviconUploaded: boolean
}

export interface ArmoryReleaseDownloadResultDto {
  info: ArmoryAssetsInfoDto
  releaseTag: string
  downloadedAssets: string[]
  missingAssets: string[]
}

export type ArmoryStyleTemplate = 'Classic' | 'Tbc' | 'Wotlk' | 'Custom'

export interface ArmoryStylingDto {
  template: ArmoryStyleTemplate
  advancedEnabled: boolean
  primaryColor: string
  secondaryColor: string
  accentColor: string
  backgroundColor: string
  surfaceColor: string
  panelColor: string
  borderColor: string
  navbarColor: string
  linkColor: string
  headingColor: string
  mutedTextColor: string
  inputColor: string
  buttonTextColor: string
  textColor: string
  wallpaperUrl?: string | null
}

export type ArmoryLayoutMode = 'Template' | 'Custom'

export type ArmoryLayoutTemplateId =
  | 'Default'
  | 'NewsFocus'
  | 'CharactersFocus'
  | 'Dashboard'
  | 'WowheadProfile'
  | 'AowowDense'
  | 'IcyVeinsHero'
  | 'WowheadTabs'
  | 'Custom'

export type ArmoryWidgetType =
  | 'PageTitle'
  | 'News'
  | 'RecentCharacters'
  | 'CharacterSearch'
  | 'Spacer'
  | 'CharacterHeader'
  | 'CharacterModelViewer'
  | 'CharacterStats'
  | 'CharacterOverviewCards'
  | 'CharacterSubnav'
  | 'ConnectCta'
  | 'NewsFeed'
  | 'GuildHeader'
  | 'GuildRoster'
  | 'TopLogsTable'
  | 'MapCanvas'

export type ArmoryPageId =
  | 'home'
  | 'connect'
  | 'news-list'
  | 'character'
  | 'character-talents'
  | 'character-skills'
  | 'character-achievements'
  | 'character-progression'
  | 'character-logs'
  | 'guild'
  | 'top-logs'
  | 'map'

export type WidgetShadowPreset = 'None' | 'Sm' | 'Md' | 'Lg' | 'Theme'

export interface WidgetChromeDto {
  borderEnabled?: boolean | null
  borderColor?: string | null
  borderWidth?: number | null
  borderRadius?: number | null
  backgroundColor?: string | null
  padding?: number | null
  shadow?: WidgetShadowPreset | null
  titleColor?: string | null
}

export interface ArmoryLayoutGridDto {
  columns: number
  rowHeight: number
  gap: number
}

export interface ArmoryLayoutWidgetDto {
  id: string
  type: ArmoryWidgetType
  x: number
  y: number
  w: number
  h: number
  visible?: boolean
  settings?: Record<string, unknown> | null
  chrome?: WidgetChromeDto | null
}

export interface ArmoryPageLayoutDto {
  mode: ArmoryLayoutMode
  templateId: string
  grid: ArmoryLayoutGridDto
  widgets: ArmoryLayoutWidgetDto[]
}

export interface ArmoryLayoutDto {
  version: number
  navbar?: ArmoryNavbarDto
  pages: Partial<Record<ArmoryPageId, ArmoryPageLayoutDto>>
}

export type ArmoryNavbarLinkKind = 'Home' | 'TopLogs' | 'Map' | 'Connect' | 'News' | 'Custom'

export interface ArmoryNavbarLinkDto {
  id: string
  kind: ArmoryNavbarLinkKind
  visible?: boolean
  label?: string | null
  href?: string | null
  openInNewTab?: boolean
}

export interface ArmoryNavbarDto {
  showSearch?: boolean
  searchPlaceholder?: string
  links: ArmoryNavbarLinkDto[]
}
