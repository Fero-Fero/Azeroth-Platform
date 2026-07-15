import * as path from "path";
import { Readable } from "stream";
import { randomBytes } from "crypto";
import * as uuid from "uuid";
import express, { Express } from "express";
import cookieParser from "cookie-parser";
import rateLimit from "express-rate-limit";
import * as winston from "winston";
import morgan from "morgan";
import { Pool, createPool, RowDataPacket } from "mysql2/promise";
import { engine as handlebarsEngine } from "express-handlebars";

import { Config, IRealmConfig } from "./Config";
import { AccountController } from "./controllers/AccountController";
import { getSession } from "./auth/Session";
import { issueCsrfToken, verifyCsrf } from "./auth/Csrf";
import { createEmailConfirmationGate } from "./middleware/EmailConfirmationGate";
import { DbcManager } from "./data/DbcReader";
import { RaidTrackerCatalog } from "./data/RaidTrackerCatalog";
import { CharacterCustomization, CharacterCustomizationDataUnavailableError } from "./data/CharacterCustomization";
import { IndexController } from "./controllers/IndexController";
import { PlatformController } from "./controllers/PlatformController";
import { CharacterController } from "./controllers/CharacterController";
import { GuildController } from "./controllers/GuildController";
import { ArenaController } from "./controllers/ArenaController";
import { MapController } from "./controllers/MapController";
import { TopRecordsController } from "./controllers/TopRecordsController";
import { IQuest, IQuestComparison } from './types/QuestTypes';
import { loadArmorySiteLayout, resolveNavbarLinks } from "./ArmoryLayout";
import { AccountProfile, AccountProfileStore } from "./auth/AccountProfileStore";
import { isAccountSession, isPendingSession } from "./auth/Session";

function displayNameInitials(displayName: string): string {
	const trimmed = displayName.trim();
	if (!trimmed) {
		return "?";
	}
	const parts = trimmed.split(/\s+/).filter(Boolean);
	if (parts.length >= 2) {
		return `${parts[0][0] ?? ""}${parts[1][0] ?? ""}`.toUpperCase();
	}
	return trimmed.slice(0, 2).toUpperCase();
}

export class Armory {
	public characterCustomization: CharacterCustomization;
	public dbc: DbcManager;
	public config!: Config;
	public worldDb!: Pool;
	public raidTrackerCatalog!: RaidTrackerCatalog;
	public logger: winston.Logger;
	public charsetCache: { [key: string]: string };

	private charsDbs: { [key: string]: Pool };
	private authDbs: { [key: string]: Pool };
	// Cache of per-realm characters-DB table existence probes, used to detect optional server
	// modules (raid trackers). Installation only changes on a stack rebuild (which restarts the
	// armory), so probing once per realm+table for the process lifetime is sufficient.
	private charsTableCache: Map<string, boolean>;
	private profileStore: AccountProfileStore | null = null;
	private errorNames: { [key: number]: string };
	private errorDescriptions: { [key: number]: string };

	public constructor() {
		this.dbc = new DbcManager();
		this.characterCustomization = new CharacterCustomization();
		this.charsDbs = {};
		this.authDbs = {};
		this.charsTableCache = new Map();
		this.logger = winston.createLogger({
			level: "info",
			format: winston.format.combine(
				winston.format.timestamp({ format: "YYYY-MM-DD HH:mm:ss:ms" }),
				winston.format.printf((info) => `[${info.timestamp}] [${info.level.toUpperCase()}]: ${info.message}`),
			),
			transports: [
				new winston.transports.Console({ level: "debug" }),
				new winston.transports.File({ filename: path.join("logs", "armory.error.log"), level: "error" }),
				new winston.transports.File({ filename: path.join("logs", "armory.combined.log"), level: "http" }),
			],
		});
		this.charsetCache = {};

		this.errorNames = {
			400: "Bad Request",
			401: "Unauthorized",
			403: "Forbidden",
			404: "Not Found",
			500: "Internal Server Error",
		};
		this.errorDescriptions = {
			400: "Invalid request.",
			404: "Sorry, we could not find what you were looking for.",
			500: "An unexpected internal error has occurred. Please contact the site owner.",
		};
	}

