import * as fs from "fs";
import * as path from "path";

import camelCase from "camelcase";

export interface IGlyphProperties {
	id: number;
	spellId: number;
}

export interface IAchievement {
	id: number;
	faction: number;
	titleLang0: string;
	descriptionLang0: string;
	category: number;
	points: number;
	flags: number;
	iconId: number;
}

export interface IAchievementCategory {
	id: number;
	parent: number;
	nameLang0: string;
}

export interface ICharTitle {
	id: number;
	nameLang0: string;
	name1Lang0: string;
	maskId: number;
}

export interface IItemDbc {
	id: number;
	classId: number;
	subclassId: number;
	displayInfoId: number;
	inventoryType: number;
}

export interface IItemRetailDbc {
	id: number;
	inventoryType: number;
}

export interface IItemAppearanceDbc {
	id: number;
	itemDisplayInfoId: number;
}

export interface IItemModifiedAppearanceDbc {
	id: number;
	itemId: number;
	itemAppearanceId: number;
}

export interface IItemDisplayInfoDbc {
	id: number;
	inventoryIcon0: number;
}

export interface IMountDbc {
	id: number;
	sourceSpellId: number;
}

export interface IMountXDisplayDbc {
	id: number;
	creatureDisplayInfoId: number;
	mountId: number;
}

export interface ISkillDbc {
    id: number;
    categoryId: number;
    skillCostId: number;
    name: string;
    spellIcon: number;
    altVerb: string;
    canLink: number;
}

export interface ISpellDbc {
	id: number;
	mechanic: number;
	spellIconId: number;
	nameLang0?: string;
	descriptionLang0?: string;
	effect0?: number;
	effect1?: number;
	effect2?: number;
	effectAura0?: number;
	effectAura1?: number;
	effectAura2?: number;
	effectBasePoints0?: number;
	effectBasePoints1?: number;
	effectBasePoints2?: number;
	effectMiscValue0?: number;
	effectMiscValue1?: number;
	effectMiscValue2?: number;
	effectMiscValueB0?: number;
	effectMiscValueB1?: number;
	effectMiscValueB2?: number;
	procChance?: number;
	procCharges?: number;
	maxTargets?: number;
	effectAmplitude0?: number;
	effectAmplitude1?: number;
	effectAmplitude2?: number;
	effectChainTargets0?: number;
	effectChainTargets1?: number;
	effectChainTargets2?: number;
	durationIndex?: number;
	effectRadiusIndex0?: number;
	effectRadiusIndex1?: number;
	effectRadiusIndex2?: number;
}

export interface IItemSetDbc {
	id: number;
	nameLang: string;
	setSpellId0?: number;
	setSpellId1?: number;
	setSpellId2?: number;
	setSpellId3?: number;
	setSpellId4?: number;
	setSpellId5?: number;
	setSpellId6?: number;
	setSpellId7?: number;
	setThreshold0?: number;
	setThreshold1?: number;
	setThreshold2?: number;
	setThreshold3?: number;
	setThreshold4?: number;
	setThreshold5?: number;
	setThreshold6?: number;
	setThreshold7?: number;
}

export interface ISpellDurationDbc {
	id: number;
	duration: number;
}

export interface ISpellRadiusDbc {
	id: number;
	radius: number;
}

export interface ISpellItemEnchantmentDbc {
	id: number;
	srcItemId: number;
}

export interface ISpellIcon {
	id: number;
	textureFilename: string;
}

export interface ITalent {
	id: number;
	tabId: number;
	tierId: number;
	columnIndex: number;
	spellRank0: number;
	spellRank1: number;
	spellRank2: number;
	spellRank3: number;
	spellRank4: number;
	prereqTalent0: number;
	prereqRank0: number;
}

export interface ITalentTab {
	id: number;
	nameLang0: string;
	spellIconId: number;
	classMask: number;
}

export interface IFactionDbc {
    id: number;
    reputationId: number;
    name: string;
}

export interface IAreas {
    id: number;
    zoneName: string;
    mapId: number;
    areaId: number;
}

