import * as express from "express";
import { Readable } from "stream";
import { decode } from "html-entities";

import { Armory } from "../Armory";
import { buildLayoutRenderModel } from "../LayoutWidgets";

/**
 * Bridges the armory to the Azeroth Platform backend for this stack: it fetches per-stack launcher
 * news, proxies news cover images, and proxies the launcher download so the browser never needs to
 * reach the platform API directly (avoids CORS / internal-hostname problems).
 */
export interface NewsItem {
	id: string;
	title: string;
	date: string;
	html: string;
	/** Plain-text summary of the body, used on the home-page cards. */
	excerpt: string;
	/**
	 * Optional content category (patch/announcement/expansion/event/update/hotfix), rendered as a
	 * colored corner ribbon on the news cards. Empty string means no ribbon.
	 */
	tag: string;
	hasImage: boolean;
	imageUrl: string | null;
	/** Link to this article's full detail page on the armory. */
	url: string;
}

export class PlatformController {
	private armory: Armory;
	private readonly apiUrl: string;
	private readonly stackId: string;
	private readonly clientUrl: string;

	public constructor(armory: Armory) {
		this.armory = armory;
		this.apiUrl = (process.env.PLATFORM_API_URL ?? "").replace(/\/+$/, "");
		this.stackId = process.env.PLATFORM_STACK_ID ?? "";
		// This stack's own client-server container (same compose network). The launcher exe is served
		// from here so the armory never has to reach the central manager for a download.
		this.clientUrl = (process.env.CLIENT_PORTAL_URL ?? "").replace(/\/+$/, "");
	}

	public get isConfigured(): boolean {
		return this.apiUrl !== "" && this.stackId !== "";
	}

	/** Whether this stack's own client container URL is known (for the self-contained launcher download). */
	public get hasClientContainer(): boolean {
		return this.clientUrl !== "";
	}

	/** Fetches every published article for this stack (cover images + links pointed at the armory). */
	private async fetchNews(): Promise<NewsItem[]> {
		if (!this.isConfigured) {
			return [];
		}

		try {
			const res = await fetch(`${this.apiUrl}/api/stacks/${this.stackId}/launcher/news`, {
				signal: AbortSignal.timeout(5000),
			});
			if (!res.ok) {
				return [];
			}

			const items = (await res.json()) as Array<{
				id: string;
				title: string;
				date: string;
				html: string;
				tag?: string;
				hasImage: boolean;
			}>;

			const root = this.armory.config.websiteRoot ?? "";
			return items.map((item) => ({
				id: item.id,
				title: item.title,
				date: item.date,
				html: item.html,
				excerpt: PlatformController.makeExcerpt(item.html),
				tag: (item.tag ?? "").toLowerCase(),
				hasImage: item.hasImage,
				imageUrl: item.hasImage ? `${root}/news-image/${encodeURIComponent(item.id)}` : null,
				url: `${root}/news/${encodeURIComponent(item.id)}`,
			}));
		} catch (err) {
			this.armory.logger.warn(`Could not load platform news: ${err}`);
			return [];
		}
	}

	/** Fetches the latest news for this stack, for the home-page cards. */
	public async getNews(limit = 6): Promise<NewsItem[]> {
		return (await this.fetchNews()).slice(0, limit);
	}

	/** Renders the "View all" news page: every published article plus a client-side search box. */
	public async newsList(req: express.Request, res: express.Response): Promise<void> {
		const news = await this.fetchNews();
		const layoutRender = buildLayoutRenderModel("news-list", { news });
		res.render("news-list.hbs", {
			title: `News - ${this.armory.config.websiteName ?? "Armory"}`,
			news,
			...layoutRender,
		});
	}

	/** Finds a single article by id (for the full detail page), or null if it doesn't exist. */
	public async getNewsItem(id: string): Promise<NewsItem | null> {
		return (await this.fetchNews()).find((item) => item.id === id) ?? null;
	}

	/** Renders a single news article in full (title, date, cover image, rich HTML body). */
	public async newsArticle(req: express.Request, res: express.Response): Promise<void> {
		const article = await this.getNewsItem(String(req.params.id));
		if (article === null) {
			// Handled by the shared error middleware (renders the 404 page).
			throw 404;
		}

		res.render("news.hbs", {
			title: `${article.title} - ${this.armory.config.websiteName ?? "Armory"}`,
			article,
		});
	}

	/** Builds a short plain-text summary from a sanitized HTML body for the news cards. */
	private static makeExcerpt(html: string, max = 180): string {
		const text = decode(String(html ?? "").replace(/<[^>]+>/g, " "))
			.replace(/\s+/g, " ")
			.trim();
		return text.length > max ? `${text.slice(0, max).trimEnd()}…` : text;
	}

	/** Proxies a news cover image from the platform so the browser can load it same-origin. */
	public async newsImage(req: express.Request, res: express.Response): Promise<void> {
		if (!this.isConfigured) {
			res.status(404).send("News image unavailable.");
			return;
		}

		const id = String(req.params.id);
		const upstream = await fetch(
			`${this.apiUrl}/api/stacks/${this.stackId}/launcher/news-image/${encodeURIComponent(id)}`,
			{ signal: AbortSignal.timeout(10000) },
		);
		if (!upstream.ok || upstream.body === null) {
			res.status(404).send("News image not found.");
			return;
		}

		res.setHeader("Content-Type", upstream.headers.get("content-type") ?? "image/png");
		res.setHeader("Cache-Control", "public, max-age=300");
		Readable.fromWeb(upstream.body as import("stream/web").ReadableStream).pipe(res);
	}

	/** Proxies the built launcher executable download from this stack's own client container. */
	public async downloadLauncher(req: express.Request, res: express.Response): Promise<void> {
		if (!this.hasClientContainer) {
			res.status(404).send("Launcher download unavailable.");
			return;
		}

		const upstream = await fetch(`${this.clientUrl}/launcher/download`, {
			signal: AbortSignal.timeout(30000),
		});
		if (!upstream.ok || upstream.body === null) {
			res.status(404).send("The launcher has not been built yet. Ask an administrator to build it.");
			return;
		}

		res.setHeader("Content-Type", upstream.headers.get("content-type") ?? "application/octet-stream");
		const disposition = upstream.headers.get("content-disposition");
		if (disposition) {
			res.setHeader("Content-Disposition", disposition);
		} else {
			res.setHeader("Content-Disposition", "attachment; filename=\"AzerothPlatformLauncher.exe\"");
		}
		Readable.fromWeb(upstream.body as import("stream/web").ReadableStream).pipe(res);
	}

	/** Renders the Connect page (launcher download + how-to-connect info). */
	public async connect(req: express.Request, res: express.Response): Promise<void> {
		const root = this.armory.config.websiteRoot ?? "";
		const layoutRender = buildLayoutRenderModel("connect");
		res.render("connect.hbs", {
			title: `Connect - ${this.armory.config.websiteName ?? "Armory"}`,
			realmName: this.armory.config.realms[0]?.name ?? "AzerothCore",
			downloadAvailable: this.hasClientContainer,
			downloadUrl: `${root}/download-launcher`,
			...layoutRender,
		});
	}
}