	public async start(): Promise<void> {
		const app: Express = express();
		const listenPort = 48733;

		app.set("query parser", "extended");
		// Honor X-Forwarded-Proto/For when running behind a reverse proxy so req.secure and client
		// IPs reflect the original request (used for Secure cookies and rate limiting).
		app.set("trust proxy", true);

		this.logger.info("Loading config...");
		this.config = await Config.load(this.logger);
		if (this.config.accounts.enabled && !this.config.accounts.sessionSecret) {
			// Without a configured secret, fall back to an ephemeral one so login still works; sessions
			// won't survive a restart. The platform normally injects a stable per-stack secret.
			this.config.accounts.sessionSecret = randomBytes(32).toString("hex");
			this.logger.warn("No account session secret configured; using an ephemeral one (sessions reset on restart).");
		}
		this.logger.info("Loading data files...");
		if (this.config.loadDbcs) {
			try {
				await this.dbc.loadAllFiles();
			} catch (err) {
				// DBC datasets (static/data/dbc) drive item/model metadata. They are normally shipped with the
				// image; if absent, keep the armory up with reduced item/model detail rather than crashing.
				this.logger.warn(`Could not load DBC data files; item/model detail will be limited. (${err})`);
			}
		}
		try {
			await this.characterCustomization.loadData((this.config.assetProxyUrl ?? "").replace(/\/+$/, ""));
		} catch (err) {
			if (err instanceof CharacterCustomizationDataUnavailableError) {
				// The multi-GB 3D model-viewer data (static/data/{meta,mo3,textures,bone}) is
				// intentionally excluded from the platform's armory image. The armory still serves every
				// DB-backed page; only the 3D character model viewer is unavailable.
				this.logger.warn(err.message);
			} else {
				throw err;
			}
		}

		this.logger.info("Connecting to databases...");
		this.worldDb = createPool(this.config.worldDatabase);
		// Shared instance/boss catalogue of the raid tracker modules, read from the world DB
		// (raid_tracker_* tables). Loaded lazily on first use.
		this.raidTrackerCatalog = new RaidTrackerCatalog(this);
		for (const realm of this.config.realms) {
			this.charsDbs[realm.name.toLowerCase()] = createPool(realm.charactersDatabase);
			// The auth DB lives on the same MySQL server as this realm's characters DB; reuse those
			// credentials, only swapping the database name. Used for player login/registration.
			this.authDbs[realm.name.toLowerCase()] = createPool({
				...realm.charactersDatabase,
				database: realm.authDatabase,
			});
		}

		this.logger.info("Starting server...");

		const locals: { [key: string]: unknown } = {
            websiteUrl: this.config.websiteUrl,
			websiteName: this.config.websiteName,
			websiteRoot: this.config.websiteRoot,
			iframeMode: this.config.iframeMode,
		};
		for (const key of Object.keys(locals)) {
			app.locals[key] = locals[key];
		}
		app.locals.locals = locals;

		app.engine(
			".hbs",
			handlebarsEngine({
				extname: "hbs",
				partialsDir: path.join(process.cwd(), "static", "partials"),
				layoutsDir: path.join(process.cwd(), "static"),
				defaultLayout: "layout.hbs",
				helpers: {
					// eslint-disable-next-line @typescript-eslint/no-var-requires
					...(() => {
						const bundled = require("handlebars-helpers")();
						// The bundled `error` helper logs to the console and renders nothing, which breaks
						// auth forms that display `{{authError}}` if templates ever use `{{error}}` again.
						delete bundled.error;
						return bundled;
					})(),
                    hasInProgressQuests: function(quests: IQuest[]): boolean {
                        return quests.some(quest => quest.status === 'In Progress');
                    },
                    getDiffClass: function(quest: IQuestComparison): string {
                        if (!quest.char1Status && !quest.char2Status) {
                            return '';
                        }
                        if (!quest.char1Status) {
                            return 'quest-missing-1';
                        }
                        if (!quest.char2Status) {
                            return 'quest-missing-2';
                        }
                        if (quest.char1Status !== quest.char2Status) {
                            return 'quest-diff';
                        }

                        return '';
                    },
                    getDiffCount: function(quests: IQuestComparison[]): number {
                        return quests.reduce((count, quest) => {
                            if (!quest.char1Status && quest.char2Status) {
                                return count + 1;
                            }
                            if (quest.char1Status && !quest.char2Status) {
                                return count + 1;
                            }
                            if (quest.char1Status !== quest.char2Status) {
                                return count + 1;
                            }

                            return count;
                        }, 0);
                    },
                    getActiveQuests: function(categories: { [key: string]: IQuestComparison[] }): (IQuestComparison & { category: string })[] {
                        const activeQuests: (IQuestComparison & { category: string })[] = [];
                        Object.entries(categories).forEach(([category, quests]) => {
                            quests.forEach(quest => {
                                if (quest.char1Status === 'In Progress' || quest.char2Status === 'In Progress') {
                                    activeQuests.push({
                                        ...quest,
                                        category
                                    });
                                }
                            });
                        });
                        return activeQuests.sort((a, b) => {
                            // First sort by category
                            const categoryCompare = a.category.localeCompare(b.category);
                            if (categoryCompare !== 0) {
                                return categoryCompare;
                            }

                            // Then by title within same category
                            return a.title.localeCompare(b.title);
                        });
                    }
				},
			}),
		);
		app.set("view engine", "handlebars");
		app.set("views", path.join(process.cwd(), "static"));

		app.use((req: express.Request, res: express.Response, next: express.NextFunction) => {
			req.id = uuid.v4();
			next();
		});

		morgan.token("id", (req: express.Request) => {
			return req.id;
		});
		morgan.token("ip", (req: express.Request) => {
			const forwardedFor = req.headers["x-forwarded-for"];
			if (forwardedFor) {
				if (typeof forwardedFor === "string") {
					return forwardedFor;
				}
				return forwardedFor.join(", ");
			}
			return req.socket.remoteAddress;
		});
		app.use(
			morgan(":method :url :status - ID :id - IP :ip - :response-time ms", {
				stream: {
					write: (msg: string) => this.logger.http(msg.trim()),
				},
			}),
		);

		app.get("/health", (_req: express.Request, res: express.Response) => {
			res.status(200).json({ status: "ok" });
		});

		app.use("/js", express.static("static/js"));
		app.use("/css", express.static("static/css"));
		app.use("/img", express.static("static/img"));
		// Heavy 3D model-viewer datasets are excluded from the armory image. When an asset sidecar is
		// configured they are proxied from it server-side (browser stays same-origin, no CORS); otherwise
		// they fall back to whatever exists locally.
		const assetBase = (this.config.assetProxyUrl ?? "").replace(/\/+$/, "");
		if (assetBase) {
			this.logger.info(`Serving 3D model-viewer assets via sidecar: ${assetBase}`);
		}
		app.use("/static/data/mo3", this.assetRoute("mo3", "static/data/mo3", assetBase));
		app.use("/static/data/meta", this.assetRoute("meta", "static/data/meta", assetBase));
		app.use("/static/data/bone", this.assetRoute("bone", "static/data/bone", assetBase));
		app.use("/static/data/textures", this.assetRoute("textures", "static/data/textures", assetBase));
		// Progression artwork (dungeon/raid/world boss card backgrounds) is uploaded via armory.data.zip
		// under progression/ and served from the per-stack asset sidecar when configured, with a local
		// static/data/progression fallback for dev / default images baked into the image.
		app.use("/static/data/progression", this.assetRoute("progression", "static/data/progression", assetBase));
		app.use("/static/data/background.png", express.static("static/data/modelviewer-background.png"));

		app.use(cookieParser());
		app.use(express.urlencoded({ extended: false }));

		// Only account features rely on cookies/CSRF, so scope the token issuance to that mode.
		if (this.config.accounts.enabled) {
			app.use(issueCsrfToken);
			await this.getProfileStore().ensureTable();
		}

		// Expose the current player session (if any) to every view for the navbar.
		app.use(async (req: express.Request, res: express.Response, next: express.NextFunction) => {
			try {
				res.locals.accountsEnabled = this.config.accounts.enabled;
				res.locals.allowRegistration =
					this.config.accounts.enabled &&
					this.config.accounts.allowRegistration &&
					(!this.config.accounts.emailConfirmationEnabled || this.config.accounts.emailConfigured);
				res.locals.emailConfirmationEnabled = this.config.accounts.emailConfirmationEnabled;
				const session = this.config.accounts.enabled ? getSession(req, this.config.accounts) : null;
				if (session && isAccountSession(session)) {
					let displayName = session.username;
					try {
						const profile = await this.ensureAccountProfile(session.id);
						displayName = profile.displayName || session.username;
					} catch (err) {
						this.logger.warn(`Failed to load account profile for navbar: ${err}`);
					}
					res.locals.currentUser = {
						displayName,
						initials: displayNameInitials(displayName),
						isPending: false,
					};
				} else if (session && isPendingSession(session)) {
					res.locals.currentUser = {
						displayName: "Verify email",
						initials: "!",
						isPending: true,
					};
				} else {
					res.locals.currentUser = null;
				}
			} catch (err) {
				return next(err);
			}
			res.locals.handlebarsData = {
				websiteUrl: this.config.websiteUrl,
				websiteName: this.config.websiteName,
				websiteRoot: this.config.websiteRoot,
				iframeMode: this.config.iframeMode,
				currentUser: res.locals.currentUser,
			};
			res.locals.worldMapEnabled = this.config.worldMapModule;
			// The Top Logs nav tab is only shown when the clear tracker is installed somewhere
			// (cached table probes, so this is only expensive on the very first request).
			res.locals.topRecordsEnabled = await this.isLogsTrackerInstalledAnywhere();

			const layout = loadArmorySiteLayout();
			const navbar = layout.navbar;
			res.locals.navbarLinks = resolveNavbarLinks(navbar, {
				websiteRoot: this.config.websiteRoot ?? "",
				websiteName: this.config.websiteName ?? "Azeroth",
				topRecordsEnabled: !!res.locals.topRecordsEnabled,
				worldMapEnabled: !!res.locals.worldMapEnabled,
			});
			res.locals.navbarShowSearch = navbar?.showSearch !== false;
			res.locals.navbarSearchPlaceholder = navbar?.searchPlaceholder?.trim() || "Search character...";

			next();
		});

		if (this.config.accounts.enabled && this.config.accounts.emailConfirmationEnabled) {
			app.use(createEmailConfirmationGate(this.config));
		}

		let accountControllerRef: AccountController | null = null;
		if (this.config.accounts.enabled) {
			const accountController = new AccountController(this);
			accountControllerRef = accountController;
			await accountController.initialize();
			// Throttle credential endpoints to slow brute-force / mass-registration.
			const authLimiter = rateLimit({
				windowMs: 15 * 60 * 1000,
				limit: 20,
				standardHeaders: true,
				legacyHeaders: false,
			});
			app.get("/login", this.wrapRoute(async (req, res) => accountController.loginForm(req, res)));
			app.post("/login", authLimiter, verifyCsrf, this.wrapRoute(accountController.login.bind(accountController)));
			app.get("/register", this.wrapRoute(async (req, res) => accountController.registerForm(req, res)));
			app.post("/register", authLimiter, verifyCsrf, this.wrapRoute(accountController.register.bind(accountController)));
			app.get("/verify-email-pending", this.wrapRoute(async (req, res) => accountController.verifyEmailPendingForm(req, res)));
			app.post(
				"/verify-email-pending/resend",
				authLimiter,
				verifyCsrf,
				this.wrapRoute(accountController.resendVerification.bind(accountController)),
			);
			app.get("/verify-email", this.wrapRoute(accountController.verifyEmail.bind(accountController)));
			app.get("/choose-username", this.wrapRoute(async (req, res) => accountController.chooseUsernameForm(req, res)));
			app.post("/choose-username", authLimiter, verifyCsrf, this.wrapRoute(accountController.chooseUsername.bind(accountController)));
			app.post("/logout", verifyCsrf, this.wrapRoute(async (req, res) => accountController.logout(req, res)));
			app.get("/account", this.wrapRoute(accountController.account.bind(accountController)));
			app.get("/account/details", this.wrapRoute(accountController.accountDetails.bind(accountController)));
			app.get("/account/characters", this.wrapRoute(accountController.accountCharacters.bind(accountController)));
			app.get("/account/guild", this.wrapRoute(accountController.accountGuild.bind(accountController)));
			app.post(
				"/account/profile",
				authLimiter,
				verifyCsrf,
				this.wrapRoute(accountController.updateProfile.bind(accountController)),
			);
		}

		const platformController = new PlatformController(this);
		const indexController = new IndexController(this, platformController);
		app.get("/", this.wrapRoute(indexController.index.bind(indexController)));
		app.get("/search", this.wrapRoute(indexController.search.bind(indexController)));
		app.get("/api/search", this.wrapRoute(indexController.searchSuggest.bind(indexController)));

		// Platform bridge: Connect page + proxied news images and launcher download.
		app.get("/connect", this.wrapRoute(platformController.connect.bind(platformController)));
		app.get("/news", this.wrapRoute(platformController.newsList.bind(platformController)));
		app.get("/news/:id", this.wrapRoute(platformController.newsArticle.bind(platformController)));
		app.get("/news-image/:id", this.wrapRoute(platformController.newsImage.bind(platformController)));
		app.get("/download-launcher", this.wrapRoute(platformController.downloadLauncher.bind(platformController)));

		const charsController = new CharacterController(this);
		await charsController.load();
		if (accountControllerRef) {
			accountControllerRef.setItemLookup(charsController);
			app.get("/account/guild/bank", this.wrapRoute(accountControllerRef.guildBank.bind(accountControllerRef)));
		}
		app.get("/character/:realm/:name", this.wrapRoute(charsController.character.bind(charsController)));
		app.get("/character/:realm/:name/talents", this.wrapRoute(charsController.talents.bind(charsController)));
        app.get("/character/:realm/:name/skills", this.wrapRoute(charsController.skills.bind(charsController)));
		app.get("/character/:realm/:name/achievements", this.wrapRoute(charsController.achievements.bind(charsController)));
		app.get("/character/:realm/:character/achievements/data", this.wrapRoute(charsController.achievementsData.bind(charsController)));
		app.get("/character/:realm/:name/progression", this.wrapRoute(charsController.progression.bind(charsController)));
		app.get("/character/:realm/:character/progression/data", this.wrapRoute(charsController.progressionData.bind(charsController)));
		app.get("/character/:realm/:name/logs", this.wrapRoute(charsController.records.bind(charsController)));
		app.get("/character/:realm/:character/logs/data", this.wrapRoute(charsController.recordsData.bind(charsController)));
		app.get("/character/:realm/:name/records", this.wrapRoute(charsController.records.bind(charsController)));
		app.get("/character/:realm/:character/records/data", this.wrapRoute(charsController.recordsData.bind(charsController)));
		app.get("/character/:realm/:name/pvp", this.wrapRoute(charsController.pvp.bind(charsController)));
        app.get("/character/:realm/:name/reputation", this.wrapRoute(charsController.reputation.bind(charsController)));
        app.get("/character/:realm/:name/mounts", this.wrapRoute(charsController.mountsPage.bind(charsController)));
        app.get("/character/:realm/:name/pets", this.wrapRoute(charsController.pets.bind(charsController)));
        app.get("/character/:realm/:name/companions", this.wrapRoute(charsController.companions.bind(charsController)));
        app.get("/character/:realm/:name/quests", this.wrapRoute(charsController.quests.bind(charsController)));
        app.get("/character/:realm/:name/quests/compare/:otherRealm/:otherName", this.wrapRoute(charsController.questsCompare.bind(charsController)));

		const guildsController = new GuildController(this);
		app.get("/guild/:realm/:name", this.wrapRoute(guildsController.guild.bind(guildsController)));
		app.get("/guild/:realm/:guild/members", this.wrapRoute(guildsController.members.bind(guildsController)));

		const arenaController = new ArenaController(this);
		app.get("/arena", this.wrapRoute(arenaController.index.bind(arenaController)));
		app.get("/arena/ladder", this.wrapRoute(arenaController.ladder.bind(arenaController)));
		app.get("/arena/team/:realm/:name", this.wrapRoute(arenaController.team.bind(arenaController)));

		// Server-wide fastest clear/boss-kill leaderboards (mod-raid-logs-tracker). The routes
		// 404 when the module is not installed; the nav tab is hidden via topRecordsEnabled.
		const topRecordsController = new TopRecordsController(this);
		app.get("/top-logs", this.wrapRoute(topRecordsController.index.bind(topRecordsController)));
		app.get("/top-logs/data", this.wrapRoute(topRecordsController.data.bind(topRecordsController)));
		app.get("/top-records", this.wrapRoute(topRecordsController.index.bind(topRecordsController)));
		app.get("/top-records/data", this.wrapRoute(topRecordsController.data.bind(topRecordsController)));

		// The Azeroth world map is optional: when disabled, its routes are not registered (so /map and
		// /map/data 404) and the nav link is hidden via the worldMapEnabled view local.
		if (this.config.worldMapModule) {
			const mapController = new MapController(this);
			await mapController.load();
			app.get("/map", this.wrapRoute(mapController.map.bind(mapController)));
			app.get("/map/data", this.wrapRoute(mapController.mapData.bind(mapController)));
		}

		app.use((err: unknown, req: express.Request, res: express.Response, next: express.NextFunction) => {
			// Error handler

			if (err instanceof Error) {
				const contents = err.stack ?? `${err.name}: ${err.message}`;
				this.logger.error(`Error on request ${req.id}. ${contents}`);
			}

			let status = 500;
			if (typeof err === "number") {
				status = err;
			}

			this.sendError(req, res, status);
		});

		app.use((req: express.Request, res: express.Response, next: express.NextFunction) => {
			// 404 handler
			this.sendError(req, res, 404);
		});

		this.gc();
		app.listen(listenPort, "0.0.0.0", () => {
			this.logger.info(`Server is listening on 0.0.0.0:${listenPort}.`);
		});
	}

