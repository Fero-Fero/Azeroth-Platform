import * as fs from "fs";
import * as path from "path";

import {
	buildDefaultSiteLayout,
	buildPageTemplate,
	getPageLayout,
	migrateLayoutToV2,
	type ArmoryPageLayoutConfig,
	type ArmorySiteLayoutConfig,
} from "./ArmoryLayoutPages";

export type { ArmoryPageLayoutConfig, ArmorySiteLayoutConfig } from "./ArmoryLayoutPages";

export interface ArmoryLayoutWidget {
	id: string;
	type: string;
	x: number;
	y: number;
	w: number;
	h: number;
	visible?: boolean;
	settings?: Record<string, unknown>;
}

export type ArmoryNavbarLinkKind = "Home" | "TopLogs" | "Map" | "Connect" | "News" | "Custom";

export interface ArmoryNavbarLink {
	id: string;
	kind: ArmoryNavbarLinkKind;
	visible?: boolean;
	label?: string | null;
	href?: string | null;
	openInNewTab?: boolean;
}

export interface ArmoryNavbarConfig {
	showSearch?: boolean;
	searchPlaceholder?: string;
	links: ArmoryNavbarLink[];
}

export interface ResolvedNavbarLink {
	id: string;
	label: string;
	href: string;
	openInNewTab: boolean;
}

export interface ArmoryLayoutConfig {
	version: number;
	grid: { columns: number; rowHeight: number; gap: number };
	widgets: ArmoryLayoutWidget[];
	navbar?: ArmoryNavbarConfig;
}

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

export function loadArmorySiteLayout(): ArmorySiteLayoutConfig {
	const layoutPath = path.join(process.cwd(), "static", "data", "armory-layout.json");
	if (!fs.existsSync(layoutPath)) {
		return compactSiteLayout(buildDefaultSiteLayout());
	}

	try {
		const raw = JSON.parse(fs.readFileSync(layoutPath, "utf8")) as Record<string, unknown>;
		const normalized = normalizeSiteLayoutFromDisk(raw);
		return compactSiteLayout(normalized);
	} catch {
		return compactSiteLayout(buildDefaultSiteLayout());
	}
}

export function loadPageLayout(pageId: string, site?: ArmorySiteLayoutConfig): ArmoryPageLayoutConfig {
	const layout = site ?? loadArmorySiteLayout();
	const page = getPageLayout(layout, pageId);
	return page.mode === "Custom" ? applyWidgetMinimums(page) : compactPageLayout(page);
}

function applyWidgetMinimums(page: ArmoryPageLayoutConfig): ArmoryPageLayoutConfig {
	return {
		...page,
		widgets: page.widgets.map((widget) => {
				const minW = widgetMinWidth(widget.type);
				const minH = widgetMinHeight(widget.type);
				return {
					...widget,
					w: Math.max(widget.w, minW),
					h: Math.max(widget.h, minH),
				};
			}),
	};
}

function widgetMinWidth(type: string): number {
	switch (type) {
		case "CharacterSubnav":
			return 6;
		case "MapCanvas":
			return 6;
		default:
			return 3;
	}
}

function widgetMinHeight(type: string): number {
	switch (type) {
		case "CharacterSubnav":
			return 2;
		case "CharacterHeader":
			return 2;
		case "CharacterModelViewer":
			return 4;
		case "CharacterStats":
			return 3;
		case "CharacterOverviewCards":
			return 2;
		default:
			return 1;
	}
}

/** Backward-compatible home-page layout reader for V1 callers. */
export function loadArmoryLayout(): ArmoryLayoutConfig {
	const site = loadArmorySiteLayout();
	const home = loadPageLayout("home", site);
	return {
		version: site.version,
		grid: home.grid,
		widgets: home.widgets,
		navbar: site.navbar,
	};
}

/** Accepts camelCase (preferred) or legacy PascalCase JSON from older manager builds. */
function normalizeLayoutFromDisk(raw: Record<string, unknown>): ArmoryLayoutConfig {
	const gridRaw = (pick(raw, "grid", "Grid") ?? {}) as Record<string, unknown>;
	const widgetsRaw = pick(raw, "widgets", "Widgets");
	const navbarRaw = pick(raw, "navbar", "Navbar");

	const widgets = Array.isArray(widgetsRaw)
		? widgetsRaw
				.map((entry) => normalizeWidgetFromDisk(entry as Record<string, unknown>))
				.filter((widget): widget is ArmoryLayoutWidget => widget !== null)
		: [];

	return {
		version: readNumber(raw, "version", "Version", 1),
		grid: {
			columns: readNumber(gridRaw, "columns", "Columns", 12),
			rowHeight: readNumber(gridRaw, "rowHeight", "RowHeight", 48),
			gap: readNumber(gridRaw, "gap", "Gap", 12),
		},
		widgets,
		navbar: normalizeNavbarFromDisk(navbarRaw as Record<string, unknown> | undefined),
	};
}

