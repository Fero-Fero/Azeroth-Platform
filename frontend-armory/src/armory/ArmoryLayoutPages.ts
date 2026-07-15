import type { ArmoryLayoutWidget, ArmoryNavbarConfig } from "./ArmoryLayout";

export interface ArmoryPageLayoutConfig {
	mode?: string;
	templateId?: string;
	grid: { columns: number; rowHeight: number; gap: number };
	widgets: ArmoryLayoutWidget[];
}

export interface ArmorySiteLayoutConfig {
	version: number;
	navbar?: ArmoryNavbarConfig;
	pages: Record<string, ArmoryPageLayoutConfig>;
	grid?: { columns: number; rowHeight: number; gap: number };
	widgets?: ArmoryLayoutWidget[];
	templateId?: string;
}

export const ALL_ARMORY_PAGE_IDS = [
	"home",
	"connect",
	"news-list",
	"character",
	"character-talents",
	"character-skills",
	"character-achievements",
	"character-progression",
	"character-logs",
	"guild",
	"top-logs",
	"map",
] as const;

export type ArmoryPageId = (typeof ALL_ARMORY_PAGE_IDS)[number];

const DEFAULT_NAVBAR: ArmoryNavbarConfig = {
	showSearch: true,
	searchPlaceholder: "Search character...",
	links: [
		{ id: "nav-home", kind: "Home", visible: true },
		{ id: "nav-top-logs", kind: "TopLogs", visible: true },
		{ id: "nav-map", kind: "Map", visible: true },
		{ id: "nav-connect", kind: "Connect", visible: true },
	],
};

function w(
	id: string,
	type: string,
	x: number,
	y: number,
	width: number,
	height: number,
	settings?: Record<string, unknown>,
): ArmoryLayoutWidget {
	return { id, type, x, y, w: width, h: height, visible: true, settings };
}

function page(templateId: string, widgets: ArmoryLayoutWidget[]): ArmoryPageLayoutConfig {
	return { mode: "Template", templateId, grid: { columns: 12, rowHeight: 48, gap: 12 }, widgets };
}

function resolvePageTemplateId(pageId: string, templateId: string): string {
	if (pageId === "home" && templateId === "SingleColumn") {
		return "Default";
	}
	return templateId;
}

