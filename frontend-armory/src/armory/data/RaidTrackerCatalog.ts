import * as fs from "fs";
import * as path from "path";
import { RowDataPacket } from "mysql2/promise";

import type { Armory } from "../Armory";

export type TrackerContent = "dungeon" | "raid" | "world";
export type TrackerExpansion = "classic" | "tbc" | "wotlk";

export interface ITrackerBoss {
	id: number;
	name: string;
	orderIndex: number;
	isFinal: boolean;
	npcIds: number[];
}

export interface ITrackerInstance {
	id: number;
	content: TrackerContent;
	expansion: TrackerExpansion;
	mapId: number;
	dungeonVersion: string;
	key: string;
	name: string;
	/** Flat-scale difficulties (0=5N 1=5HC 2=10N 3=20N 4=25N 5=40N 6=10HC 7=25HC) the instance is tracked under. */
	difficulties: number[];
	/** Public URL of the instance artwork, or null when no image matches. */
	image: string | null;
	bosses: ITrackerBoss[];
	/**
	 * Single-difficulty instances only: whether a kill must be recorded on exactly that
	 * difficulty to count. True when another instance shares the same map and content type
	 * with a different difficulty set (e.g. Classic vs WotLK Naxxramas share map 533 and
	 * boss entries and can only be told apart by the recorded difficulty).
	 */
	exactDifficultyMatch: boolean;
}

/**
 * A display variant of an instance. Multi-difficulty instances (e.g. WotLK raids) become
 * one card per difficulty; single-difficulty instances are one unlabelled card.
 */
export interface ITrackerVariant {
	/** Difficulty shown on the card, or null for unlabelled single cards. */
	difficulty: number | null;
	/** Difficulty a kill/clear must be recorded on to count, or null to accept any. */
	matchDifficulty: number | null;
}

/** Unified difficulty scale recorded by the raid tracker modules. */
export const RaidDifficultyLabel: { [key: number]: string } = {
	0: "Normal",
	1: "Heroic",
	2: "10 Normal",
	3: "20 Normal",
	4: "25 Normal",
	5: "40 Normal",
	6: "10 Heroic",
	7: "25 Heroic",
};

/** Decomposition of the flat difficulty scale into group size and heroic mode. */
export const RaidDifficultyParts: { [key: number]: { size: number; heroic: boolean } } = {
	0: { size: 5, heroic: false },
	1: { size: 5, heroic: true },
	2: { size: 10, heroic: false },
	3: { size: 20, heroic: false },
	4: { size: 25, heroic: false },
	5: { size: 40, heroic: false },
	6: { size: 10, heroic: true },
	7: { size: 25, heroic: true },
};

const ContentByCode: { [key: number]: TrackerContent } = { 0: "dungeon", 1: "raid", 2: "world" };
const ExpansionByCode: { [key: number]: TrackerExpansion } = { 0: "classic", 1: "tbc", 2: "wotlk" };
const TrackerContents: TrackerContent[] = ["dungeon", "raid", "world"];
const TrackerExpansions: TrackerExpansion[] = ["classic", "tbc", "wotlk"];
const ContentIdBase: { [key in TrackerContent]: number } = { dungeon: 1000, raid: 2000, world: 3000 };
const ContentTypeCode: { [key in TrackerContent]: number } = { dungeon: 0, raid: 1, world: 2 };
const ExpansionCode: { [key in TrackerExpansion]: number } = { classic: 0, tbc: 1, wotlk: 2 };
const Classic20ManRaids = new Set(["zulgurub", "ruinsofahnqiraj"]);
const VersionAbbrev: { [key: string]: string } = {
	lowerblackrockspire: "lbrs",
	upperblackrockspire: "ubrs",
};

interface IProgressionJsonBoss {
	name: string;
	npcIds: number[];
}

interface IProgressionJsonInstance {
	key: string;
	name: string;
	mapId: number;
	image?: string;
	difficulties?: number[];
	matchDifficulty?: number;
	bosses: IProgressionJsonBoss[];
}