function normalizeSiteLayoutFromDisk(raw: Record<string, unknown>): ArmorySiteLayoutConfig {
	const version = readNumber(raw, "version", "Version", 1);
	const navbar = normalizeNavbarFromDisk(pick(raw, "navbar", "Navbar") as Record<string, unknown> | undefined);
	const pagesRaw = pick(raw, "pages", "Pages");

	if (version >= 2 && pagesRaw && typeof pagesRaw === "object" && !Array.isArray(pagesRaw)) {
		const pages: Record<string, ArmoryPageLayoutConfig> = {};
		for (const [pageId, entry] of Object.entries(pagesRaw as Record<string, unknown>)) {
			pages[pageId] = normalizePageLayoutFromDisk(entry as Record<string, unknown>, pageId);
		}
		return migrateLayoutToV2({ version, navbar, pages });
	}

	const legacy = normalizeLayoutFromDisk(raw);
	return migrateLayoutToV2({
		version: legacy.version,
		navbar: legacy.navbar,
		grid: legacy.grid,
		widgets: legacy.widgets,
		templateId: readString(raw, "templateId", "TemplateId") || undefined,
		pages: {},
	});
}

function normalizePageLayoutFromDisk(raw: Record<string, unknown>, pageId: string): ArmoryPageLayoutConfig {
	const fallback = buildPageTemplate(pageId, "Default");
	const gridRaw = (pick(raw, "grid", "Grid") ?? {}) as Record<string, unknown>;
	const widgetsRaw = pick(raw, "widgets", "Widgets");
	const widgets = Array.isArray(widgetsRaw)
		? widgetsRaw
				.map((entry) => normalizeWidgetFromDisk(entry as Record<string, unknown>))
				.filter((widget): widget is ArmoryLayoutWidget => widget !== null)
		: fallback.widgets;

	return {
		mode: readString(raw, "mode", "Mode") || fallback.mode,
		templateId: readString(raw, "templateId", "TemplateId") || fallback.templateId,
		grid: {
			columns: readNumber(gridRaw, "columns", "Columns", fallback.grid.columns),
			rowHeight: readNumber(gridRaw, "rowHeight", "RowHeight", fallback.grid.rowHeight),
			gap: readNumber(gridRaw, "gap", "Gap", fallback.grid.gap),
		},
		widgets: widgets.length ? widgets : fallback.widgets,
	};
}

function normalizeWidgetFromDisk(raw: Record<string, unknown>): ArmoryLayoutWidget | null {
	const type = readString(raw, "type", "Type");
	if (!type) {
		return null;
	}

	return {
		id: readString(raw, "id", "Id") || `w-${Date.now()}`,
		type,
		x: readNumber(raw, "x", "X", 0),
		y: readNumber(raw, "y", "Y", 0),
		w: readNumber(raw, "w", "W", 4),
		h: readNumber(raw, "h", "H", 2),
		visible: readBool(raw, "visible", "Visible", true),
		settings: readSettings(raw),
	};
}

function normalizeNavbarFromDisk(raw?: Record<string, unknown>): ArmoryNavbarConfig {
	if (!raw) {
		return normalizeNavbar(undefined);
	}

	const linksRaw = pick(raw, "links", "Links");
	const links = Array.isArray(linksRaw)
		? linksRaw.map((entry) => normalizeNavbarLinkFromDisk(entry as Record<string, unknown>))
		: [];

	return normalizeNavbar({
		showSearch: readBool(raw, "showSearch", "ShowSearch", true),
		searchPlaceholder: readString(raw, "searchPlaceholder", "SearchPlaceholder") || undefined,
		links,
	});
}

function normalizeNavbarLinkFromDisk(raw: Record<string, unknown>): ArmoryNavbarLink {
	return {
		id: readString(raw, "id", "Id") || `nav-${Date.now()}`,
		kind: (readString(raw, "kind", "Kind") || "Custom") as ArmoryNavbarLinkKind,
		visible: readBool(raw, "visible", "Visible", true),
		label: readString(raw, "label", "Label") || null,
		href: readString(raw, "href", "Href") || null,
		openInNewTab: readBool(raw, "openInNewTab", "OpenInNewTab", false),
	};
}