	public getCharactersDb(realm: string): Pool {
		return this.charsDbs[realm.toLowerCase()];
	}

	public getAuthDb(realm: string): Pool {
		return this.authDbs[realm.toLowerCase()];
	}

	private getProfileStore(): AccountProfileStore {
		if (!this.profileStore) {
			const realm = this.config.realms[0]?.name;
			if (!realm) {
				throw new Error("No realm configured for account profiles.");
			}
			this.profileStore = new AccountProfileStore(
				this.getAuthDb(realm),
				this.config.dbQueryTimeout,
			);
		}
		return this.profileStore;
	}

	public async preferredDisplayName(accountId: number): Promise<string> {
		let best: { level: number; name: string } | null = null;
		for (const realm of this.config.realms) {
			const [rows] = await this.getCharactersDb(realm.name).query<RowDataPacket[]>({
				sql: `
					SELECT \`name\`, \`level\`
					FROM \`characters\`
					WHERE \`account\` = ? AND \`deleteInfos_Account\` IS NULL
					ORDER BY \`level\` DESC, \`name\` ASC
					LIMIT 1
				`,
				values: [accountId],
				timeout: this.config.dbQueryTimeout,
			});
			if (rows.length === 0) {
				continue;
			}
			const row = rows[0];
			if (!best || row.level > best.level) {
				best = { level: row.level, name: row.name };
			}
		}
		return best?.name ?? `Player${accountId}`;
	}

