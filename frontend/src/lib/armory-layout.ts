import type { CSSProperties } from 'react'
import type {
  ArmoryLayoutDto,
  ArmoryLayoutTemplateId,
  ArmoryLayoutWidgetDto,
  ArmoryNavbarDto,
  ArmoryNavbarLinkDto,
  ArmoryNavbarLinkKind,
  ArmoryPageId,
  ArmoryPageLayoutDto,
  ArmoryWidgetType,
  WidgetChromeDto,
  WidgetShadowPreset,
} from '@/types/armory.types'
import {
  ALL_ARMORY_PAGE_IDS,
  ARMORY_PAGE_GROUPS,
  DEFAULT_NAVBAR,
  PAGE_TEMPLATE_COPY,
  PAGE_WIDGET_TYPES,
  buildDefaultSiteLayout,
  buildPageTemplate,
  getPageLayout,
  getPageTemplates,
  isPageCustomized,
  ensureSiteLayout,
  resolveEditorTemplateId,
  setPageLayout,
} from './armory-layout-pages'

export {
  ALL_ARMORY_PAGE_IDS,
  ARMORY_PAGE_GROUPS,
  DEFAULT_NAVBAR,
  PAGE_TEMPLATE_COPY,
  PAGE_WIDGET_TYPES,
  buildDefaultSiteLayout,
  buildPageTemplate,
  getPageLayout,
  getPageTemplates,
  isPageCustomized,
  ensureSiteLayout,
  resolveEditorTemplateId,
  setPageLayout,
}

export const WIDGET_CATALOG: Record<
  ArmoryWidgetType,
  { label: string; description: string; minW: number; minH: number; defaultW: number; defaultH: number }
> = {
  PageTitle: {
    label: 'Page Title',
    description: 'Armory heading with your site name',
    minW: 3,
    minH: 1,
    defaultW: 12,
    defaultH: 1,
  },
  News: {
    label: 'Latest News',
    description: 'News cards from the launcher portal',
    minW: 3,
    minH: 2,
    defaultW: 12,
    defaultH: 4,
  },
  RecentCharacters: {
    label: 'Recently Active',
    description: 'Characters who logged out most recently',
    minW: 3,
    minH: 2,
    defaultW: 12,
    defaultH: 3,
  },
  CharacterSearch: {
    label: 'Character Search',
    description: 'Searchable character list with realm filter (multi-realm)',
    minW: 4,
    minH: 3,
    defaultW: 12,
    defaultH: 5,
  },
  Spacer: {
    label: 'Spacer',
    description: 'Empty vertical space',
    minW: 1,
    minH: 1,
    defaultW: 12,
    defaultH: 1,
  },
  CharacterHeader: {
    label: 'Character Header',
    description: 'Name, level, guild, and item level',
    minW: 6,
    minH: 2,
    defaultW: 12,
    defaultH: 2,
  },
  CharacterModelViewer: {
    label: '3D Model',
    description: 'Character model and equipment viewer',
    minW: 4,
    minH: 4,
    defaultW: 8,
    defaultH: 8,
  },
  CharacterStats: {
    label: 'Character Stats',
    description: 'Primary and secondary stat panels',
    minW: 3,
    minH: 3,
    defaultW: 4,
    defaultH: 8,
  },
  CharacterOverviewCards: {
    label: 'Overview Cards',
    description: '2-column grid: talents, professions, honorable kills, mounts, achievements',
    minW: 4,
    minH: 2,
    defaultW: 8,
    defaultH: 3,
  },
  CharacterSubnav: {
    label: 'Character Tabs',
    description: 'Sub-page navigation strip',
    minW: 6,
    minH: 2,
    defaultW: 12,
    defaultH: 2,
  },
  ConnectCta: {
    label: 'Connect CTA',
    description: 'Launcher download and server info',
    minW: 4,
    minH: 2,
    defaultW: 12,
    defaultH: 4,
  },
  NewsFeed: {
    label: 'News Feed',
    description: 'Full news listing',
    minW: 4,
    minH: 3,
    defaultW: 12,
    defaultH: 8,
  },
  GuildHeader: {
    label: 'Guild Header',
    description: 'Guild name, realm, and summary',
    minW: 4,
    minH: 1,
    defaultW: 12,
    defaultH: 2,
  },
  GuildRoster: {
    label: 'Guild Roster',
    description: 'Member list table',
    minW: 4,
    minH: 4,
    defaultW: 12,
    defaultH: 8,
  },
  TopLogsTable: {
    label: 'Top Logs',
    description: 'Leaderboard filters and table',
    minW: 4,
    minH: 4,
    defaultW: 12,
    defaultH: 9,
  },
  MapCanvas: {
    label: 'World Map',
    description: 'Interactive world map',
    minW: 6,
    minH: 6,
    defaultW: 12,
    defaultH: 12,
  },
}