type ProgressionJsonSection = { [key in TrackerExpansion]?: IProgressionJsonInstance[] };

// The curated artwork under progression/ (uploaded via armory.data.zip or baked into static/data/progression)
// is keyed by loosely snake_cased display names. Most files match an instance's key or name once both are
// normalized (lowercased, non-alphanumerics stripped); these are the historical exceptions.
const ImageAliases: { [instanceKey: string]: string } = {
	wailingcaverns: "wailingcavern",
	stormwindstockade: "thestockades",
	scarletmonastery: "scarletmonastary",
	theslavepens: "theslavespens",
};

function normalizeImageKey(value: string): string {
	return value.toLowerCase().replace(/[^a-z0-9]/g, "");
}

/**
 * The shared "what to track" catalogue of the raid tracker modules
 * (mod-raid-progression-tracker / mod-raid-logs-tracker), loaded from the world database
 * tables `raid_tracker_instance`, `raid_tracker_instance_difficulty`, `raid_tracker_boss`
 * and `raid_tracker_boss_npc`. Both modules ship the same seed SQL, so the catalogue is
 * present whenever either module is installed.
 *
 * Loading is lazy: a successful load (including "tables absent") is cached for the process
 * lifetime, while transient DB errors are retried on the next request so an armory that
 * started before MySQL was ready heals itself.
 */
export class RaidTrackerCatalog {
	private armory: Armory;
	private loadPromise: Promise<void> | null = null;
	private loaded = false;

	private instances: ITrackerInstance[] = [];
	private instancesById: Map<number, ITrackerInstance> = new Map();

	public constructor(armory: Armory) {
		this.armory = armory;
	}

	private async ensureLoaded(): Promise<void> {
		if (this.loaded) {
			return;
		}
		if (this.loadPromise === null) {
			this.loadPromise = this.loadCatalogue()
				.then(() => {
					this.loaded = true;
				})
				.catch((err) => {
					this.loadPromise = null;
					this.armory.logger.warn(`Could not load the raid tracker catalogue from the world database: ${err}`);
				});
		}
		await this.loadPromise;
	}

	/** All instances in catalogue order. Empty when no tracker module is installed. */
	public async getInstances(): Promise<ITrackerInstance[]> {
		await this.ensureLoaded();
		return this.instances;
	}

	public async getInstance(id: number): Promise<ITrackerInstance | undefined> {
		await this.ensureLoaded();
		return this.instancesById.get(id);
	}

	public async getSection(content: TrackerContent, expansion: TrackerExpansion): Promise<ITrackerInstance[]> {
		await this.ensureLoaded();
		return this.instances.filter((i) => i.content === content && i.expansion === expansion);
	}

	/** Display variants of an instance: one card per difficulty, or one unlabelled card. */
	public static getVariants(instance: ITrackerInstance): ITrackerVariant[] {
		if (instance.difficulties.length > 1) {
			return instance.difficulties.map((d) => ({ difficulty: d, matchDifficulty: d }));
		}
		return [
			{
				difficulty: null,
				matchDifficulty: instance.exactDifficultyMatch && instance.difficulties.length === 1 ? instance.difficulties[0] : null,
			},
		];
	}