function readSettings(raw: Record<string, unknown>): Record<string, unknown> | undefined {
	const settings = pick(raw, "settings", "Settings");
	if (!settings || typeof settings !== "object" || Array.isArray(settings)) {
		return undefined;
	}
	return settings as Record<string, unknown>;
}

function pick(raw: Record<string, unknown>, ...keys: string[]): unknown {
	for (const key of keys) {
		if (raw[key] !== undefined && raw[key] !== null) {
			return raw[key];
		}
	}
	return undefined;
}

function readString(raw: Record<string, unknown>, camel: string, pascal: string): string {
	const value = pick(raw, camel, pascal);
	return typeof value === "string" ? value : "";
}

function readNumber(raw: Record<string, unknown>, camel: string, pascal: string, fallback: number): number {
	const value = pick(raw, camel, pascal);
	if (typeof value === "number" && !Number.isNaN(value)) {
		return value;
	}
	if (typeof value === "string") {
		const parsed = parseInt(value, 10);
		return Number.isNaN(parsed) ? fallback : parsed;
	}
	return fallback;
}

function readBool(raw: Record<string, unknown>, camel: string, pascal: string, fallback: boolean): boolean {
	const value = pick(raw, camel, pascal);
	if (typeof value === "boolean") {
		return value;
	}
	return fallback;
}

function normalizeLayout(layout: ArmoryLayoutConfig): ArmoryLayoutConfig {
	return {
		...layout,
		navbar: normalizeNavbar(layout.navbar),
	};
}

function overlaps(x1: number, y1: number, w1: number, h1: number, x2: number, y2: number, w2: number, h2: number) {
	return x1 < x2 + w2 && x1 + w1 > x2 && y1 < y2 + h2 && y1 + h1 > y2;
}

function canPlace(
	x: number,
	y: number,
	w: number,
	h: number,
	placed: ArmoryLayoutWidget[],
	columns: number,
) {
	if (x + w > columns) {
		return false;
	}
	for (const other of placed) {
		if (overlaps(x, y, w, h, other.x, other.y, other.w, other.h)) {
			return false;
		}
	}
	return true;
}

function compactPageWidgets(widgets: ArmoryLayoutWidget[], columns: number): ArmoryLayoutWidget[] {
	const copies = widgets.map((widget) => ({ ...widget }));
	const placed: ArmoryLayoutWidget[] = [];

	for (const widget of copies
		.filter((entry) => entry.visible !== false)
		.sort((a, b) => a.y - b.y || a.x - b.x)) {
		let y = 0;
		while (!canPlace(widget.x, y, widget.w, widget.h, placed, columns)) {
			y++;
		}
		widget.y = y;
		placed.push(widget);
	}

	return copies;
}

export function compactPageLayout(page: ArmoryPageLayoutConfig): ArmoryPageLayoutConfig {
	const normalized = applyWidgetMinimums(page);
	const columns = normalized.grid.columns;
	return {
		...normalized,
		widgets:
			normalized.mode === "Custom"
				? normalized.widgets
				: compactPageWidgets(normalized.widgets, columns),
	};
}

export function compactSiteLayout(site: ArmorySiteLayoutConfig): ArmorySiteLayoutConfig {
	const pages = Object.fromEntries(
		Object.entries(site.pages).map(([pageId, page]) => [pageId, compactPageLayout(page)]),
	) as Record<string, ArmoryPageLayoutConfig>;
	return { ...site, pages };
}

function compactLayout(layout: ArmoryLayoutConfig): ArmoryLayoutConfig {
	return normalizeLayout({
		...layout,
		widgets: compactPageWidgets(layout.widgets, layout.grid.columns),
	});
}

function normalizeNavbar(navbar?: ArmoryNavbarConfig): ArmoryNavbarConfig {
	if (!navbar?.links?.length) {
		return { ...DEFAULT_NAVBAR, links: DEFAULT_NAVBAR.links.map((link) => ({ ...link })) };
	}

	const links = navbar.links
		.filter((link) => link.kind !== "Custom" || (link.label?.trim() && link.href?.trim()))
		.map((link) => ({ ...link, id: link.id?.trim() || `nav-${Date.now()}` }));

	if (!links.some((link) => link.kind === "Home")) {
		links.unshift({ id: "nav-home", kind: "Home", visible: true });
	}

	return {
		showSearch: navbar.showSearch !== false,
		searchPlaceholder: navbar.searchPlaceholder?.trim() || "Search character...",
		links,
	};
}