	public async ensureAccountProfile(accountId: number): Promise<AccountProfile> {
		const store = this.getProfileStore();
		return store.getOrCreate(accountId, await this.preferredDisplayName(accountId));
	}

	public getRealm(realm: string): IRealmConfig | undefined {
		return this.config.realms.find((r) => r.name.toLowerCase() === realm.toLowerCase());
	}

	/**
	 * Whether the raid-progression-tracker module is installed on the given realm (its
	 * `raid_progression_tracker` table exists in the realm's characters DB).
	 */
	public async isProgressionModuleInstalled(realm: string): Promise<boolean> {
		return this.hasCharactersTable(realm, "raid_progression_tracker");
	}

	/**
	 * Whether the raid-logs-tracker module is installed on the given realm (its
	 * `raid_logs_tracker` table exists in the realm's characters DB).
	 */
	public async isLogsTrackerModuleInstalled(realm: string): Promise<boolean> {
		return this.hasCharactersTable(realm, "raid_logs_tracker");
	}

	/** Whether the raid-logs-tracker module is installed on at least one realm. */
	public async isLogsTrackerInstalledAnywhere(): Promise<boolean> {
		const results = await Promise.all(this.config.realms.map((realm) => this.isLogsTrackerModuleInstalled(realm.name)));
		return results.some((installed) => installed);
	}

