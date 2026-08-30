import * as express from "express";
import { RowDataPacket } from "mysql2/promise";

import { Utils } from "../Utils";
import { Armory } from "../Armory";
import { DataTablesSsp } from "../DataTablesSsp";
import { PlatformController } from "./PlatformController";
import { buildLayoutRenderModel } from "../LayoutWidgets";
import { loadPageLayout, maxNewsLimit, maxRecentLimit } from "../ArmoryLayout";

export class IndexController {
	private armory: Armory;
	private platform: PlatformController;

	public constructor(armory: Armory, platform: PlatformController) {
		this.armory = armory;
		this.platform = platform;
	}

	public async index(req: express.Request, res: express.Response): Promise<void> {
		const homePage = loadPageLayout("home");
		const newsLimit = maxNewsLimit(homePage);
		const recentLimit = maxRecentLimit(homePage);
		const news = await this.platform.getNews(newsLimit);
		const recentCharacters = await this.getRecentCharacters(recentLimit);
		const layoutRender = buildLayoutRenderModel("home", { news, recentCharacters });

		res.render("index.hbs", {
			title: "Azeroth Armory",
			realms: this.armory.config.realms.map((r) => r.name),
			defaultRealm: this.armory.config.realms[0]?.name ?? "",
			news,
			recentCharacters,
			...layoutRender,
		});
	}

	private async getRecentCharacters(
		limit = 5,
	): Promise<
		{ realm: string; name: string; level: number; classIcon: string; raceIcon: string; guild: string | null }[]
	> {
		const realm = this.armory.config.realms[0];
		if (realm === undefined) {
			return [];
		}

		const gmFilter = this.armory.config.hideGameMasters ? "AND `aa`.`id` IS NULL" : "";
		try {
			const [rows] = await this.armory.getCharactersDb(realm.name).query<RowDataPacket[]>({
				sql: `
					SELECT \`c\`.\`name\`, \`c\`.\`level\`, \`c\`.\`class\`, \`c\`.\`race\`, \`c\`.\`gender\`, \`g\`.\`name\` AS \`guild\`
					FROM \`characters\` \`c\`
					LEFT JOIN \`guild_member\` \`gm\` ON \`gm\`.\`guid\` = \`c\`.\`guid\`
					LEFT JOIN \`guild\` \`g\` ON \`g\`.\`guildid\` = \`gm\`.\`guildid\`
					LEFT JOIN \`${realm.authDatabase}\`.\`account\` \`a\` ON \`a\`.\`id\` = \`c\`.\`account\`
					LEFT JOIN \`${realm.authDatabase}\`.\`account_access\` \`aa\` ON \`aa\`.\`id\` = \`c\`.\`account\` AND \`aa\`.\`RealmID\` IN (-1, ${realm.realmId}) AND \`aa\`.\`gmlevel\` > 0
					WHERE \`c\`.\`level\` >= 1
						AND \`c\`.\`deleteInfos_Account\` IS NULL
						AND \`a\`.\`username\` != 'AHBOT'
						${gmFilter}
					ORDER BY \`c\`.\`logout_time\` DESC
					LIMIT ${Math.max(1, Math.min(limit, 20))}
				`,
				timeout: this.armory.config.dbQueryTimeout,
			});

			return (rows as RowDataPacket[]).map((row) => ({
				realm: realm.name,
				name: row.name,
				level: row.level,
				classIcon: Utils.classNames[row.class],
				raceIcon: `${Utils.raceNames[row.race]}_${row.gender === 0 ? "male" : "female"}`,
				guild: row.guild,
			}));
		} catch (err) {
			this.armory.logger.warn(`Could not load recent characters: ${err}`);
			return [];
		}
	}