interface IAsyncGeneratorWithArrayMethods<T> {
	[Symbol.asyncIterator](): AsyncGenerator<T>;
	toArray(): Promise<T[]>;
	map<M>(fn: (t: T) => M): IAsyncGeneratorWithArrayMethods<M>;
	filter(fn: (t: T) => boolean): IAsyncGeneratorWithArrayMethods<T>;
	find(fn: (t: T) => boolean): Promise<T | undefined>;
}

class ArrayAsAsyncGenerator<T> implements IAsyncGeneratorWithArrayMethods<T> {
	private data: T[];

	public constructor(data: T[]) {
		this.data = data;
	}

	async toArray(): Promise<T[]> {
		return this.data;
	}

	async *[Symbol.asyncIterator](): AsyncGenerator<T> {
		for (const x of this.data) {
			yield x;
		}
	}

	public map<M>(fn: (t: T) => M): ArrayAsAsyncGenerator<M> {
		return new ArrayAsAsyncGenerator<M>(this.data.map(fn));
	}

	filter(fn: (t: T) => boolean): ArrayAsAsyncGenerator<T> {
		return new ArrayAsAsyncGenerator<T>(this.data.filter(fn));
	}

	async find(fn: (t: T) => boolean): Promise<T | undefined> {
		return this.data.find(fn);
	}
}

class AsyncGenWrapper<T> implements IAsyncGeneratorWithArrayMethods<T> {
	private gen: AsyncGenerator<T>;

	public constructor(gen: AsyncGenerator<T>) {
		this.gen = gen;
	}

	public static from<T>(array: T[]): AsyncGenWrapper<T> {
		return new AsyncGenWrapper<T>(
			(async function* () {
				for (const x of array) {
					yield x;
				}
			})(),
		);
	}

	public async *[Symbol.asyncIterator](): AsyncGenerator<T> {
		for await (const x of this.gen) {
			yield x;
		}
	}

	public async toArray(): Promise<T[]> {
		const values = [];
		for await (const x of this) {
			values.push(x);
		}
		return values;
	}

	private wrap<X>(g: (that: AsyncGenWrapper<T>) => AsyncGenerator<X>): AsyncGenWrapper<X> {
		return new AsyncGenWrapper<X>(g(this));
	}

	public map<M>(fn: (t: T) => M): AsyncGenWrapper<M> {
		return this.wrap(async function* (me) {
			for await (const x of me) {
				yield fn(x);
			}
		});
	}

	public filter(fn: (t: T) => boolean): AsyncGenWrapper<T> {
		return this.wrap(async function* (me) {
			for await (const x of me) {
				if (fn(x)) {
					yield x;
				}
			}
		});
	}

	public async find(fn: (t: T) => boolean): Promise<T | undefined> {
		for await (const x of this) {
			if (fn(x)) {
				return x;
			}
		}
		return undefined;
	}
}

// A field is either a column name (kept under the same camelCased key) or a
// [property, header] tuple that aliases a CSV column to a different property
// name. Aliases let us point at the data/dbc exports (e.g. "Name_Lang") while
// keeping the property names the rest of the code expects (e.g. "nameLang0").
type DbcField = string | [string, string];

class DbcReader<T> {
	private filePath: string;
	private fields: DbcField[];

	public constructor(filePath: string, keepFields: DbcField[] = []) {
		this.filePath = filePath;
		this.fields = keepFields;
	}

	public async *read(): AsyncGenerator<T> {
		const stream = fs.createReadStream(this.filePath);
		const itr = this.parseCsv(stream);
		const headerLine = await itr.next();
		if (headerLine.done === true) {
			return;
		}

		const headerCols = headerLine.value.map((header) => camelCase(header).replace(/[[\]]/g, ""));

		// Map camelCased header -> destination property name.
		const aliasMap = new Map<string, string>();
		for (const field of this.fields) {
			if (Array.isArray(field)) {
				aliasMap.set(field[1], field[0]);
			} else {
				aliasMap.set(field, field);
			}
		}

		for await (const arr of itr) {
			const cols = arr.map((value) => {
				if (value === "") {
					return value;
				}
				return isNaN(+value) ? value : parseInt(value, 10);
			});
			const row: Record<string, unknown> = {};
			headerCols.forEach((header, headerIdx) => {
				if (this.fields.length === 0) {
					row[header] = cols[headerIdx];
				} else {
					const prop = aliasMap.get(header);
					if (prop !== undefined) {
						row[prop] = cols[headerIdx];
					}
				}
			});
			yield row as T;
		}
	}