	/**
	 * Probes the realm's characters DB for a table, caching the result per realm+table for the
	 * process lifetime (optional module tables only appear/disappear on a stack rebuild, which
	 * restarts the armory). A failed probe is treated as "absent" and cached like any result.
	 */
	public async hasGuildBankTables(realm: string): Promise<boolean> {
		return this.hasCharactersTable(realm, "guild_bank_tab");
	}

	private async hasCharactersTable(realm: string, table: string): Promise<boolean> {
		const cacheKey = `${realm.toLowerCase()}:${table}`;
		const cached = this.charsTableCache.get(cacheKey);
		if (cached !== undefined) {
			return cached;
		}

		let installed = false;
		try {
			const [rows] = await this.getCharactersDb(realm).query<RowDataPacket[]>({
				sql: "SELECT 1 FROM `information_schema`.`tables` WHERE `table_schema` = DATABASE() AND `table_name` = ? LIMIT 1",
				values: [table],
				timeout: this.config.dbQueryTimeout,
			});
			installed = (rows as RowDataPacket[]).length > 0;
		} catch {
			installed = false;
		}

		this.charsTableCache.set(cacheKey, installed);
		return installed;
	}

	public async getDatabaseCharset(realm: string): Promise<string> {
		const db = this.getCharactersDb(realm);

		if (!(realm in this.charsetCache)) {
			const [rows] = await db.query<RowDataPacket[]>({
				sql: `
					SELECT CCSA.character_set_name AS charset FROM information_schema.\`TABLES\` T,
					information_schema.\`COLLATION_CHARACTER_SET_APPLICABILITY\` CCSA
					WHERE CCSA.collation_name = T.table_collation
					AND T.table_schema = "${(await db.getConnection()).config.database}"
					AND T.table_name = "characters"
				`,
				timeout: this.config.dbQueryTimeout,
			});
			this.charsetCache[realm] = rows[0].charset;
		}
		return this.charsetCache[realm];
	}