	private async loadCatalogue(): Promise<void> {
		const worldDb = this.armory.worldDb;
		const timeout = this.armory.config.dbQueryTimeout;

		const [probe] = await worldDb.query<RowDataPacket[]>({
			sql:
				"SELECT 1 FROM `information_schema`.`tables` " +
				"WHERE `table_schema` = DATABASE() AND `table_name` = 'raid_tracker_instance' LIMIT 1",
			timeout,
		});
		if ((probe as RowDataPacket[]).length === 0) {
			await this.loadFromJsonFallback("world database raid_tracker_instance table is absent");
			return;
		}

		const [instanceRows] = await worldDb.query<RowDataPacket[]>({
			sql: "SELECT `id`, `content_type`, `expansion`, `map_id`, `dungeon_version`, `instance_key`, `name` FROM `raid_tracker_instance` ORDER BY `id`",
			timeout,
		});
		if ((instanceRows as RowDataPacket[]).length === 0) {
			await this.loadFromJsonFallback("world database raid_tracker_instance table is empty");
			return;
		}
		const [difficultyRows] = await worldDb.query<RowDataPacket[]>({
			sql: "SELECT `instance_id`, `difficulty` FROM `raid_tracker_instance_difficulty` ORDER BY `instance_id`, `difficulty`",
			timeout,
		});
		const [bossRows] = await worldDb.query<RowDataPacket[]>({
			sql: "SELECT `id`, `instance_id`, `name`, `order_index`, `is_final` FROM `raid_tracker_boss` ORDER BY `instance_id`, `order_index`",
			timeout,
		});
		const [npcRows] = await worldDb.query<RowDataPacket[]>({
			sql: "SELECT `boss_id`, `npc_entry` FROM `raid_tracker_boss_npc`",
			timeout,
		});

		const npcsByBoss = new Map<number, number[]>();
		for (const row of npcRows as RowDataPacket[]) {
			const list = npcsByBoss.get(row.boss_id) ?? [];
			list.push(row.npc_entry);
			npcsByBoss.set(row.boss_id, list);
		}

		const difficultiesByInstance = new Map<number, number[]>();
		for (const row of difficultyRows as RowDataPacket[]) {
			const list = difficultiesByInstance.get(row.instance_id) ?? [];
			list.push(row.difficulty);
			difficultiesByInstance.set(row.instance_id, list);
		}

		const bossesByInstance = new Map<number, ITrackerBoss[]>();
		for (const row of bossRows as RowDataPacket[]) {
			const list = bossesByInstance.get(row.instance_id) ?? [];
			list.push({
				id: row.id,
				name: row.name,
				orderIndex: row.order_index,
				isFinal: row.is_final === 1,
				npcIds: npcsByBoss.get(row.id) ?? [],
			});
			bossesByInstance.set(row.instance_id, list);
		}

		const imageIndex = await this.buildImageIndex();

		const instances: ITrackerInstance[] = [];
		for (const row of instanceRows as RowDataPacket[]) {
			const content = ContentByCode[row.content_type];
			const expansion = ExpansionByCode[row.expansion];
			if (content === undefined || expansion === undefined) {
				continue;
			}
			instances.push({
				id: row.id,
				content,
				expansion,
				mapId: row.map_id,
				dungeonVersion: row.dungeon_version,
				key: row.instance_key,
				name: row.name,
				difficulties: difficultiesByInstance.get(row.id) ?? [],
				image: this.resolveImage(imageIndex, content, expansion, row.instance_key, row.name),
				bosses: bossesByInstance.get(row.id) ?? [],
				exactDifficultyMatch: false,
			});
		}

		// Instances that share a map with a differently-tuned sibling (Classic vs WotLK
		// Naxxramas/Onyxia) can only be told apart by the recorded difficulty, so their
		// kills must match exactly. Instances alone on their map (or sharing it with an
		// identically-tuned sibling like LBRS/UBRS) accept a kill on any difficulty.
		for (const instance of instances) {
			if (instance.difficulties.length !== 1) {
				continue;
			}
			instance.exactDifficultyMatch = instances.some(
				(other) =>
					other.id !== instance.id &&
					other.mapId === instance.mapId &&
					other.content === instance.content &&
					(other.difficulties.length !== instance.difficulties.length ||
						other.difficulties.some((d) => !instance.difficulties.includes(d))),
			);
		}

		this.instances = instances;
		this.instancesById = new Map(instances.map((i) => [i.id, i]));
		this.armory.logger.info(`Loaded raid tracker catalogue: ${instances.length} instances.`);
	}