	private async *parseCsv(stream: fs.ReadStream): AsyncGenerator<string[]> {
		// Adapted from https://stackoverflow.com/a/14991797
		const arr = [];
		let col = 0;
		let quote = false; // 'true' means we're inside a quoted field

		for await (const chunk of stream) {
			const str = chunk.toString();
			// Iterate over each character, keep track of current column (of the returned array)
			for (let c = 0; c < str.length; ++c) {
				const ch = str[c];
				const nch = str[c + 1]; // Current character, next character
				if (!(col in arr)) {
					arr[col] = ""; // Create a new column (start with empty string) if necessary
				}

				// If the current character is a quotation mark, and we're inside a
				// quoted field, and the next character is also a quotation mark,
				// add a quotation mark to the current column and skip the next character
				if (ch == '"' && quote && nch == '"') {
					arr[col] += ch;
					++c;
					continue;
				}

				// If it's just one quotation mark, begin/end quoted field
				if (ch == '"') {
					quote = !quote;
					continue;
				}

				// If it's a comma and we're not in a quoted field, move on to the next column
				if (ch == "," && !quote) {
					++col;
					continue;
				}

				// If it's a newline (CRLF) and we're not in a quoted field, skip the next character
				// and move on to the next row and move to column 0 of that new row
				if (ch == "\r" && nch == "\n" && !quote) {
					yield arr;
					arr.length = 0; // Clear the row
					col = 0;
					++c;
					continue;
				}

				// If it's a newline (LF or CR) and we're not in a quoted field,
				// move on to the next row and move to column 0 of that new row
				if (!quote && (ch == "\r" || ch == "\n")) {
					yield arr;
					arr.length = 0; // Clear the row
					col = 0;
					continue;
				}

				// Otherwise, append the current character to the current column
				arr[col] += ch;
			}
		}
	}
}

const dir = path.join(process.cwd(), "static", "data");
// Full 3.3.5a client DBC dump (CSV). The retail (9.2.0) files used by the Zam 3D
// viewer (transmog appearances + mounts) have no 3.3.5 equivalent and live in
// static/data/dbc_transmog/ (or on the mounted armory-assets volume at dbc_transmog/).
function dbcDatasetDir(...segments: string[]): string {
	const mount = (process.env["ACORE_ARMORY_ASSETS_MOUNT"] ?? "").replace(/\/+$/, "");
	if (mount) {
		return path.join(mount, ...segments);
	}
	return path.join(dir, ...segments);
}
const dbcDir = dbcDatasetDir("dbc");
const transmogDir = dbcDatasetDir("dbc_transmog");
export const DbcFiles = {
	achievement: path.join(dbcDir, "Achievement.csv"),
	achievementCategory: path.join(dbcDir, "Achievement_Category.csv"),
	charTitles: path.join(dbcDir, "CharTitles.csv"),
    areas: path.join(dbcDir, "AreaTable.csv"),
    faction: path.join(dbcDir, "Faction.csv"),
	glyphProperties: path.join(dbcDir, "GlyphProperties.csv"),
	item: path.join(dbcDir, "Item.csv"),
	itemRetail: path.join(dbcDir, "Item.csv"),
	itemAppearance: path.join(transmogDir, "ItemAppearance_9.2.0_41462.csv"),
	itemModifiedAppearance: path.join(transmogDir, "ItemModifiedAppearance_9.2.0_41462.csv"),
	itemDisplayInfo: path.join(dbcDir, "ItemDisplayInfo.csv"),
	mount: path.join(transmogDir, "Mount_9.2.0_41462.csv"),
	mountDisplay: path.join(transmogDir, "MountXDisplay_9.2.0_41462.csv"),
    skill: path.join(dbcDir, "SkillLine.csv"),
	spell: path.join(dbcDir, "Spell.csv"),
	spellItemEnchantment: path.join(dbcDir, "SpellItemEnchantment.csv"),
	spellIcon: path.join(dbcDir, "SpellIcon.csv"),
	talent: path.join(dbcDir, "Talent.csv"),
	talentTab: path.join(dbcDir, "TalentTab.csv"),
	itemSet: path.join(dbcDir, "ItemSet.csv"),
	spellDuration: path.join(dbcDir, "SpellDuration.csv"),
	spellRadius: path.join(dbcDir, "SpellRadius.csv"),
};