export function buildPageTemplate(pageId: string, templateId: string): ArmoryPageLayoutConfig {
	switch (`${pageId}:${resolvePageTemplateId(pageId, templateId)}`) {
		case "home:NewsFocus":
			return page("NewsFocus", [
				w("tpl-pt", "PageTitle", 0, 0, 12, 1),
				w("tpl-news", "News", 0, 1, 12, 5, { limit: 3, title: "Latest News", showViewAll: true }),
				w("tpl-recent", "RecentCharacters", 0, 6, 6, 3, { limit: 5, title: "Recently Active" }),
				w("tpl-search", "CharacterSearch", 6, 6, 6, 5, { pageLength: 50 }),
			]);
		case "home:CharactersFocus":
			return page("CharactersFocus", [
				w("tpl-pt", "PageTitle", 0, 0, 12, 1),
				w("tpl-recent", "RecentCharacters", 0, 1, 12, 3, { limit: 5, title: "Recently Active" }),
				w("tpl-news", "News", 0, 4, 6, 4, { limit: 3, title: "Latest News", showViewAll: true }),
				w("tpl-search", "CharacterSearch", 6, 4, 6, 5, { pageLength: 50 }),
			]);
		case "home:Dashboard":
			return page("Dashboard", [
				w("tpl-pt", "PageTitle", 0, 0, 12, 1),
				w("tpl-news", "News", 0, 1, 6, 4, { limit: 3, title: "Latest News", showViewAll: true }),
				w("tpl-recent", "RecentCharacters", 6, 1, 6, 4, { limit: 5, title: "Recently Active" }),
				w("tpl-search", "CharacterSearch", 0, 5, 12, 5, { pageLength: 50 }),
			]);
		case "character:Default":
			return page("Default", [
				w("tpl-hdr", "CharacterHeader", 0, 0, 12, 2),
				w("tpl-model", "CharacterModelViewer", 0, 2, 8, 8),
				w("tpl-stats", "CharacterStats", 8, 2, 4, 11),
				w("tpl-cards", "CharacterOverviewCards", 0, 11, 8, 3),
			]);
		case "character:WowheadProfile":
			return page("WowheadProfile", [
				w("tpl-hdr", "CharacterHeader", 0, 0, 12, 2),
				w("tpl-model", "CharacterModelViewer", 0, 2, 12, 6),
				w("tpl-stats", "CharacterStats", 0, 8, 12, 4),
				w("tpl-cards", "CharacterOverviewCards", 0, 12, 12, 3),
			]);
		case "character:AowowDense":
			return page("AowowDense", [
				w("tpl-hdr", "CharacterHeader", 0, 0, 12, 2),
				w("tpl-model", "CharacterModelViewer", 0, 2, 12, 6),
				w("tpl-stats", "CharacterStats", 0, 8, 6, 4),
				w("tpl-cards", "CharacterOverviewCards", 6, 8, 6, 4),
			]);
		case "connect:IcyVeinsHero":
			return page("IcyVeinsHero", [w("tpl-cta", "ConnectCta", 0, 0, 12, 6)]);
		case "connect:Default":
			return page("Default", [
				w("tpl-pt", "PageTitle", 0, 0, 12, 1),
				w("tpl-cta", "ConnectCta", 0, 1, 12, 4),
			]);
		case "news-list:IcyVeinsHero":
		case "news-list:Default":
			return page(templateId, [
				w("tpl-pt", "PageTitle", 0, 0, 12, 1),
				w("tpl-feed", "NewsFeed", 0, 1, 12, templateId === "IcyVeinsHero" ? 10 : 8, {
					limit: 12,
					title: "News",
					showViewAll: false,
				}),
			]);
		case "guild:Default":
		case "guild:AowowDense":
			return page(templateId, [
				w("tpl-hdr", "GuildHeader", 0, 0, 12, 2),
				w("tpl-roster", "GuildRoster", 0, 2, 12, 8),
			]);
		case "top-logs:Default":
		case "top-logs:AowowDense":
			return page(templateId, [
				w("tpl-pt", "PageTitle", 0, 0, 12, 1),
				w("tpl-logs", "TopLogsTable", 0, 1, 12, 9),
			]);
		case "map:Default":
			return page("Default", [w("tpl-map", "MapCanvas", 0, 0, 12, 12)]);
		default:
			if (pageId.startsWith("character-")) {
				return page("WowheadTabs", [w("tpl-subnav", "CharacterSubnav", 0, 0, 12, 2)]);
			}
			return page("Default", [
				w("tpl-pt", "PageTitle", 0, 0, 12, 1),
				w("tpl-news", "News", 0, 1, 12, 4, { limit: 3, title: "Latest News", showViewAll: true }),
				w("tpl-recent", "RecentCharacters", 0, 5, 12, 3, { limit: 5, title: "Recently Active" }),
				w("tpl-search", "CharacterSearch", 0, 8, 12, 5, { pageLength: 50 }),
			]);
	}
}

export function buildDefaultSiteLayout(): ArmorySiteLayoutConfig {
	const pages = Object.fromEntries(
		ALL_ARMORY_PAGE_IDS.map((id) => [id, buildPageTemplate(id, "Default")]),
	) as Record<string, ArmoryPageLayoutConfig>;

	return { version: 2, navbar: DEFAULT_NAVBAR, pages };
}

export function migrateLayoutToV2(site: ArmorySiteLayoutConfig): ArmorySiteLayoutConfig {
	if (site.version >= 2 && site.pages && Object.keys(site.pages).length > 0) {
		return site;
	}

	if (site.widgets?.length) {
		const homeWidgets = site.widgets.filter((widget) => widget.type !== "RealmSelector");
		const home = page(resolvePageTemplateId("home", site.templateId ?? "Default"), homeWidgets);
		const defaults = buildDefaultSiteLayout();
		return {
			version: 2,
			navbar: site.navbar ?? defaults.navbar,
			pages: { ...defaults.pages, home },
		};
	}

	return buildDefaultSiteLayout();
}

export function getPageLayout(site: ArmorySiteLayoutConfig, pageId: string): ArmoryPageLayoutConfig {
	return site.pages[pageId] ?? buildPageTemplate(pageId, "Default");
}

export function pageIdFromCharacterRoute(subpage?: string): string {
	if (!subpage) {
		return "character";
	}
	return `character-${subpage}`;
}

/** Maps character controller subpage keys to layout page ids. */
export function characterLayoutPageId(page: string): string {
	switch (page) {
		case "overview":
			return "character";
		case "records":
			return "character-logs";
		default:
			return pageIdFromCharacterRoute(page);
	}
}