export function resolveNavbarLinks(
	navbar: ArmoryNavbarConfig | undefined,
	ctx: {
		websiteRoot: string;
		websiteName: string;
		topRecordsEnabled: boolean;
		worldMapEnabled?: boolean;
	},
): ResolvedNavbarLink[] {
	const config = normalizeNavbar(navbar);
	const root = (ctx.websiteRoot ?? "").replace(/\/+$/, "");
	const worldMapEnabled = ctx.worldMapEnabled !== false;

	const resolved: ResolvedNavbarLink[] = [];
	for (const link of config.links) {
		if (link.visible === false) {
			continue;
		}

		if (link.kind === "Custom") {
			if (!link.label?.trim() || !link.href?.trim()) {
				continue;
			}
			resolved.push({
				id: link.id,
				label: link.label.trim(),
				href: resolveHref(root, link.href.trim()),
				openInNewTab: !!link.openInNewTab,
			});
			continue;
		}

		const builtin = builtinNavbarLink(link.kind, ctx.websiteName);
		if (!builtin) {
			continue;
		}
		if (link.kind === "TopLogs" && !ctx.topRecordsEnabled) {
			continue;
		}
		if (link.kind === "Map" && !worldMapEnabled) {
			continue;
		}

		const label =
			link.kind === "Home"
				? link.label?.trim() || ctx.websiteName || "Azeroth"
				: link.label?.trim() || builtin.label;

		resolved.push({
			id: link.id,
			label,
			href: `${root}${builtin.path}`,
			openInNewTab: !!link.openInNewTab,
		});
	}

	return resolved;
}

function builtinNavbarLink(kind: ArmoryNavbarLinkKind, _websiteName: string) {
	switch (kind) {
		case "Home":
			return { label: "", path: "/" };
		case "TopLogs":
			return { label: "Top Logs", path: "/top-logs" };
		case "Map":
			return { label: "Azeroth", path: "/map" };
		case "Connect":
			return { label: "Connect", path: "/connect" };
		case "News":
			return { label: "News", path: "/news" };
		default:
			return null;
	}
}

function resolveHref(root: string, href: string): string {
	if (/^https?:\/\//i.test(href)) {
		return href;
	}
	if (href.startsWith("/")) {
		return `${root}${href}`;
	}
	return `${root}/${href}`;
}

export function layoutHasWidget(layout: ArmoryLayoutConfig | ArmoryPageLayoutConfig, type: string): boolean {
	return layout.widgets.some((w) => w.visible !== false && w.type === type);
}

export function pageHasWidget(pageId: string, type: string, site?: ArmorySiteLayoutConfig): boolean {
	return layoutHasWidget(loadPageLayout(pageId, site), type);
}

export function layoutIntSetting(widget: ArmoryLayoutWidget, key: string, fallback: number): number {
	const value = widget.settings?.[key];
	if (typeof value === "number") {
		return value;
	}
	if (typeof value === "string") {
		const parsed = parseInt(value, 10);
		return Number.isNaN(parsed) ? fallback : parsed;
	}
	return fallback;
}

export function layoutStringSetting(widget: ArmoryLayoutWidget, key: string, fallback: string): string {
	const value = widget.settings?.[key];
	return typeof value === "string" ? value : fallback;
}

export function layoutBoolSetting(widget: ArmoryLayoutWidget, key: string, fallback: boolean): boolean {
	const value = widget.settings?.[key];
	if (typeof value === "boolean") {
		return value;
	}
	return fallback;
}

export function maxNewsLimit(layout: ArmoryLayoutConfig | ArmoryPageLayoutConfig): number {
	const limits = layout.widgets
		.filter((w) => w.visible !== false && (w.type === "News" || w.type === "NewsFeed"))
		.map((w) => layoutIntSetting(w, "limit", w.type === "NewsFeed" ? 12 : 3));
	return limits.length ? Math.max(...limits) : 3;
}

export function maxRecentLimit(layout: ArmoryLayoutConfig | ArmoryPageLayoutConfig): number {
	const limits = layout.widgets
		.filter((w) => w.visible !== false && w.type === "RecentCharacters")
		.map((w) => layoutIntSetting(w, "limit", 5));
	return limits.length ? Math.max(...limits) : 5;
}
