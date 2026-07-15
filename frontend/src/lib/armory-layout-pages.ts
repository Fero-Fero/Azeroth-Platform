import type {
  ArmoryLayoutDto,
  ArmoryLayoutWidgetDto,
  ArmoryNavbarDto,
  ArmoryPageId,
  ArmoryPageLayoutDto,
  ArmoryWidgetType,
} from '@/types/armory.types'

export const DEFAULT_NAVBAR: ArmoryNavbarDto = {
  showSearch: true,
  searchPlaceholder: 'Search character...',
  links: [
    { id: 'nav-home', kind: 'Home', visible: true },
    { id: 'nav-top-logs', kind: 'TopLogs', visible: true },
    { id: 'nav-map', kind: 'Map', visible: true },
    { id: 'nav-connect', kind: 'Connect', visible: true },
  ],
}

export const ARMORY_PAGE_GROUPS: { label: string; pages: { id: ArmoryPageId; label: string }[] }[] = [
  {
    label: 'Site',
    pages: [
      { id: 'home', label: 'Home' },
      { id: 'connect', label: 'Connect' },
      { id: 'news-list', label: 'News list' },
    ],
  },
  {
    label: 'Character',
    pages: [
      { id: 'character', label: 'Overview' },
      { id: 'character-talents', label: 'Talents' },
      { id: 'character-skills', label: 'Skills' },
      { id: 'character-achievements', label: 'Achievements' },
      { id: 'character-progression', label: 'Progression' },
      { id: 'character-logs', label: 'Logs' },
    ],
  },
  {
    label: 'Community',
    pages: [
      { id: 'guild', label: 'Guild' },
      { id: 'top-logs', label: 'Top logs' },
    ],
  },
  {
    label: 'Other',
    pages: [{ id: 'map', label: 'Map' }],
  },
]

export const ALL_ARMORY_PAGE_IDS: ArmoryPageId[] = ARMORY_PAGE_GROUPS.flatMap((g) => g.pages.map((p) => p.id))

export const PAGE_TEMPLATE_COPY: Record<
  ArmoryPageId,
  { id: string; label: string; description: string; inspiredBy?: string }[]
> = {
  home: [
    { id: 'Default', label: 'Classic stack', description: 'News, recently active, then search.', inspiredBy: 'Classic armory' },
    { id: 'NewsFocus', label: 'News focus', description: 'Large news with search beside recent.', inspiredBy: 'Icy Veins hero' },
    { id: 'CharactersFocus', label: 'Characters focus', description: 'Recently active at the top.', inspiredBy: 'AoWoW dense' },
    { id: 'Dashboard', label: 'Dashboard', description: 'News and recent side by side.', inspiredBy: 'Wowhead grid' },
  ],
  character: [
    { id: 'Default', label: 'Classic stack', description: 'Model viewer left, stats on the right, overview cards in a 2-column grid below the model.', inspiredBy: 'Classic' },
    { id: 'WowheadProfile', label: 'Wowhead profile', description: 'Full-width stacked model, stats, and cards.', inspiredBy: 'Wowhead' },
    { id: 'AowowDense', label: 'AoWoW dense', description: 'Compact single-column character view.', inspiredBy: 'AoWoW' },
  ],
  connect: [
    { id: 'Default', label: 'Classic stack', description: 'Title and connect CTA.', inspiredBy: 'Classic' },
    { id: 'IcyVeinsHero', label: 'Icy Veins hero', description: 'Full-width connect banner.', inspiredBy: 'Icy Veins' },
  ],
  'news-list': [
    { id: 'Default', label: 'Classic stack', description: 'Title and news feed.', inspiredBy: 'Classic' },
    { id: 'IcyVeinsHero', label: 'Icy Veins hero', description: 'Large news listing area.', inspiredBy: 'Icy Veins' },
  ],
  guild: [
    { id: 'Default', label: 'Classic stack', description: 'Header and member table.', inspiredBy: 'Classic' },
    { id: 'AowowDense', label: 'AoWoW dense', description: 'Compact guild layout.', inspiredBy: 'AoWoW' },
  ],
  'top-logs': [
    { id: 'Default', label: 'Classic stack', description: 'Title and leaderboard table.', inspiredBy: 'Classic' },
    { id: 'AowowDense', label: 'AoWoW dense', description: 'Compact filters + table.', inspiredBy: 'AoWoW' },
  ],
  map: [{ id: 'Default', label: 'Classic stack', description: 'Edge-to-edge world map.', inspiredBy: 'Classic' }],
  'character-talents': [{ id: 'Default', label: 'Classic stack', description: 'Subnav + talent content.', inspiredBy: 'Wowhead' }],
  'character-skills': [{ id: 'Default', label: 'Classic stack', description: 'Subnav + skills content.', inspiredBy: 'Wowhead' }],
  'character-achievements': [{ id: 'Default', label: 'Classic stack', description: 'Subnav + achievements.', inspiredBy: 'Wowhead' }],
  'character-progression': [{ id: 'Default', label: 'Classic stack', description: 'Subnav + progression.', inspiredBy: 'Wowhead' }],
  'character-logs': [{ id: 'Default', label: 'Classic stack', description: 'Subnav + logs table.', inspiredBy: 'Wowhead' }],
}