export const NAVBAR_LINK_KINDS: Record<
  ArmoryNavbarLinkKind,
  { label: string; description: string; singleton?: boolean }
> = {
  Home: { label: 'Home', description: 'Site home - label defaults to your stack name', singleton: true },
  TopLogs: { label: 'Top Logs', description: 'Leaderboard (shown when logs tracker is installed)', singleton: true },
  Map: { label: 'Map', description: 'World map page', singleton: true },
  Connect: { label: 'Connect', description: 'Launcher download / connect page', singleton: true },
  News: { label: 'News', description: 'News listing page', singleton: true },
  Custom: { label: 'Custom link', description: 'Any URL or path' },
}

export function newNavbarLinkId() {
  return `nav-${crypto.randomUUID().replace(/-/g, '').slice(0, 12)}`
}

export function normalizeNavbar(navbar?: ArmoryNavbarDto | null): ArmoryNavbarDto {
  if (!navbar?.links?.length) {
    return cloneNavbar(DEFAULT_NAVBAR)
  }

  const links = navbar.links
    .filter((link) => link.kind !== 'Custom' || (link.label?.trim() && link.href?.trim()))
    .map((link) => ({
      ...link,
      id: link.id?.trim() || newNavbarLinkId(),
    }))

  if (!links.some((l) => l.kind === 'Home')) {
    links.unshift({ id: 'nav-home', kind: 'Home', visible: true })
  }

  return {
    showSearch: navbar.showSearch !== false,
    searchPlaceholder: navbar.searchPlaceholder?.trim() || 'Search character...',
    links,
  }
}

export function cloneNavbar(navbar: ArmoryNavbarDto): ArmoryNavbarDto {
  return JSON.parse(JSON.stringify(navbar)) as ArmoryNavbarDto
}

export function resolveNavbarLinksForPreview(
  navbar: ArmoryNavbarDto | undefined,
  siteName: string,
  options?: { topLogsEnabled?: boolean; worldMapEnabled?: boolean },
): { id: string; label: string; href: string; openInNewTab: boolean }[] {
  const topLogsEnabled = options?.topLogsEnabled !== false
  const worldMapEnabled = options?.worldMapEnabled !== false
  const config = normalizeNavbar(navbar)
  return config.links
    .filter((link) => link.visible !== false)
    .filter((link) => (link.kind !== 'TopLogs' || topLogsEnabled) && (link.kind !== 'Map' || worldMapEnabled))
    .map((link) => resolveNavbarLink(link, siteName))
    .filter((link): link is NonNullable<typeof link> => link !== null)
}

function resolveNavbarLink(link: ArmoryNavbarLinkDto, siteName: string) {
  switch (link.kind) {
    case 'Home':
      return {
        id: link.id,
        label: link.label?.trim() || siteName || 'Azeroth',
        href: '/',
        openInNewTab: !!link.openInNewTab,
      }
    case 'TopLogs':
      return {
        id: link.id,
        label: link.label?.trim() || 'Top Logs',
        href: '/top-logs',
        openInNewTab: !!link.openInNewTab,
      }
    case 'Map':
      return {
        id: link.id,
        label: link.label?.trim() || 'Azeroth',
        href: '/map',
        openInNewTab: !!link.openInNewTab,
      }
    case 'Connect':
      return {
        id: link.id,
        label: link.label?.trim() || 'Connect',
        href: '/connect',
        openInNewTab: !!link.openInNewTab,
      }
    case 'News':
      return {
        id: link.id,
        label: link.label?.trim() || 'News',
        href: '/news',
        openInNewTab: !!link.openInNewTab,
      }
    case 'Custom':
      if (!link.label?.trim() || !link.href?.trim()) return null
      return {
        id: link.id,
        label: link.label.trim(),
        href: link.href.trim(),
        openInNewTab: !!link.openInNewTab,
      }
    default:
      return null
  }
}

export function createNavbarLink(kind: ArmoryNavbarLinkKind): ArmoryNavbarLinkDto {
  if (kind === 'Custom') {
    return { id: newNavbarLinkId(), kind, visible: true, label: 'New link', href: '/news', openInNewTab: false }
  }
  return { id: newNavbarLinkId(), kind, visible: true }
}

