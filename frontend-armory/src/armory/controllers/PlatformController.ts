import * as express from "express";
import { createReadStream } from "fs";
import { access, readFile } from "fs/promises";
import * as path from "path";
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

interface LauncherBuildManifest {
	version?: string;
	fileName?: string;
	downloadAvailable?: boolean;
}

export class PlatformController {
	private armory: Armory;
	private readonly apiUrl: string;
	private readonly stackId: string;
	private readonly clientUrl: string;
	private readonly launcherDistDir: string;

	public constructor(armory: Armory) {
		this.armory = armory;
		this.apiUrl = (process.env.PLATFORM_API_URL ?? "").replace(/\/+$/, "");
		this.stackId = process.env.PLATFORM_STACK_ID ?? "";
		// Optional HTTP fallback to the stack client container (same compose network).
		this.clientUrl = (process.env.CLIENT_PORTAL_URL ?? "").replace(/\/+$/, "");
		// Preferred path: read the launcher exe directly from the shared launcher-dist volume.
		this.launcherDistDir = (process.env.CLIENT_LAUNCHER_DIST_DIR ?? "").replace(/\/+$/, "");
	}

	public get isConfigured(): boolean {
		return this.apiUrl !== "" && this.stackId !== "";
	}

	/** Whether this stack mounts the launcher-dist volume or can reach the client container. */
	public get hasClientContainer(): boolean {
		return this.launcherDistDir !== "" || this.clientUrl !== "";
	}

	private async readLocalLauncherManifest(): Promise<{ version: string; fileName: string } | null> {
		if (!this.launcherDistDir) {
			return null;
		}

		try {
			const manifestPath = path.join(this.launcherDistDir, "build.json");
			const raw = await readFile(manifestPath, "utf8");
			const manifest = JSON.parse(raw) as LauncherBuildManifest;
			const version = manifest.version?.trim();
			const fileName = manifest.fileName?.trim();
			if (!version || !fileName) {
				return null;
			}

			await access(path.join(this.launcherDistDir, fileName));
			return { version, fileName };
		} catch (err) {
			this.armory.logger.warn(`Could not read local launcher manifest: ${err}`);
			return null;
		}
	}

	private async isLauncherDownloadAvailable(): Promise<boolean> {
		const local = await this.readLocalLauncherManifest();
		if (local) {
			return true;
		}

		if (!this.clientUrl) {
			return false;
		}

		try {
			const res = await fetch(`${this.clientUrl}/launcher/latest`, {
				signal: AbortSignal.timeout(5000),
			});
			if (!res.ok) {
				return false;
			}

			const info = (await res.json()) as LauncherBuildManifest;
			return info.downloadAvailable === true;
		} catch (err) {
			this.armory.logger.warn(`Could not probe launcher availability: ${err}`);
			return false;
		}
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

	private sendLauncherUnavailable(res: express.Response, status: number): void {
		res.status(status)
			.type("txt")
			.send("Launcher not available yet. Ask an administrator to build and deploy it to this stack.");
	}

	private async downloadLauncherFromVolume(res: express.Response): Promise<boolean> {
		const manifest = await this.readLocalLauncherManifest();
		if (!manifest) {
			return false;
		}

		const exePath = path.join(this.launcherDistDir, manifest.fileName);
		res.setHeader("Content-Type", "application/octet-stream");
		res.setHeader("Content-Disposition", `attachment; filename="${manifest.fileName}"`);

		await new Promise<void>((resolve, reject) => {
			const stream = createReadStream(exePath);
			stream.on("error", reject);
			res.on("close", () => {
				if (!res.writableEnded) {
					stream.destroy();
				}
			});
			stream.on("end", resolve);
			stream.pipe(res);
		});

		return true;
	}

	private async downloadLauncherFromClient(res: express.Response): Promise<boolean> {
		if (!this.clientUrl) {
			return false;
		}

		const upstream = await fetch(`${this.clientUrl}/launcher/download`, {
			signal: AbortSignal.timeout(120_000),
		});
		if (!upstream.ok || upstream.body === null) {
			return false;
		}

		res.setHeader("Content-Type", upstream.headers.get("content-type") ?? "application/octet-stream");
		const disposition = upstream.headers.get("content-disposition");
		if (disposition) {
			res.setHeader("Content-Disposition", disposition);
		} else {
			res.setHeader("Content-Disposition", 'attachment; filename="AzerothPlatformLauncher.exe"');
		}

		await new Promise<void>((resolve, reject) => {
			const body = Readable.fromWeb(upstream.body as import("stream/web").ReadableStream);
			body.on("error", reject);
			res.on("close", () => body.destroy());
			body.on("end", resolve);
			body.pipe(res);
		});

		return true;
	}

	/** Serves the built launcher executable from the shared launcher-dist volume (preferred) or client HTTP. */
	public async downloadLauncher(req: express.Request, res: express.Response): Promise<void> {
		if (!this.hasClientContainer) {
			this.sendLauncherUnavailable(res, 404);
			return;
		}

		try {
			if (await this.downloadLauncherFromVolume(res)) {
				return;
			}

			if (await this.downloadLauncherFromClient(res)) {
				return;
			}

			this.sendLauncherUnavailable(res, 404);
		} catch (err) {
			this.armory.logger.warn(`Launcher download failed: ${err}`);
			if (!res.headersSent) {
				res.status(503)
					.type("txt")
					.send(
						"Launcher is not available right now. The launcher may not be deployed yet, or the armory needs to be recreated to mount the launcher volume.",
					);
			}
		}
	}

	/** Renders the Connect page (launcher download + how-to-connect info). */
	public async connect(req: express.Request, res: express.Response): Promise<void> {
		const root = this.armory.config.websiteRoot ?? "";
		const layoutRender = buildLayoutRenderModel("connect");
		const downloadAvailable = await this.isLauncherDownloadAvailable();
		res.render("connect.hbs", {
			title: `Connect - ${this.armory.config.websiteName ?? "Armory"}`,
			realmName: this.armory.config.realms[0]?.name ?? "AzerothCore",
			downloadAvailable,
			downloadUrl: `${root}/download-launcher`,
			...layoutRender,
		});
	}
}