const CLASSIC_STACK_TEMPLATE_ID = 'Default'

/** Template picker options with Classic stack (Default) always first. */
export function getPageTemplates(pageId: ArmoryPageId) {
  const templates = PAGE_TEMPLATE_COPY[pageId] ?? PAGE_TEMPLATE_COPY.home
  return [...templates].sort((a, b) => {
    if (a.id === CLASSIC_STACK_TEMPLATE_ID) return -1
    if (b.id === CLASSIC_STACK_TEMPLATE_ID) return 1
    return 0
  })
}

/** Resolves the template id shown in the editor (Classic stack when unset, customized, or unknown). */
export function resolveEditorTemplateId(page: ArmoryPageLayoutDto, pageId: ArmoryPageId): string {
  const templates = getPageTemplates(pageId)
  if (page.mode === 'Custom' || page.templateId === 'Custom') {
    return CLASSIC_STACK_TEMPLATE_ID
  }
  if (templates.some((template) => template.id === page.templateId)) {
    return page.templateId
  }
  return CLASSIC_STACK_TEMPLATE_ID
}

export const PAGE_WIDGET_TYPES: Record<ArmoryPageId, ArmoryWidgetType[]> = {
  home: ['PageTitle', 'News', 'RecentCharacters', 'CharacterSearch', 'Spacer'],
  connect: ['PageTitle', 'ConnectCta', 'Spacer'],
  'news-list': ['PageTitle', 'NewsFeed', 'Spacer'],
  character: ['CharacterHeader', 'CharacterModelViewer', 'CharacterStats', 'CharacterOverviewCards', 'Spacer'],
  'character-talents': ['CharacterSubnav'],
  'character-skills': ['CharacterSubnav'],
  'character-achievements': ['CharacterSubnav'],
  'character-progression': ['CharacterSubnav'],
  'character-logs': ['CharacterSubnav'],
  guild: ['GuildHeader', 'GuildRoster', 'Spacer'],
  'top-logs': ['PageTitle', 'TopLogsTable', 'Spacer'],
  map: ['MapCanvas'],
}

function w(
  id: string,
  type: ArmoryWidgetType,
  x: number,
  y: number,
  width: number,
  height: number,
  settings?: Record<string, unknown>,
) {
  return { id, type, x, y, w: width, h: height, visible: true, settings: settings ?? null, chrome: null }
}

function page(templateId: string, widgets: ArmoryLayoutWidgetDto[]): ArmoryPageLayoutDto {
  return { mode: 'Template', templateId, grid: { columns: 12, rowHeight: 48, gap: 12 }, widgets }
}

function resolvePageTemplateId(pageId: ArmoryPageId, templateId: string): string {
  if (pageId === 'home' && templateId === 'SingleColumn') {
    return 'Default'
  }
  return templateId
}