export function reorderNavbarLinks(
  links: ArmoryNavbarLinkDto[],
  sourceId: string,
  targetId: string,
): ArmoryNavbarLinkDto[] {
  const fromIndex = links.findIndex((link) => link.id === sourceId)
  const toIndex = links.findIndex((link) => link.id === targetId)
  if (fromIndex < 0 || toIndex < 0 || fromIndex === toIndex) {
    return links
  }
  const next = [...links]
  const [moved] = next.splice(fromIndex, 1)
  next.splice(toIndex, 0, moved)
  return next
}

export function navbarLinkDisplayLabel(link: ArmoryNavbarLinkDto, siteName: string): string {
  if (link.label?.trim()) {
    return link.label.trim()
  }
  if (link.kind === 'Home') {
    return siteName || 'Azeroth'
  }
  return NAVBAR_LINK_KINDS[link.kind]?.label ?? link.kind
}

export function cloneLayout(layout: ArmoryLayoutDto): ArmoryLayoutDto {
  const cloned = ensureSiteLayout(JSON.parse(JSON.stringify(layout)) as ArmoryLayoutDto)
  cloned.navbar = normalizeNavbar(cloned.navbar)
  return cloned
}

export function layoutFromTemplate(templateId: Exclude<ArmoryLayoutTemplateId, 'Custom'>): ArmoryLayoutDto {
  const site = buildDefaultSiteLayout()
  site.pages!.home = buildPageTemplate('home', templateId)
  return cloneLayout(site)
}

export function layoutPageFromTemplate(pageId: ArmoryPageId, templateId: string): ArmoryLayoutDto {
  const site = buildDefaultSiteLayout()
  return setPageLayout(site, pageId, buildPageTemplate(pageId, templateId))
}

export function newWidgetId() {
  return `w-${crypto.randomUUID().replace(/-/g, '').slice(0, 12)}`
}

export function createWidget(type: ArmoryWidgetType, x = 0, y = 0): ArmoryLayoutWidgetDto {
  const meta = WIDGET_CATALOG[type]
  const settings: Record<string, unknown> = {}
  if (type === 'News') {
    settings.limit = 3
    settings.title = 'Latest News'
    settings.showViewAll = true
  } else if (type === 'RecentCharacters') {
    settings.limit = 5
    settings.title = 'Recently Active'
  } else if (type === 'CharacterSearch') {
    settings.pageLength = 50
  } else if (type === 'NewsFeed') {
    settings.limit = 12
    settings.title = 'News'
  }

  return {
    id: newWidgetId(),
    type,
    x,
    y,
    w: meta.defaultW,
    h: meta.defaultH,
    visible: true,
    settings: Object.keys(settings).length ? settings : null,
    chrome: null,
  }
}

export function getIntSetting(widget: ArmoryLayoutWidgetDto, key: string, fallback: number): number {
  const value = widget.settings?.[key]
  if (typeof value === 'number') return value
  if (typeof value === 'string') {
    const parsed = Number.parseInt(value, 10)
    return Number.isNaN(parsed) ? fallback : parsed
  }
  return fallback
}

export function getStringSetting(widget: ArmoryLayoutWidgetDto, key: string, fallback: string): string {
  const value = widget.settings?.[key]
  return typeof value === 'string' ? value : fallback
}

export function getBoolSetting(widget: ArmoryLayoutWidgetDto, key: string, fallback: boolean): boolean {
  const value = widget.settings?.[key]
  if (typeof value === 'boolean') return value
  return fallback
}

export function widgetChromeStyle(chrome: WidgetChromeDto | null | undefined): CSSProperties {
  if (!chrome) return {}
  const style: CSSProperties = {}

  if (chrome.borderEnabled === false) {
    style.border = 'none'
    style.boxShadow = 'none'
  } else {
    if (chrome.borderWidth != null && chrome.borderWidth >= 0) {
      style.borderWidth = chrome.borderWidth
      style.borderStyle = 'solid'
      style.borderColor = resolveColor(chrome.borderColor, 'var(--armory-border)')
    } else if (chrome.borderColor) {
      style.borderColor = resolveColor(chrome.borderColor, 'var(--armory-border)')
    }
  }

  if (chrome.borderRadius != null && chrome.borderRadius >= 0) {
    style.borderRadius = chrome.borderRadius
  }
  if (chrome.backgroundColor) {
    style.background = resolveColor(chrome.backgroundColor, 'var(--armory-panel)')
  }
  if (chrome.padding != null && chrome.padding >= 0) {
    style.padding = chrome.padding
  }
  if (chrome.shadow) {
    style.boxShadow = shadowCss(chrome.shadow)
  }
  if (chrome.titleColor) {
    style.color = resolveColor(chrome.titleColor, 'var(--armory-heading)')
  }

  return style
}

