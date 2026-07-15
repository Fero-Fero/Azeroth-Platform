import * as express from "express";
import { encode } from "html-entities";
import { RowDataPacket } from "mysql2/promise";

import { Armory } from "../Armory";
import { buildLayoutRenderModel } from "../LayoutWidgets";
import { IRealmConfig } from "../Config";
import { DataTablesSsp } from "../DataTablesSsp";
import { Utils, IEmblem, IEmblemSource, EFaction } from "../Utils";

interface IGuildRank {
	id: number;
	name: string;
}

interface IGuildData {
	id: number;
	name: string;
	leader: string;
	faction: EFaction;
	emblem: IEmblem;
	membersCount: number;
}

export class GuildController {
	private armory: Armory;

	public constructor(armory: Armory) {
		this.armory = armory;
	}

	public async guild(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const guildName = req.params.name as string;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			// Could not find realm
			return next(404);
		}

		const guildData = await this.getGuildData(realm, guildName);
		if (guildData === null) {
			// Could not find guild
			return next(404);
		}

		const layoutRender = buildLayoutRenderModel("guild");
		res.render("guild.hbs", {
			title: `${guildName}`,
			realm: realm.name,
			...guildData,
			...layoutRender,
		});
	}

	public async members(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const guildId = parseInt(req.params.guild as string);

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			// Could not find realm
			return next(404);
		}

		if (isNaN(guildId) || !(await this.guildExists(realm, guildId))) {
			// Could not find guild
			return next(404);
		}

		const db = this.armory.getCharactersDb(realm.name);
		const charSet = await this.armory.getDatabaseCharset(realm.name);

		let ssp = new DataTablesSsp(req.query, db, "guild_member", "guid", [
			{ name: "name", table: "characters", collation: `${charSet}_general_ci` },
			{ name: "rank" },
			{ name: "level", table: "characters" },
			{ name: "class", table: "characters", formatter: (cls) => Utils.classNames[cls] },
			{ name: "race", table: "characters", formatter: (race, row) => `${Utils.raceNames[race]}_${row[6] === 0 ? "male" : "female"}` },
			{ name: "online", table: "characters", formatter: (online) => online === 1 },
		]);
		ssp.joins = [{ table1: "guild_member", column1: "guid", table2: "characters", column2: "guid", kind: "LEFT" }];
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

		const result = await ssp.where("`guildid` = ?", guildId).where("`deleteInfos_Account` IS NULL").run(this.armory.config.dbQueryTimeout);

		const ranks = await this.getGuildRanks(realm, guildId);
		const ranksObj: { [key: number]: string } = {};
		for (const rank of ranks) {
			ranksObj[rank.id] = encode(rank.name);
		}

		res.json({
			...result,
			ranks: ranksObj,
		});
	}

	private async getGuildData(realm: IRealmConfig, name: string): Promise<IGuildData | null> {
		const guildId = await this.getGuildId(realm, name);
		if (guildId === null) {
			return null;
		}
		return this.getGuildDataById(realm, guildId);
	}

	private async getGuildDataById(realm: IRealmConfig, guildId: number): Promise<IGuildData | null> {
		const db = this.armory.getCharactersDb(realm.name);
		const [rows] = await db.query<RowDataPacket[]>({
			sql: `
				SELECT guildid, name, leaderguid, EmblemStyle AS emblemStyle, EmblemColor AS emblemColor, BorderStyle AS borderStyle, BorderColor AS borderColor, BackgroundColor AS background
				FROM guild WHERE guildid = ?
			`,
			values: [guildId],
			timeout: this.armory.config.dbQueryTimeout,
		});
		if (rows.length === 0) {
			return null;
		}
		const guild = rows[0];

		const [leaderRows] = await db.query<RowDataPacket[]>({
			sql: "SELECT name, race FROM characters WHERE guid = ? LIMIT 1",
			values: [guild.leaderguid],
			timeout: this.armory.config.dbQueryTimeout,
		});
		const leader = leaderRows[0];

		const [countRows] = await db.query<RowDataPacket[]>({
			sql: "SELECT COUNT(guid) AS `count` FROM guild_member WHERE guildid = ?",
			values: [guild.guildid],
			timeout: this.armory.config.dbQueryTimeout,
		});
		const membersCount = countRows[0]?.count ?? 0;

		return {
			id: guild.guildid,
			name: guild.name,
			leader: leader?.name ?? "Unknown",
			faction: leader ? Utils.getFactionFromRaceId(leader.race) : EFaction.Horde,
			emblem: Utils.makeEmblemObject(guild as unknown as IEmblemSource),
			membersCount,
		};
	}

	private async getGuildId(realm: IRealmConfig, name: string): Promise<number | null> {
		const db = this.armory.getCharactersDb(realm.name);
		const [rows] = await db.query<RowDataPacket[]>({
			sql: `
				SELECT guildid
				FROM guild WHERE name = ?
			`,
			values: [name],
			timeout: this.armory.config.dbQueryTimeout,
		});
		if ((rows as RowDataPacket[]).length === 0) {
			return null;
		}

		return rows[0].guildid;
	}

	private async guildExists(realm: IRealmConfig, id: number): Promise<boolean> {
		const db = this.armory.getCharactersDb(realm.name);
		const [rows] = await db.query<RowDataPacket[]>({
			sql: "SELECT guildid FROM guild WHERE guildid = ? LIMIT 1",
			values: [id],
			timeout: this.armory.config.dbQueryTimeout,
		});

		return rows.length > 0;
	}

	private async getGuildRanks(realm: IRealmConfig, id: number): Promise<IGuildRank[]> {
		const db = this.armory.getCharactersDb(realm.name);
		const [rows] = await db.query<RowDataPacket[]>({
			sql: `
				SELECT rid AS id, rname AS name
				FROM guild_rank WHERE guildid = ?
			`,
			values: [id],
			timeout: this.armory.config.dbQueryTimeout,
		});

		return rows as IGuildRank[];
	}
}
