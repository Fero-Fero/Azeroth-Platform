import * as express from "express";
import { RowDataPacket } from "mysql2/promise";

import { Armory } from "../Armory";
import { IRealmConfig } from "../Config";

// Continent map ids used as "open world" point maps; everything else is an instance.
const PointMaps = [0, 1, 530, 571, 609];
// Bit masks (1 << (race-1)) for the two factions, matching the legacy playermap.
const HordeRaces = 0x2b2;
const AllianceRaces = 0x44d;
const OutlandInstances = [540, 542, 543, 544, 545, 546, 547, 548, 550, 552, 553, 554, 555, 556, 557, 558, 559, 562, 564, 565];
const NorthrendInstances = [533, 574, 575, 576, 578, 599, 600, 601, 602, 603, 604, 608, 615, 616, 617, 619, 624, 631, 632, 649, 650, 658, 668, 724];
const MapsCount = 3; // Azeroth, Outland, Northrend

interface IOnlineRow {
	guid: number;
	account: number;
	name: string;
	class: number;
	race: number;
	level: number;
	gender: number;
	position_x: number;
	position_y: number;
	map: number;
	zone: number;
	extra_flags: number;
}

interface IMapPlayer {
	x: number;
	y: number;
	dead: number;
	name: string;
	map: number;
	zone: string;
	cl: number;
	race: number;
	level: number;
	gender: number;
	Extention: number;
	leaderGuid: number;
}

export class MapController {
	private armory: Armory;
	private zoneNameById: { [key: number]: string };

	public constructor(armory: Armory) {
		this.armory = armory;
		this.zoneNameById = {};
	}

	public async load(): Promise<void> {
		this.zoneNameById = {};
		for await (const area of this.armory.dbc.areas()) {
			this.zoneNameById[area.id] = area.zoneName;
		}
	}

	public async map(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		res.render("map.hbs", {
			title: "Azeroth",
			page: "map",
		});
	}

	public async mapData(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realm = this.armory.config.realms[0];
		if (realm === undefined) {
			res.json({ online: null, status: { online: 2 } });
			return;
		}

		const db = this.armory.getCharactersDb(realm.name);

		// GM accounts are hidden from the map (privacy + matches hideGameMasters intent).
		const gmAccounts = new Set<number>();
		try {
			const [gmRows] = await db.query<RowDataPacket[]>({
				sql: `SELECT \`id\` FROM \`${realm.authDatabase}\`.\`account_access\` WHERE \`gmlevel\` > 0 AND \`RealmID\` IN (-1, ?)`,
				values: [realm.realmId],
				timeout: this.armory.config.dbQueryTimeout,
			});
			for (const row of gmRows as RowDataPacket[]) {
				gmAccounts.add(row.id);
			}
		} catch {
			// account_access may be absent on non-AzerothCore cores; treat as no GMs.
		}

		const [onlineRows] = await db.query<RowDataPacket[]>({
			sql: `
				SELECT \`guid\`, \`account\`, \`name\`, \`class\`, \`race\`, \`level\`, \`gender\`,
					\`position_x\`, \`position_y\`, \`map\`, \`zone\`, \`extra_flags\`
				FROM \`characters\`
				WHERE \`online\` = 1
				ORDER BY \`name\`
			`,
			timeout: this.armory.config.dbQueryTimeout,
		});
		const online = onlineRows as RowDataPacket[] as IOnlineRow[];

		// Resolve group leaders so instanced party members can be grouped in tooltips.
		const leaderByGuid: { [key: number]: number } = {};
		if (online.length > 0) {
			try {
				const [groupRows] = await db.query<RowDataPacket[]>({
					sql: `
						SELECT gm.\`memberGuid\` AS memberGuid, g.\`leaderGuid\` AS leaderGuid
						FROM \`group_member\` gm
						JOIN \`groups\` g ON g.\`guid\` = gm.\`guid\`
						WHERE gm.\`memberGuid\` IN (?)
					`,
					values: [online.map((r) => r.guid)],
					timeout: this.armory.config.dbQueryTimeout,
				});
				for (const row of groupRows as RowDataPacket[]) {
					leaderByGuid[row.memberGuid] = row.leaderGuid;
				}
			} catch {
				// group tables differ between cores; grouping is optional.
			}
		}

		const counts: number[][] = [];
		for (let i = 0; i < MapsCount; i++) {
			counts.push([0, 0]);
		}

		const players: IMapPlayer[] = [];
		for (const row of online) {
			if (gmAccounts.has(row.account)) {
				continue;
			}

			let extention = 0;
			if ((row.map === 530 && row.position_y > -1000) || OutlandInstances.includes(row.map)) {
				extention = 1;
			} else if (row.map === 571 || NorthrendInstances.includes(row.map)) {
				extention = 2;
			}

			const raceBit = 0x1 << (row.race - 1);
			if (HordeRaces & raceBit) {
				counts[extention][1]++;
			} else if (AllianceRaces & raceBit) {
				counts[extention][0]++;
			}

			players.push({
				x: row.position_x,
				y: row.position_y,
				dead: 0,
				name: row.name,
				map: row.map,
				zone: this.getZoneName(row.zone),
				cl: row.class,
				race: row.race,
				level: row.level,
				gender: row.gender,
				Extention: extention,
				leaderGuid: leaderByGuid[row.guid] ?? 0,
			});
		}

		players.sort((a, b) => {
			if (a.leaderGuid === b.leaderGuid) {
				return a.name.localeCompare(b.name);
			}
			return a.leaderGuid < b.leaderGuid ? -1 : 1;
		});

		const status = await this.getStatus(realm, db);

		res.json({
			online: [...counts, ...players],
			status,
		});
	}

	private async getStatus(realm: IRealmConfig, db: ReturnType<Armory["getCharactersDb"]>): Promise<unknown> {
		try {
			const [rows] = await db.query<RowDataPacket[]>({
				sql: `
					SELECT UNIX_TIMESTAMP() AS now, \`starttime\`, \`maxplayers\`
					FROM \`${realm.authDatabase}\`.\`uptime\`
					WHERE \`starttime\` = (SELECT MAX(\`starttime\`) FROM \`${realm.authDatabase}\`.\`uptime\`)
				`,
				timeout: this.armory.config.dbQueryTimeout,
			});
			const row = (rows as RowDataPacket[])[0];
			if (row === undefined) {
				return { online: 0, uptime: 0, maxplayers: 0, gmonline: 0 };
			}
			return {
				online: 1,
				uptime: Number(row.now) - Number(row.starttime),
				maxplayers: row.maxplayers,
				gmonline: 0,
			};
		} catch {
			return { online: 0, uptime: 0, maxplayers: 0, gmonline: 0 };
		}
	}

	private getZoneName(zoneId: number): string {
		return this.zoneNameById[zoneId] || "Unknown zone";
	}
}