	/**
	 * Fallback catalogue used when a realm has the character tracker tables but the shared world
	 * `raid_tracker_*` catalogue has not been installed yet. It preserves the old JSON ordering and
	 * shape, preferring an uploaded progression/*.json bundle from armory.data.zip over local defaults.
	 */
	private async loadFromJsonFallback(reason: string): Promise<void> {
		const imageIndex = await this.buildImageIndex();
		const sectionsByContent = new Map<TrackerContent, ProgressionJsonSection>();

		for (const content of TrackerContents) {
			const section = await this.readJsonSection(content);
			if (section !== null) {
				sectionsByContent.set(content, section);
			}
		}

		const instances: ITrackerInstance[] = [];
		for (const content of TrackerContents) {
			const section = sectionsByContent.get(content);
			if (section === undefined) {
				continue;
			}
			let counter = 0;
			for (const expansion of TrackerExpansions) {
				for (const entry of section[expansion] ?? []) {
					counter++;
					const instanceId = ContentIdBase[content] + counter;
					const bosses = entry.bosses.map((boss, i) => ({
						id: instanceId * 100 + i + 1,
						name: boss.name,
						orderIndex: i + 1,
						isFinal: i === entry.bosses.length - 1,
						npcIds: boss.npcIds,
					}));
					instances.push({
						id: instanceId,
						content,
						expansion,
						mapId: entry.mapId,
						dungeonVersion: "base",
						key: entry.key,
						name: entry.name,
						difficulties: this.resolveJsonDifficulties(content, entry),
						image: this.resolveImage(imageIndex, content, expansion, entry.key, entry.name),
						bosses,
						exactDifficultyMatch: entry.matchDifficulty !== undefined,
					});
				}
			}
		}

		this.assignJsonDungeonVersions(instances);
		this.markSharedMapExactDifficulty(instances);
		this.instances = instances;
		this.instancesById = new Map(instances.map((i) => [i.id, i]));
		this.armory.logger.warn(`Using progression JSON fallback catalogue (${reason}); loaded ${instances.length} instances.`);
	}

	private async readJsonSection(content: TrackerContent): Promise<ProgressionJsonSection | null> {
		const assetBase = (this.armory.config.assetProxyUrl ?? "").replace(/\/+$/, "");
		if (assetBase) {
			try {
				const res = await fetch(`${assetBase}/progression/${content}.json`, { signal: AbortSignal.timeout(15000) });
				if (res.ok) {
					return JSON.parse(await res.text()) as ProgressionJsonSection;
				}
			} catch {
				// Fall back to local bundled JSON.
			}
		}

		const localPath = path.join(process.cwd(), "static", "data", "progression", `${content}.json`);
		try {
			return JSON.parse(fs.readFileSync(localPath, "utf8")) as ProgressionJsonSection;
		} catch {
			return null;
		}
	}

	private resolveJsonDifficulties(content: TrackerContent, entry: IProgressionJsonInstance): number[] {
		if (entry.difficulties !== undefined && entry.difficulties.length > 0) {
			return [...new Set(entry.difficulties)];
		}
		if (entry.matchDifficulty !== undefined) {
			return [entry.matchDifficulty];
		}
		if (content === "world") {
			return [0];
		}
		if (content === "dungeon") {
			return [0];
		}
		if (Classic20ManRaids.has(entry.key)) {
			return [3];
		}
		return [5];
	}

	private assignJsonDungeonVersions(instances: ITrackerInstance[]): void {
		const byMap = new Map<number, ITrackerInstance[]>();
		for (const instance of instances) {
			if (instance.content === "world") {
				continue;
			}
			const list = byMap.get(instance.mapId) ?? [];
			list.push(instance);
			byMap.set(instance.mapId, list);
		}

		for (const instance of instances) {
			const siblings = byMap.get(instance.mapId) ?? [];
			if (instance.content === "world" || siblings.length <= 1) {
				instance.dungeonVersion = "base";
			} else if (VersionAbbrev[instance.key] !== undefined) {
				instance.dungeonVersion = VersionAbbrev[instance.key];
			} else {
				instance.dungeonVersion = instance.expansion;
			}
		}
	}