// Field lists for the data/dbc CSVs. Where the dbc column name differs from the
// property the code expects, a [property, csvColumn] alias is used (csvColumn is
// the camelCased header, e.g. "Name_Lang" -> "nameLang").
const dbcFields: { [key: string]: DbcField[] } = {
	achievement: ["id", "faction", ["titleLang0", "titleLang"], ["descriptionLang0", "descriptionLang"], "category", "points", "flags", "iconId"],
	achievementCategory: ["id", "parent", ["nameLang0", "nameLang"]],
	charTitles: ["id", ["nameLang0", "nameLang"], ["name1Lang0", "name1Lang"], "maskId"],
    areas: ["id", ["zoneName", "areaNameLang"]],
	glyphProperties: ["id", "spellId"],
	item: ["id", "classId", "subclassId", "displayInfoId", "inventoryType"],
	itemRetail: ["id", "inventoryType"],
	itemAppearance: ["id", "itemDisplayInfoId"],
	itemModifiedAppearance: ["id", "itemId", "itemAppearanceId"],
	itemDisplayInfo: ["id", "inventoryIcon0"],
	mount: ["id", "sourceSpellId"],
	mountDisplay: ["id", "creatureDisplayInfoId", "mountId"],
    skill: ["id", "categoryId", ["name", "displayNameLang"]],
	spell: [
		"id",
		"mechanic",
		"spellIconId",
		["nameLang0", "nameLang"],
		["descriptionLang0", "descriptionLang"],
		"effect0",
		"effect1",
		"effect2",
		"effectAura0",
		"effectAura1",
		"effectAura2",
		"effectBasePoints0",
		"effectBasePoints1",
		"effectBasePoints2",
		"effectMiscValue0",
		"effectMiscValue1",
		"effectMiscValue2",
		"effectMiscValueB0",
		"effectMiscValueB1",
		"effectMiscValueB2",
		"procChance",
		"procCharges",
		"maxTargets",
		["effectAmplitude0", "effectAuraPeriod0"],
		["effectAmplitude1", "effectAuraPeriod1"],
		["effectAmplitude2", "effectAuraPeriod2"],
		"effectChainTargets0",
		"effectChainTargets1",
		"effectChainTargets2",
		"durationIndex",
		"effectRadiusIndex0",
		"effectRadiusIndex1",
		"effectRadiusIndex2",
	],
	spellItemEnchantment: ["id", "srcItemId"],
	spellIcon: ["id", "textureFilename"],
	talent: [
		"id",
		"tabId",
		"tierId",
		"columnIndex",
		"spellRank0",
		"spellRank1",
		"spellRank2",
		"spellRank3",
		"spellRank4",
		"prereqTalent0",
		"prereqRank0",
	],
	talentTab: ["id", ["nameLang0", "nameLang"], "spellIconId", "classMask"],
    faction: ["id", ["reputationId", "reputationIndex"], ["name", "nameLang"]],
	itemSet: [
		"id",
		"nameLang",
		"setSpellId0", "setSpellId1", "setSpellId2", "setSpellId3", "setSpellId4", "setSpellId5", "setSpellId6", "setSpellId7",
		"setThreshold0", "setThreshold1", "setThreshold2", "setThreshold3", "setThreshold4", "setThreshold5", "setThreshold6", "setThreshold7",
	],
	spellDuration: ["id", "duration"],
	spellRadius: ["id", "radius"],
};