export function buildPageTemplate(pageId: ArmoryPageId, templateId: string): ArmoryPageLayoutDto {
  const resolvedId = resolvePageTemplateId(pageId, templateId)
  switch (`${pageId}:${resolvedId}`) {
    case 'home:NewsFocus':
      return page('NewsFocus', [
        w('tpl-pt', 'PageTitle', 0, 0, 12, 1),
        w('tpl-news', 'News', 0, 1, 12, 5, { limit: 3, title: 'Latest News', showViewAll: true }),
        w('tpl-recent', 'RecentCharacters', 0, 6, 6, 3, { limit: 5, title: 'Recently Active' }),
        w('tpl-search', 'CharacterSearch', 6, 6, 6, 5, { pageLength: 50 }),
      ])
    case 'home:CharactersFocus':
      return page('CharactersFocus', [
        w('tpl-pt', 'PageTitle', 0, 0, 12, 1),
        w('tpl-recent', 'RecentCharacters', 0, 1, 12, 3, { limit: 5, title: 'Recently Active' }),
        w('tpl-news', 'News', 0, 4, 6, 4, { limit: 3, title: 'Latest News', showViewAll: true }),
        w('tpl-search', 'CharacterSearch', 6, 4, 6, 5, { pageLength: 50 }),
      ])
    case 'home:Dashboard':
      return page('Dashboard', [
        w('tpl-pt', 'PageTitle', 0, 0, 12, 1),
        w('tpl-news', 'News', 0, 1, 6, 4, { limit: 3, title: 'Latest News', showViewAll: true }),
        w('tpl-recent', 'RecentCharacters', 6, 1, 6, 4, { limit: 5, title: 'Recently Active' }),
        w('tpl-search', 'CharacterSearch', 0, 5, 12, 5, { pageLength: 50 }),
      ])
    case 'character:Default':
      return page('Default', [
        w('tpl-hdr', 'CharacterHeader', 0, 0, 12, 2),
        w('tpl-model', 'CharacterModelViewer', 0, 2, 8, 8),
        w('tpl-stats', 'CharacterStats', 8, 2, 4, 11),
        w('tpl-cards', 'CharacterOverviewCards', 0, 11, 8, 3),
      ])
    case 'character:WowheadProfile':
      return page('WowheadProfile', [
        w('tpl-hdr', 'CharacterHeader', 0, 0, 12, 2),
        w('tpl-model', 'CharacterModelViewer', 0, 2, 12, 6),
        w('tpl-stats', 'CharacterStats', 0, 8, 12, 4),
        w('tpl-cards', 'CharacterOverviewCards', 0, 12, 12, 3),
      ])
    case 'character:AowowDense':
      return page('AowowDense', [
        w('tpl-hdr', 'CharacterHeader', 0, 0, 12, 2),
        w('tpl-model', 'CharacterModelViewer', 0, 2, 12, 6),
        w('tpl-stats', 'CharacterStats', 0, 8, 6, 4),
        w('tpl-cards', 'CharacterOverviewCards', 6, 8, 6, 4),
      ])
    case 'connect:IcyVeinsHero':
      return page('IcyVeinsHero', [w('tpl-cta', 'ConnectCta', 0, 0, 12, 6)])
    case 'connect:Default':
      return page('Default', [
        w('tpl-pt', 'PageTitle', 0, 0, 12, 1),
        w('tpl-cta', 'ConnectCta', 0, 1, 12, 4),
      ])
    case 'news-list:IcyVeinsHero':
    case 'news-list:Default':
      return page(templateId, [
        w('tpl-pt', 'PageTitle', 0, 0, 12, 1),
        w('tpl-feed', 'NewsFeed', 0, 1, 12, templateId === 'IcyVeinsHero' ? 10 : 8, {
          limit: 12,
          title: 'News',
          showViewAll: false,
        }),
      ])
    case 'guild:Default':
    case 'guild:AowowDense':
      return page(templateId, [
        w('tpl-hdr', 'GuildHeader', 0, 0, 12, 2),
        w('tpl-roster', 'GuildRoster', 0, 2, 12, 8),
      ])
    case 'top-logs:Default':
    case 'top-logs:AowowDense':
      return page(templateId, [
        w('tpl-pt', 'PageTitle', 0, 0, 12, 1),
        w('tpl-logs', 'TopLogsTable', 0, 1, 12, 9),
      ])
    case 'map:Default':
      return page('Default', [w('tpl-map', 'MapCanvas', 0, 0, 12, 12)])
    default:
      if (pageId.startsWith('character-')) {
        return page('WowheadTabs', [w('tpl-subnav', 'CharacterSubnav', 0, 0, 12, 2)])
      }
      return page('Default', [
        w('tpl-pt', 'PageTitle', 0, 0, 12, 1),
        w('tpl-news', 'News', 0, 1, 12, 4, { limit: 3, title: 'Latest News', showViewAll: true }),
        w('tpl-recent', 'RecentCharacters', 0, 5, 12, 3, { limit: 5, title: 'Recently Active' }),
        w('tpl-search', 'CharacterSearch', 0, 8, 12, 5, { pageLength: 50 }),
      ])
  }
}

export function buildDefaultSiteLayout(): ArmoryLayoutDto {
  const pages = Object.fromEntries(
    ALL_ARMORY_PAGE_IDS.map((id) => [id, buildPageTemplate(id, 'Default')]),
  ) as ArmoryLayoutDto['pages']

  return { version: 2, navbar: DEFAULT_NAVBAR, pages }
}

export function migrateLayoutToV2(layout: ArmoryLayoutDto): ArmoryLayoutDto {
  if (layout.version >= 2 && layout.pages && Object.keys(layout.pages).length > 0) {
    return layout
  }

  if (layout.widgets?.length) {
    const homeWidgets = layout.widgets.map((w) => ({
        ...w,
        visible: w.visible ?? true,
        settings: w.settings ?? null,
        chrome: w.chrome ?? null,
      }))
    const home = page(resolvePageTemplateId('home', layout.templateId ?? 'Default'), homeWidgets)
    return {
      version: 2,
      navbar: layout.navbar,
      pages: { home, ...buildDefaultSiteLayout().pages },
    }
  }

  return buildDefaultSiteLayout()
}

export function getPageLayout(layout: ArmoryLayoutDto, pageId: ArmoryPageId): ArmoryPageLayoutDto {
  const migrated = migrateLayoutToV2(layout)
  return migrated.pages?.[pageId] ?? buildPageTemplate(pageId, 'Default')
}

export function setPageLayout(
  layout: ArmoryLayoutDto,
  pageId: ArmoryPageId,
  page: ArmoryPageLayoutDto,
): ArmoryLayoutDto {
  const migrated = migrateLayoutToV2(layout)
  return {
    ...migrated,
    pages: { ...migrated.pages, [pageId]: page },
  }
}

export function isPageCustomized(layout: ArmoryLayoutDto, pageId: ArmoryPageId): boolean {
  const page = getPageLayout(layout, pageId)
  return page.mode === 'Custom'
}