	private markSharedMapExactDifficulty(instances: ITrackerInstance[]): void {
		for (const instance of instances) {
			if (instance.difficulties.length !== 1) {
				continue;
			}
			instance.exactDifficultyMatch =
				instance.exactDifficultyMatch ||
				instances.some(
					(other) =>
						other.id !== instance.id &&
						other.mapId === instance.mapId &&
						other.content === instance.content &&
						(other.difficulties.length !== instance.difficulties.length ||
							other.difficulties.some((d) => !instance.difficulties.includes(d))),
				);
		}
	}

	/**
	 * Indexes progression card artwork. When a per-stack asset sidecar is configured the uploaded
	 * progression/ tree (from armory.data.zip) wins over the local static/data/progression fallback.
	 * The sidecar ships a generated .images.json manifest (see ArmoryAssetsService) because nginx
	 * does not expose directory listings.
	 */
	private async buildImageIndex(): Promise<Map<string, string>> {
		const assetBase = (this.armory.config.assetProxyUrl ?? "").replace(/\/+$/, "");
		const localManifest = path.join(process.cwd(), "static", "data", "progression", ".images.json");

		if (assetBase) {
			try {
				const res = await fetch(`${assetBase}/progression/.images.json`, { signal: AbortSignal.timeout(15000) });
				if (res.ok) {
					const manifest = JSON.parse(await res.text()) as { files?: Record<string, string> };
					const index = this.indexFromManifest(manifest.files);
					if (index.size > 0) {
						this.armory.logger.info(`Loaded ${index.size} progression images from the asset sidecar.`);
						return index;
					}
				}
			} catch {
				// Fall through to local sources.
			}
		}

		try {
			const manifest = JSON.parse(fs.readFileSync(localManifest, "utf8")) as { files?: Record<string, string> };
			const index = this.indexFromManifest(manifest.files);
			if (index.size > 0) {
				return index;
			}
		} catch {
			// No local manifest; walk the directory tree below.
		}

		return this.buildImageIndexFromLocalDir();
	}

	/** Turns manifest entries (asset-relative paths) into browser-facing /static/data/progression URLs. */
	private indexFromManifest(files: Record<string, string> | undefined): Map<string, string> {
		const index = new Map<string, string>();
		if (files === undefined) {
			return index;
		}
		for (const [key, assetPath] of Object.entries(files)) {
			const normalized = assetPath.replace(/\\/g, "/").replace(/^\/+/, "");
			const publicPath = normalized.startsWith("progression/")
				? `/static/data/${normalized}`
				: `/static/data/progression/${normalized}`;
			index.set(key, publicPath);
		}
		return index;
	}

	/** Walks static/data/progression on disk (dev / default baked-in images). */
	private buildImageIndexFromLocalDir(): Map<string, string> {
		const index = new Map<string, string>();
		const baseDir = path.join(process.cwd(), "static", "data", "progression");
		for (const content of ["dungeon", "raid", "world"]) {
			for (const expansion of ["classic", "tbc", "wotlk"]) {
				const dir = path.join(baseDir, content, expansion);
				let files: string[];
				try {
					files = fs.readdirSync(dir);
				} catch {
					continue;
				}
				for (const file of files) {
					const ext = path.extname(file).toLowerCase();
					if (ext !== ".png" && ext !== ".jpg" && ext !== ".jpeg" && ext !== ".webp") {
						continue;
					}
					const normalized = normalizeImageKey(path.basename(file, ext));
					index.set(`${content}/${expansion}/${normalized}`, `/static/data/progression/${content}/${expansion}/${file}`);
				}
			}
		}
		return index;
	}

	private resolveImage(
		index: Map<string, string>,
		content: TrackerContent,
		expansion: TrackerExpansion,
		key: string,
		name: string,
	): string | null {
		const candidates = [normalizeImageKey(key), normalizeImageKey(name)];
		const alias = ImageAliases[key];
		if (alias !== undefined) {
			candidates.push(alias);
		}
		for (const candidate of candidates) {
			const found = index.get(`${content}/${expansion}/${candidate}`);
			if (found !== undefined) {
				return found;
			}
		}
		return null;
	}
}
