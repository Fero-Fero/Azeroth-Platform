import {
	type ArmoryLayoutWidget,
	layoutBoolSetting,
	layoutIntSetting,
	layoutStringSetting,
	loadArmorySiteLayout,
	loadPageLayout,
	type ArmoryPageLayoutConfig,
} from "./ArmoryLayout";
import { characterLayoutPageId } from "./ArmoryLayoutPages";

export interface EnrichedLayoutWidget extends ArmoryLayoutWidget {
	typeClass: string;
	widgetTitle?: string;
	showViewAll?: boolean;
	newsItems?: unknown[];
	recentItems?: unknown[];
	pageLength?: number;
}

export function sortedLayoutWidgets(page: ArmoryPageLayoutConfig): ArmoryLayoutWidget[] {
	return page.widgets
		.filter((widget) => widget.visible !== false)
		.sort((a, b) => a.y - b.y || a.x - b.x);
}

export function enrichHomeWidget(
	widget: ArmoryLayoutWidget,
	news: unknown[],
	recentCharacters: unknown[],
): EnrichedLayoutWidget {
	const typeClass = widget.type.replace(/([A-Z])/g, "-$1").toLowerCase().replace(/^-/, "");
	return {
		...widget,
		typeClass,
		widgetTitle:
			widget.type === "News" || widget.type === "NewsFeed"
				? layoutStringSetting(widget, "title", "Latest News")
				: widget.type === "RecentCharacters"
					? layoutStringSetting(widget, "title", "Recently Active")
					: "",
		showViewAll: layoutBoolSetting(widget, "showViewAll", true),
		newsItems:
			widget.type === "News" || widget.type === "NewsFeed"
				? news.slice(0, layoutIntSetting(widget, "limit", widget.type === "NewsFeed" ? 12 : 3))
				: [],
		recentItems:
			widget.type === "RecentCharacters"
				? recentCharacters.slice(0, layoutIntSetting(widget, "limit", 5))
				: [],
		pageLength: layoutIntSetting(widget, "pageLength", 50),
	};
}

const PAGE_TITLE_DEFAULTS: Record<string, string> = {
	connect: "Connect",
	"news-list": "News",
	"top-logs": "Top Logs",
};

export function enrichGenericWidget(widget: ArmoryLayoutWidget, pageId?: string): EnrichedLayoutWidget {
	const typeClass = widget.type.replace(/([A-Z])/g, "-$1").toLowerCase().replace(/^-/, "");
	const widgetTitle =
		widget.type === "PageTitle"
			? layoutStringSetting(widget, "title", pageId ? (PAGE_TITLE_DEFAULTS[pageId] ?? "") : "")
			: undefined;
	return { ...widget, visible: widget.visible !== false, typeClass, widgetTitle: widgetTitle || undefined };
}

export function buildLayoutRenderModel(
	pageId: string,
	options?: {
		news?: unknown[];
		recentCharacters?: unknown[];
	},
): { pageId: string; layoutWidgets: EnrichedLayoutWidget[]; hasCharacterSearch: boolean } {
	const site = loadArmorySiteLayout();
	const page = loadPageLayout(pageId, site);
	const widgets = sortedLayoutWidgets(page).map((widget) => {
		if (pageId === "home") {
			return enrichHomeWidget(widget, options?.news ?? [], options?.recentCharacters ?? []);
		}
		if (pageId === "news-list") {
			return enrichHomeWidget(widget, options?.news ?? [], []);
		}
		return enrichGenericWidget(widget, pageId);
	});

	return {
		pageId,
		layoutWidgets: widgets,
		hasCharacterSearch: widgets.some((widget) => widget.type === "CharacterSearch"),
	};
}

export { characterLayoutPageId, loadPageLayout, loadArmorySiteLayout };