function resolveColor(value: string | null | undefined, fallback: string): string {
  if (!value) return fallback
  if (value === 'theme') return fallback
  if (value === 'transparent') return 'transparent'
  return value
}

function shadowCss(preset: WidgetShadowPreset): string {
  switch (preset) {
    case 'None':
      return 'none'
    case 'Sm':
      return '0 1px 3px rgba(0, 0, 0, 0.35)'
    case 'Md':
      return '0 4px 12px rgba(0, 0, 0, 0.45)'
    case 'Lg':
      return '0 12px 32px rgba(0, 0, 0, 0.55)'
    default:
      return '0 2px 6px rgba(0, 0, 0, 0.5)'
  }
}

export function layoutsEqual(a: ArmoryLayoutDto, b: ArmoryLayoutDto): boolean {
  return JSON.stringify(a) === JSON.stringify(b)
}

type GridPlaced = { x: number; y: number; w: number; h: number; visible?: boolean }

function overlaps(x1: number, y1: number, w1: number, h1: number, x2: number, y2: number, w2: number, h2: number) {
  return x1 < x2 + w2 && x1 + w1 > x2 && y1 < y2 + h2 && y1 + h1 > y2
}

function canPlace(x: number, y: number, w: number, h: number, placed: GridPlaced[], columns: number) {
  if (x + w > columns) return false
  for (const other of placed) {
    if (overlaps(x, y, w, h, other.x, other.y, other.w, other.h)) return false
  }
  return true
}

/** Moves visible widgets up to remove empty rows (matches react-grid-layout vertical compaction). */
export function compactWidgetsVertically<T extends GridPlaced>(widgets: T[], columns: number): T[] {
  const result = widgets.map((widget) => ({ ...widget }))
  const placed: T[] = []

  for (const widget of result.filter((w) => w.visible !== false).sort((a, b) => a.y - b.y || a.x - b.x)) {
    let y = 0
    while (!canPlace(widget.x, y, widget.w, widget.h, placed, columns)) {
      y++
    }
    widget.y = y
    placed.push(widget)
  }

  return result
}

export function sortWidgetsForDisplay<T extends { x: number; y: number; visible?: boolean }>(widgets: T[]): T[] {
  return widgets.filter((widget) => widget.visible !== false).sort((a, b) => a.y - b.y || a.x - b.x)
}

export function applyWidgetMinimums(page: ArmoryPageLayoutDto): ArmoryPageLayoutDto {
  return {
    ...page,
    widgets: page.widgets.map((widget) => {
        const meta = WIDGET_CATALOG[widget.type]
        if (!meta) return widget
        return {
          ...widget,
          w: Math.max(widget.w, meta.minW),
          h: Math.max(widget.h, meta.minH),
        }
      }),
  }
}

export function compactPageLayout(page: ArmoryPageLayoutDto): ArmoryPageLayoutDto {
  const filtered = applyWidgetMinimums(page).widgets
  return {
    ...page,
    widgets:
      page.mode === 'Custom'
        ? filtered
        : compactWidgetsVertically(filtered, page.grid.columns),
  }
}

export function compactLayout(layout: ArmoryLayoutDto): ArmoryLayoutDto {
  const ensured = ensureSiteLayout(layout)
  const pages = Object.fromEntries(
    ALL_ARMORY_PAGE_IDS.map((pageId) => [
      pageId,
      compactPageLayout(getPageLayout(ensured, pageId)),
    ]),
  ) as ArmoryLayoutDto['pages']
  return { ...ensured, version: 2, pages }
}

export function gridContentHeight(
  widgets: ArmoryLayoutWidgetDto[],
  rowHeight: number,
  gap: number,
  columns = 12,
): number {
  const compacted = compactWidgetsVertically(widgets, columns)
  const visible = compacted.filter((w) => w.visible !== false)
  if (!visible.length) return rowHeight
  const maxBottom = Math.max(...visible.map((w) => w.y + w.h))
  return maxBottom * rowHeight + Math.max(0, maxBottom - 1) * gap
}