export class DbcManager {
	private _achievement!: IAchievement[];
	private _achievementCategory!: IAchievementCategory[];
	private _charTitles!: ICharTitle[];
    private _areas!: IAreas[];
	private _glyphProperties!: IGlyphProperties[];
    private _faction!: IFactionDbc[];
	private _item!: IItemDbc[];
	private _itemRetail!: IItemRetailDbc[];
	private _itemAppearance!: IItemAppearanceDbc[];
	private _itemModifiedAppearance!: IItemModifiedAppearanceDbc[];
	private _itemDisplayInfo!: IItemDisplayInfoDbc[];
	private _mount!: IMountDbc[];
	private _mountDisplay!: IMountXDisplayDbc[];
    private _skill!: ISkillDbc[];
	private _spell!: ISpellDbc[];
	private _spellItemEnchantment!: ISpellItemEnchantmentDbc[];
	private _spellIcon!: ISpellIcon[];
	private _talent!: ITalent[];
	private _talentTab!: ITalentTab[];
	private _itemSet!: IItemSetDbc[];
	private _spellDuration!: ISpellDurationDbc[];
	private _spellRadius!: ISpellRadiusDbc[];

	public async loadAllFiles(): Promise<void> {
		this._achievement = await this.read<IAchievement>(DbcFiles.achievement, dbcFields.achievement).toArray();
		this._achievementCategory = await this.read<IAchievementCategory>(
			DbcFiles.achievementCategory,
			dbcFields.achievementCategory,
		).toArray();
		this._charTitles = await this.readOptional<ICharTitle>(DbcFiles.charTitles, dbcFields.charTitles).toArray();
        this._areas = await this.read<IAreas>(DbcFiles.areas, dbcFields.areas).toArray();
		this._glyphProperties = await this.read<IGlyphProperties>(DbcFiles.glyphProperties, dbcFields.glyphProperties).toArray();
		this._item = await this.read<IItemDbc>(DbcFiles.item, dbcFields.item).toArray();
		this._itemRetail = await this.read<IItemRetailDbc>(DbcFiles.itemRetail, dbcFields.itemRetail).toArray();
		this._itemAppearance = await this.readOptional<IItemAppearanceDbc>(DbcFiles.itemAppearance, dbcFields.itemAppearance).toArray();
		this._itemModifiedAppearance = await this.readOptional<IItemModifiedAppearanceDbc>(
			DbcFiles.itemModifiedAppearance,
			dbcFields.itemModifiedAppearance,
		).toArray();
		this._itemDisplayInfo = await this.read<IItemDisplayInfoDbc>(DbcFiles.itemDisplayInfo, dbcFields.itemDisplayInfo).toArray();
		this._mount = await this.readOptional<IMountDbc>(DbcFiles.mount, dbcFields.mount).toArray();
		this._mountDisplay = await this.readOptional<IMountXDisplayDbc>(DbcFiles.mountDisplay, dbcFields.mountDisplay).toArray();
        this._skill = await this.read<ISkillDbc>(DbcFiles.skill, dbcFields.skill).toArray();
		this._spell = await this.read<ISpellDbc>(DbcFiles.spell, dbcFields.spell).toArray();
		this._spellItemEnchantment = await this.read<ISpellItemEnchantmentDbc>(
			DbcFiles.spellItemEnchantment,
			dbcFields.spellItemEnchantment,
		).toArray();
		this._spellIcon = await this.read<ISpellIcon>(DbcFiles.spellIcon, dbcFields.spellIcon).toArray();
		this._talent = await this.read<ITalent>(DbcFiles.talent, dbcFields.talent).toArray();
		this._talentTab = await this.read<ITalentTab>(DbcFiles.talentTab, dbcFields.talentTab).toArray();
		this._itemSet = await this.read<IItemSetDbc>(DbcFiles.itemSet, dbcFields.itemSet).toArray();
		this._spellDuration = await this.read<ISpellDurationDbc>(DbcFiles.spellDuration, dbcFields.spellDuration).toArray();
		this._spellRadius = await this.read<ISpellRadiusDbc>(DbcFiles.spellRadius, dbcFields.spellRadius).toArray();
	}

	public achievement() {
		return this.getLoadedDataOrRead(DbcFiles.achievement, this._achievement, dbcFields.achievement);
	}

