import * as express from "express";
import { RowDataPacket } from "mysql2/promise";

import { Armory } from "../Armory";
import { buildLayoutRenderModel } from "../LayoutWidgets";
import { IRealmConfig } from "../Config";
import { RaidDifficultyParts } from "../data/RaidTrackerCatalog";

const MaxRows = 50;

/**
 * Server-wide "Top Logs" leaderboards built on the raid_logs_tracker table of
 * mod-raid-logs-tracker: the 50 fastest clears of a dungeon/raid, or fastest kills of a
 * single boss / world boss, optionally narrowed by raid size and difficulty.
 */
export class TopRecordsController {
	private armory: Armory;

	public constructor(armory: Armory) {
		this.armory = armory;
	}

	public async index(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realms = await this.getEnabledRealms();
		if (realms.length === 0) {
			// The clear tracker module is not installed on any realm.
			return next(404);
		}

		const instances = await this.armory.raidTrackerCatalog.getInstances();
		const layoutRender = buildLayoutRenderModel("top-logs");
		res.render("top-records.hbs", {
			title: "Top Logs",
			realms: realms.map((r) => r.name),
			// Picker catalogue for the client: instance/boss ids and the difficulties each
			// instance is seeded under (used to decide which filters are applicable).
			catalog: instances.map((instance) => ({
				id: instance.id,
				content: instance.content,
				expansion: instance.expansion,
				name: instance.name,
				difficulties: instance.difficulties,
				bosses: instance.bosses.map((boss) => ({ id: boss.id, name: boss.name })),
			})),
			difficultyParts: RaidDifficultyParts,
			...layoutRender,
		});
	}

	public async data(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realms = await this.getEnabledRealms();
		const realmName = req.query.realm as string | undefined;
		const realm = realmName === undefined ? realms[0] : realms.find((r) => r.name.toLowerCase() === realmName.toLowerCase());
		if (realm === undefined) {
			return next(404);
		}

		const instanceId = parseInt(req.query.instance as string) || 0;
		const instance = await this.armory.raidTrackerCatalog.getInstance(instanceId);
		if (instance === undefined) {
			return next(400);
		}

		// A specific boss (by catalogue boss id) means a boss-kill leaderboard; otherwise the
		// full-clear leaderboard. World bosses have no enter-to-clear timer, so they always
		// resolve to their (single) boss kill record.
		let bossId = parseInt(req.query.boss as string) || 0;
		if (bossId === 0 && instance.content === "world" && instance.bosses.length > 0) {
			bossId = instance.bosses[0].id;
		}
		const boss = bossId === 0 ? null : instance.bosses.find((b) => b.id === bossId);
		if (boss === undefined) {
			return next(400);
		}

		// Optional comma-separated flat difficulty filter (resolved from the raid size /
		// difficulty pickers on the client). Absent = all difficulties.
		const difficultyParam = ((req.query.difficulty as string) ?? "").trim();
		const difficulties = difficultyParam
			.split(",")
			.map((value) => parseInt(value))
			.filter((value) => !isNaN(value) && value >= 0 && value <= 255);

		const conditions: string[] = ["`rlt`.`record_type` = ?", "`rlt`.`instance_id` = ?", "`rlt`.`best_time_ms` > 0"];
		const values: unknown[] = [boss === null ? 0 : 1, instance.id];
		if (boss !== null) {
			if (boss.npcIds.length === 0) {
				res.json({ realm: realm.name, availableDifficulties: [], rows: [] });
				return;
			}
			conditions.push("`rlt`.`boss_entry` IN (?)");
			values.push(boss.npcIds);
		}

		const availableDifficulties = await this.getAvailableDifficulties(realm, conditions, values);

		if (difficulties.length > 0) {
			conditions.push("`rlt`.`difficulty` IN (?)");
			values.push(difficulties);
		}

		const rows = await this.getLeaderboard(realm, conditions, values);
		res.json({ realm: realm.name, availableDifficulties, rows });
	}

	/** Realms on which the clear tracker module is installed, in config order. */
	private async getEnabledRealms(): Promise<IRealmConfig[]> {
		const installed = await Promise.all(this.armory.config.realms.map((realm) => this.armory.isLogsTrackerModuleInstalled(realm.name)));
		return this.armory.config.realms.filter((realm, i) => installed[i]);
	}

	/** Distinct flat difficulties with at least one record for the current selection. */
	private async getAvailableDifficulties(realm: IRealmConfig, conditions: string[], values: unknown[]): Promise<number[]> {
		const [rows] = await this.armory.getCharactersDb(realm.name).query<RowDataPacket[]>({
			sql: `SELECT DISTINCT \`rlt\`.\`difficulty\` FROM \`raid_logs_tracker\` \`rlt\` WHERE ${conditions.join(" AND ")} ORDER BY \`rlt\`.\`difficulty\``,
			values,
			timeout: this.armory.config.dbQueryTimeout,
		});
		return (rows as RowDataPacket[]).map((row) => row.difficulty as number);
	}

	private async getLeaderboard(realm: IRealmConfig, conditions: string[], values: unknown[]) {
		// Same GM-hiding rule as the character pages: characters on GM accounts are excluded
		// from the leaderboard when hideGameMasters is enabled.
		const [rows] = await this.armory.getCharactersDb(realm.name).query<RowDataPacket[]>({
			sql: `
				SELECT
					\`rlt\`.\`difficulty\`, \`rlt\`.\`scope\`, \`rlt\`.\`best_time_ms\`, \`rlt\`.\`completions\`, \`rlt\`.\`last_seen\`,
					\`characters\`.\`name\`, \`characters\`.\`race\`, \`characters\`.\`class\`, \`characters\`.\`level\`,
					\`guild\`.\`name\` AS \`guild\`
				FROM \`raid_logs_tracker\` \`rlt\`
				JOIN \`characters\` ON \`characters\`.\`guid\` = \`rlt\`.\`player_guid\`
				LEFT JOIN \`guild_member\` ON \`guild_member\`.\`guid\` = \`characters\`.\`guid\`
				LEFT JOIN \`guild\` ON \`guild\`.\`guildid\` = \`guild_member\`.\`guildid\`
				LEFT JOIN \`${realm.authDatabase}\`.\`account_access\` ON \`account_access\`.\`id\` = \`characters\`.\`account\`
					AND \`account_access\`.\`RealmID\` IN (-1, ${realm.realmId}) AND \`account_access\`.\`gmlevel\` > 0
				WHERE ${conditions.join(" AND ")}
					AND (\`account_access\`.\`id\` IS NULL OR ? = 0)
				ORDER BY \`rlt\`.\`best_time_ms\` ASC, \`rlt\`.\`last_seen\` ASC
				LIMIT ${MaxRows}
			`,
			values: [...values, this.armory.config.hideGameMasters ? 1 : 0],
			timeout: this.armory.config.dbQueryTimeout,
		});

		return (rows as RowDataPacket[]).map((row) => ({
			name: row.name,
			guild: row.guild ?? null,
			race: row.race,
			class: row.class,
			level: row.level,
			difficulty: row.difficulty,
			scope: row.scope,
			bestTimeMs: row.best_time_ms,
			completions: row.completions,
			lastSeen: row.last_seen,
		}));
	}
}