	public gc(): void {
		if (this.config.loadDbcs) {
			return;
		}

		setTimeout(() => {
			if (global.gc) {
				global.gc();
			}
		}, 500);
	}

	/**
	 * Serves a model-viewer asset directory. When an asset sidecar is configured the request is
	 * proxied to it server-side (streamed straight through, so the browser fetches same-origin from
	 * the armory and never needs CORS or the sidecar's address). On a miss the local directory
	 * baked into / mounted onto the image is used as a fallback.
	 */
	private assetRoute(subPath: string, localDir: string, assetBase: string): express.RequestHandler {
		const localStatic = express.static(localDir);
		if (!assetBase) {
			return localStatic;
		}
		return async (req: express.Request, res: express.Response, next: express.NextFunction) => {
			try {
				// Within the mount, req.url is the path after the mount point (e.g. "/1234.mo3").
				const upstream = await fetch(`${assetBase}/${subPath}${req.url}`, {
					signal: AbortSignal.timeout(30000),
				});
				if (upstream.ok && upstream.body !== null) {
					const contentType = upstream.headers.get("content-type");
					if (contentType) {
						res.setHeader("Content-Type", contentType);
					}
					const contentLength = upstream.headers.get("content-length");
					if (contentLength) {
						res.setHeader("Content-Length", contentLength);
					}
					res.setHeader("Cache-Control", "public, max-age=86400");
					Readable.fromWeb(upstream.body as import("stream/web").ReadableStream).pipe(res);
					return;
				}
				localStatic(req, res, next);
			} catch {
				localStatic(req, res, next);
			}
		};
	}