	public achievementCategory() {
		return this.getLoadedDataOrRead(DbcFiles.achievementCategory, this._achievementCategory, dbcFields.achievementCategory);
	}

	public charTitles() {
		return this._charTitles === undefined
			? this.readOptional<ICharTitle>(DbcFiles.charTitles, dbcFields.charTitles)
			: new ArrayAsAsyncGenerator(this._charTitles);
	}

    public areas() {
        return this.getLoadedDataOrRead(DbcFiles.areas, this._areas, dbcFields.areas);
    }

    public faction() {
        return this.getLoadedDataOrRead(DbcFiles.faction, this._faction, dbcFields.faction);
    }

	public glyphProperties() {
		return this.getLoadedDataOrRead(DbcFiles.glyphProperties, this._glyphProperties, dbcFields.glyphProperties);
	}

	public item() {
		return this.getLoadedDataOrRead(DbcFiles.item, this._item, dbcFields.item);
	}

	public itemRetail() {
		return this.getLoadedDataOrRead(DbcFiles.itemRetail, this._itemRetail, dbcFields.itemRetail);
	}

	public itemAppearance() {
		return this.getLoadedDataOrRead(DbcFiles.itemAppearance, this._itemAppearance, dbcFields.itemAppearance);
	}

	public itemModifiedAppearance() {
		return this.getLoadedDataOrRead(DbcFiles.itemModifiedAppearance, this._itemModifiedAppearance, dbcFields.itemModifiedAppearance);
	}

	public itemDisplayInfo() {
		return this.getLoadedDataOrRead(DbcFiles.itemDisplayInfo, this._itemDisplayInfo, dbcFields.itemDisplayInfo);
	}

	public mount() {
		return this.getLoadedDataOrRead(DbcFiles.mount, this._mount, dbcFields.mount);
	}

	public mountDisplay() {
		return this.getLoadedDataOrRead(DbcFiles.mountDisplay, this._mountDisplay, dbcFields.mountDisplay);
	}

    public skill() {
        return this.getLoadedDataOrRead(DbcFiles.skill, this._skill, dbcFields.skill);
    }

	public spell() {
		return this.getLoadedDataOrRead(DbcFiles.spell, this._spell, dbcFields.spell);
	}

	public spellItemEnchantment() {
		return this.getLoadedDataOrRead(DbcFiles.spellItemEnchantment, this._spellItemEnchantment, dbcFields.spellItemEnchantment);
	}

	public spellIcon() {
		return this.getLoadedDataOrRead(DbcFiles.spellIcon, this._spellIcon, dbcFields.spellIcon);
	}

	public talent() {
		return this.getLoadedDataOrRead(DbcFiles.talent, this._talent, dbcFields.talent);
	}

	public talentTab() {
		return this.getLoadedDataOrRead(DbcFiles.talentTab, this._talentTab, dbcFields.talentTab);
	}

	public itemSet() {
		return this.getLoadedDataOrRead(DbcFiles.itemSet, this._itemSet, dbcFields.itemSet);
	}

	public spellDuration() {
		return this.getLoadedDataOrRead(DbcFiles.spellDuration, this._spellDuration, dbcFields.spellDuration);
	}

	public spellRadius() {
		return this.getLoadedDataOrRead(DbcFiles.spellRadius, this._spellRadius, dbcFields.spellRadius);
	}

	private read<T>(file: string, keepFields: DbcField[] = []): AsyncGenWrapper<T> {
		const reader = new DbcReader<T>(file, keepFields);
		return new AsyncGenWrapper(reader.read());
	}

	private readOptional<T>(file: string, keepFields: DbcField[] = []): IAsyncGeneratorWithArrayMethods<T> {
		if (!fs.existsSync(file)) {
			return new ArrayAsAsyncGenerator<T>([]);
		}
		return this.read<T>(file, keepFields);
	}

	private getLoadedDataOrRead<T>(path: string, data: T[], keepFields: DbcField[] = []): IAsyncGeneratorWithArrayMethods<T> {
		return data === undefined ? this.read<T>(path, keepFields) : new ArrayAsAsyncGenerator(data);
	}
}