	// Lightweight typeahead endpoint for the navbar search box. Returns a short list
	// of matching characters (name prefix) as JSON, applying the same bot/GM filters
	// as the "Recently Active" list.
	public async searchSuggest(req: express.Request, res: express.Response): Promise<void> {
		const query = ((req.query.q as string) ?? "").trim();
		const realmName = req.query.realm as string;
		const realm = realmName === undefined || realmName === "" ? this.armory.config.realms[0] : this.armory.config.realms.find((r) => r.name === realmName);

		if (realm === undefined || query.length < 2) {
			res.json({ realm: realm?.name ?? "", results: [] });
			return;
		}

		// Escape LIKE wildcards in the user input so they're matched literally.
		const prefix = query.replace(/[%_\\]/g, (c) => `\\${c}`);
		const gmFilter = this.armory.config.hideGameMasters ? "AND `aa`.`id` IS NULL" : "";

		try {
			// Force a case-insensitive comparison via a *_general_ci collation (the character name
			// column's own collation may be case-sensitive, e.g. *_bin). Matches the overview search.
			const charSet = await this.armory.getDatabaseCharset(realm.name);
			const [rows] = await this.armory.getCharactersDb(realm.name).query<RowDataPacket[]>({
				sql: `
					SELECT \`c\`.\`name\`, \`c\`.\`level\`, \`c\`.\`class\`, \`c\`.\`race\`, \`c\`.\`gender\`, \`g\`.\`name\` AS \`guild\`
					FROM \`characters\` \`c\`
					LEFT JOIN \`guild_member\` \`gm\` ON \`gm\`.\`guid\` = \`c\`.\`guid\`
					LEFT JOIN \`guild\` \`g\` ON \`g\`.\`guildid\` = \`gm\`.\`guildid\`
					LEFT JOIN \`${realm.authDatabase}\`.\`account\` \`a\` ON \`a\`.\`id\` = \`c\`.\`account\`
					LEFT JOIN \`${realm.authDatabase}\`.\`account_access\` \`aa\` ON \`aa\`.\`id\` = \`c\`.\`account\` AND \`aa\`.\`RealmID\` IN (-1, ${realm.realmId}) AND \`aa\`.\`gmlevel\` > 0
					WHERE \`c\`.\`deleteInfos_Account\` IS NULL
						AND \`c\`.\`name\` COLLATE ${charSet}_general_ci LIKE ? ESCAPE '\\\\'
						AND \`a\`.\`username\` != 'AHBOT'
						${gmFilter}
					ORDER BY \`c\`.\`name\` ASC
					LIMIT 8
				`,
				values: [`${prefix}%`],
				timeout: this.armory.config.dbQueryTimeout,
			});

			res.json({
				realm: realm.name,
				results: (rows as RowDataPacket[]).map((row) => ({
					realm: realm.name,
					name: row.name,
					level: row.level,
					classIcon: Utils.classNames[row.class],
					raceIcon: `${Utils.raceNames[row.race]}_${row.gender === 0 ? "male" : "female"}`,
					guild: row.guild,
				})),
			});
		} catch (err) {
			this.armory.logger.warn(`Could not run search suggestions: ${err}`);
			res.json({ realm: realm.name, results: [] });
		}
	}

	public async search(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.query.realm as string;
		const realm =
			realmName === undefined || realmName === ""
				? this.armory.config.realms[0]
				: this.armory.config.realms.find((r) => r.name === realmName);
		if (realm === undefined) {
			return next(400);
		}

		const db = this.armory.getCharactersDb(realm.name);
		const charSet = await this.armory.getDatabaseCharset(realm.name);

		let ssp = new DataTablesSsp(req.query, db, "characters", "guid", [
			{ name: "name", collation: `${charSet}_general_ci` },
			{ name: "online", formatter: (online) => online === 1 },
			{ name: "level" },
			{ name: "class", formatter: (cls) => Utils.classNames[cls] },
			{ name: "race", formatter: (race, row) => `${Utils.raceNames[race]}_${row[10] === 0 ? "male" : "female"}` },
			{ table: "guild", name: "name" },
			{ name: "money" },
			{ name: "totaltime" },
			{ name: "zone" },
			{
                name: "exploredZones",
                formatter: (exploredZones: string)=> exploredZones.trim()
                    .split(' ')
                    .map((n: string) => parseInt(n, 10))
                    .reduce((acc, n) => {
                        n = n - ((n >> 1) & 0x55555555);
                        n = (n & 0x33333333) + ((n >> 2) & 0x33333333);

                        const newZones = ((n + (n >> 4) & 0xF0F0F0F) * 0x1010101) >> 24;

                        return acc + newZones;
                    }, 0),
            },
		]);
		ssp.joins = [
			{ table1: "characters", column1: "guid", table2: "guild_member", column2: "guid", kind: "LEFT" },
			{ table1: "guild_member", column1: "guildid", table2: "guild", column2: "guildid", kind: "LEFT" },
		];
		ssp.extraDataColumns = ["`characters`.`gender`"];

		if (this.armory.config.hideGameMasters) {
			ssp.joins.push({
				table1: "characters",
				column1: "account",
				table2: "account_access",
				column2: "id",
				database2: realm.authDatabase,
				kind: "LEFT",
				where: `AND \`account_access\`.\`RealmID\` IN (-1, ${realm.realmId}) AND \`account_access\`.\`gmlevel\` > 0`,
			});
			ssp = ssp.where("`account_access`.`id` IS NULL");
		}

        ssp.joins.push({
            table1: "characters",
            column1: "account",
            table2: "account",
            column2: "id",
            database2: realm.authDatabase,
            kind: "LEFT",
        });
        ssp = ssp.where("`account`.`username` != 'AHBOT'");

		const result = await ssp.where("`deleteInfos_Account` IS NULL").run(this.armory.config.dbQueryTimeout);

		res.json({
			...result,
			realm: realm.name,
		});
	}
}