	private wrapRoute(fn: (req: express.Request, res: express.Response, next: express.NextFunction) => Promise<void>) {
		// Adds error handling for promise-based controller methods
		return async (req: express.Request, res: express.Response, next: express.NextFunction) => {
			try {
				await fn(req, res, next);
			} catch (e) {
				next(e);
			}
		};
	}

	/**
	 * Sends an error response, rendering the Handlebars error page when possible. If that render itself
	 * fails (e.g. the static web bundle / templates are missing from the image, or a helper throws), it
	 * degrades to JSON or plain text instead of falling through to Express's default handler — which
	 * would otherwise leak a raw stack trace to the client.
	 */
	private sendError(req: express.Request, res: express.Response, status: number): void {
		if (res.headersSent) {
			return;
		}
		res.status(status);

		const name = this.errorNames[status] || "Error";
		const fallback = () => {
			if (res.headersSent) {
				return;
			}
			if (req.accepts("html") || req.accepts("json")) {
				res.json({ error: name, status });
				return;
			}
			res.type("txt").send(`${status} ${name}`);
		};

		if (!req.accepts("html")) {
			fallback();
			return;
		}

		res.render("error.hbs", this.getErrorViewData(status, req), (err: Error | null, html?: string) => {
			if (err) {
				this.logger.error(`Failed to render error page for request ${req.id}: ${err.message}`);
				fallback();
				return;
			}
			res.send(html);
		});
	}

	private getErrorViewData(status: number, req: express.Request) {
		return {
			status,
			name: this.errorNames[status] || "An error occurred",
			description: this.errorDescriptions[status] || "",
			reqId: req.id,
		};
	}
}
