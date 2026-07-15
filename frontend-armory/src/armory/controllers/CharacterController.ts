import * as express from "express";
import { RowDataPacket } from "mysql2/promise";

import { Armory } from "../Armory";
import { buildLayoutRenderModel, characterLayoutPageId } from "../LayoutWidgets";
import { IRealmConfig } from "../Config";
import { IEmblem, Utils } from "../Utils";
import { StatCalculator, IEquippedItemStats, ItemMod } from "../StatCalculator";
import { IAchievement as IAchievementDbc, ICharTitle, ISkillDbc, IAreas, ISpellDbc } from "../data/DbcReader";
import { ITrackerInstance, RaidDifficultyLabel, RaidTrackerCatalog, TrackerContent, TrackerExpansion } from "../data/RaidTrackerCatalog";

interface ICharacterData {
	guid: number;
	name: string;
	race: number;
	class: number;
	gender: number;
	level: number;
	skin: number;
	face: number;
	hairStyle: number;
	hairColor: number;
	facialStyle: number;
	playerFlags: number;
	online: number;
	guild: string;
    map: number;
    zone: number;
    zoneName: string;
    position_x: number;
    position_y: number;
    money: number;
    totaltime: number;
    health: number;
    power1: number;
    totalKills: number;
    todayKills: number;
    yesterdayKills: number;
    totalHonorPoints: number;
    arenaPoints: number;
    activeTalentGroup: number;
	chosenTitle: number;
}

interface IEquipmentData {
	slot: number;
	itemEntry: number;
	flags: number;
	enchantments: string | number[];
	randomPropertyId: number;
	classId: number;
	subclassId: number;
	quality: number;
	transmog?: number;
	icon?: number;
	gems?: number[];
	name?: string;
	itemLevel?: number;
	armor?: number;
	stats?: { type: number; value: number }[];
	sockets?: number[];
	resistance?: { holy: number; fire: number; nature: number; frost: number; shadow: number; arcane: number };
	requiredLevel?: number;
	maxDurability?: number;
	inventoryType?: number;
	itemset?: number;
	typeLeft?: string;
	typeRight?: string;
	set?: {
		name: string;
		ownedCount: number;
		totalCount: number;
		members: { name: string; owned: boolean }[];
		bonuses: { threshold: number; text: string; active: boolean }[];
	};
}

interface IBaseStatRow {
	strength: number;
	agility: number;
	stamina: number;
	intellect: number;
	spirit: number;
}

interface ICustomizationOption {
	optionId: number;
	choiceId: number;
}

interface IMount {
	creatureDisplayId: number;
	spell: number;
	icon: string;
	name: string;
}

interface ICompanion {
	creatureEntry: number;
	spell: number;
	icon: string;
	name: string;
}

interface IAchievement {
	id: number;
	category: number;
	title: string;
	description: string;
	points: number;
	icon: string;
}

interface IArenaTeam {
	id: number;
	name: string;
	type: number;
	rating: number;
	seasonWins: number;
	seasonGames: number;
	background: number;
	emblemStyle: number;
	emblemColor: number;
	borderStyle: number;
	borderColor: number;
	emblem?: IEmblem;
}

interface ISkills {
    id: number;
    categoryId: number
    skill: string;
    value: number;
    max: number ;
}

interface IPet {
    entry: number;
    name: string;
    species: string;
    level: number;
    modelId: number;
    petType: string;
    slotName: string;
}

interface IReputation {
    id: number;
    name: string;
    standing: string;
    value: number;
    valueInGrade: number;
    max: number;
    expansionId: number;
}

interface IQuest {
    id: number;
    title: string;
    status: 'Completed' | 'In Progress';
    minLevel: number;
    questLevel: number;
    questSortID: number;
}

const ItemClassGem = 3;
const SpellMechanicMounted = 21;
// Non-combat companion (vanity pet) spells summon a critter: an effect of type
// SPELL_EFFECT_SUMMON whose SummonProperties (EffectMiscValueB) is the companion slot.
const SpellEffectSummon = 28;
const SummonPropertiesCompanion = 41;
const RaceDisplayName: { [key: number]: string } = {
	1: "Human",
	2: "Orc",
	3: "Dwarf",
	4: "Night Elf",
	5: "Undead",
	6: "Tauren",
	7: "Gnome",
	8: "Troll",
	10: "Blood Elf",
	11: "Draenei",
};
const ClassDisplayName: { [key: number]: string } = {
	1: "Warrior",
	2: "Paladin",
	3: "Hunter",
	4: "Rogue",
	5: "Priest",
	6: "Death Knight",
	7: "Shaman",
	8: "Mage",
	9: "Warlock",
	11: "Druid",
};

// A per-character row of the raid_logs_tracker table (mod-raid-logs-tracker).
interface ILogsTrackerRow {
	recordType: number; // 0 = instance clear, 1 = boss kill
	instanceId: number;
	bossEntry: number; // creature entry; 0 for clear rows
	difficulty: number;
	bestTimeMs: number;
	lastTimeMs: number;
	completions: number;
	lastSeen: number;
}

type ProgressionKillCounts = Map<string, number>;

const TrackerContents: TrackerContent[] = ["dungeon", "raid", "world"];
const TrackerExpansions: TrackerExpansion[] = ["classic", "tbc", "wotlk"];

export class CharacterController {
	private armory: Armory;
    private areaById!: { [key: number]: IAreas };
	private itemInventoryTypes!: { [key: number]: number };
	private itemIcons!: { [key: number]: number };
	private gemItems!: { [key: number]: boolean };
	private enchantSrcItems!: { [key: number]: number };
	private itemSocketBonuses!: { [key: number]: number };
	private mountSpells!: number[];
	private mountBySpellId!: { [key: number]: IMount };
	private companionSpells!: number[];
	private companionBySpellId!: { [key: number]: ICompanion };
    private skillById!: { [key: number]: ISkillDbc };
	private achievementById!: { [key: number]: IAchievementDbc };
	private charTitleById!: { [key: number]: ICharTitle };
	private charTitleByMaskId!: { [key: number]: ICharTitle };
	private classLevelStats!: { [classId: number]: { [level: number]: IBaseStatRow } };
	private classBasePool!: { [classId: number]: { [level: number]: { hp: number; mana: number } } };
	private raceStats!: { [raceId: number]: IBaseStatRow };
	private spellStatEffects!: { [spellId: number]: { type: number; value: number }[] };
	private spellDurationById!: { [key: number]: number };
	private spellRadiusById!: { [key: number]: number };

	public constructor(armory: Armory) {
		this.armory = armory;
	}

	public async load(): Promise<void> {
		this.itemInventoryTypes = {};
		const itemsRetail = await this.armory.dbc.itemRetail().toArray();
		for await (const item of this.armory.dbc.item()) {
			const retailItem = itemsRetail.find((row) => row.id === item.id);
			if (retailItem !== undefined) {
				this.itemInventoryTypes[item.id] = retailItem.inventoryType;
			}
		}

		this.itemIcons = {};
		const itemIconsByDisplayInfoId: { [key: number]: number } = {};
		for await (const row of this.armory.dbc.itemDisplayInfo()) {
			itemIconsByDisplayInfoId[row.id] = row.inventoryIcon0;
		}
		for await (const item of this.armory.dbc.item()) {
			const icon = itemIconsByDisplayInfoId[item.displayInfoId];
			if (icon !== undefined) {
				this.itemIcons[item.id] = icon;
			}
		}

		this.gemItems = {};
		for await (const row of this.armory.dbc.item().filter((item) => item.classId === ItemClassGem)) {
			this.gemItems[row.id] = true;
		}

		this.enchantSrcItems = {};
		for await (const row of this.armory.dbc.spellItemEnchantment()) {
			this.enchantSrcItems[row.id] = row.srcItemId;
		}

		this.itemSocketBonuses = {};
		const [rows] = await this.armory.worldDb.query<RowDataPacket[]>({
			sql: "SELECT entry, socketBonus FROM item_template WHERE socketBonus <> 0",
			timeout: this.armory.config.dbQueryTimeout,
		});
		for (const row of rows as RowDataPacket[]) {
			this.itemSocketBonuses[row.entry] = row.socketBonus;
		}

		const mountSpells = await this.armory.dbc
			.spell()
			.filter((m) => m.mechanic === SpellMechanicMounted)
			.toArray();
		this.mountSpells = mountSpells.map((spell) => spell.id);
		this.mountBySpellId = {};
		for (const spell of mountSpells) {
			const mount = await this.armory.dbc.mount().find((m) => m.sourceSpellId === spell.id);
			const icon = await this.armory.dbc.spellIcon().find((icon) => icon.id === spell.spellIconId);
			if (mount !== undefined) {
				const display = await this.armory.dbc.mountDisplay().find((d) => d.mountId === mount.id);
				if (display !== undefined) {
					this.mountBySpellId[spell.id] = {
						creatureDisplayId: display.creatureDisplayInfoId,
						spell: spell.id,
						icon: this.processSpellIconTexture(icon?.textureFilename ?? ""),
						name: spell.nameLang0 ?? "Unknown Mount",
					};
				}
			}
		}

		const companionSpells = await this.armory.dbc
			.spell()
			.filter(
				(s) =>
					(s.effect0 === SpellEffectSummon && s.effectMiscValueB0 === SummonPropertiesCompanion) ||
					(s.effect1 === SpellEffectSummon && s.effectMiscValueB1 === SummonPropertiesCompanion) ||
					(s.effect2 === SpellEffectSummon && s.effectMiscValueB2 === SummonPropertiesCompanion),
			)
			.toArray();
		this.companionSpells = companionSpells.map((spell) => spell.id);
		this.companionBySpellId = {};
		for (const spell of companionSpells) {
			const icon = await this.armory.dbc.spellIcon().find((i) => i.id === spell.spellIconId);
			const creatureEntry =
				spell.effect0 === SpellEffectSummon
					? spell.effectMiscValue0
					: spell.effect1 === SpellEffectSummon
						? spell.effectMiscValue1
						: spell.effectMiscValue2;
			this.companionBySpellId[spell.id] = {
				creatureEntry: creatureEntry ?? 0,
				spell: spell.id,
				icon: this.processSpellIconTexture(icon?.textureFilename ?? ""),
				name: spell.nameLang0 ?? "Unknown Companion",
			};
		}

		this.achievementById = {};
		for await (const achievement of this.armory.dbc.achievement()) {
			this.achievementById[achievement.id] = achievement;
		}
		this.charTitleById = {};
		this.charTitleByMaskId = {};
		for await (const title of this.armory.dbc.charTitles()) {
			this.charTitleById[title.id] = title;
			this.charTitleByMaskId[title.maskId] = title;
		}

        this.skillById = {};
        for await (const skill of this.armory.dbc.skill()) {
            this.skillById[skill.id] = skill;
        }
        this.areaById = {};
        for await (const area of this.armory.dbc.areas()) {
            this.areaById[area.id] = area;
        }

        // Lookups for resolving $d (duration) / $a (radius) tokens in spell text.
        this.spellDurationById = {};
        for await (const row of this.armory.dbc.spellDuration()) {
            this.spellDurationById[row.id] = row.duration;
        }
        this.spellRadiusById = {};
        for await (const row of this.armory.dbc.spellRadius()) {
            this.spellRadiusById[row.id] = row.radius;
        }

		await this.loadBaseStats();
		await this.loadSpellStatEffects();
	}

	private async loadBaseStats(): Promise<void> {
		this.classLevelStats = {};
		this.classBasePool = {};
		this.raceStats = {};
		try {
			const [classRows] = await this.armory.worldDb.query<RowDataPacket[]>({
				sql: "SELECT Class, Level, BaseHP, BaseMana, Strength, Agility, Stamina, Intellect, Spirit FROM player_class_stats",
				timeout: this.armory.config.dbQueryTimeout,
			});
			for (const row of classRows as RowDataPacket[]) {
				if (this.classLevelStats[row.Class] === undefined) {
					this.classLevelStats[row.Class] = {};
					this.classBasePool[row.Class] = {};
				}
				this.classLevelStats[row.Class][row.Level] = {
					strength: row.Strength,
					agility: row.Agility,
					stamina: row.Stamina,
					intellect: row.Intellect,
					spirit: row.Spirit,
				};
				this.classBasePool[row.Class][row.Level] = {
					hp: row.BaseHP ?? 0,
					mana: row.BaseMana ?? 0,
				};
			}

			const [raceRows] = await this.armory.worldDb.query<RowDataPacket[]>({
				sql: "SELECT Race, Strength, Agility, Stamina, Intellect, Spirit FROM player_race_stats",
				timeout: this.armory.config.dbQueryTimeout,
			});
			for (const row of raceRows as RowDataPacket[]) {
				this.raceStats[row.Race] = {
					strength: row.Strength,
					agility: row.Agility,
					stamina: row.Stamina,
					intellect: row.Intellect,
					spirit: row.Spirit,
				};
			}
		} catch (err) {
			// Base-stat tables are optional; the stat engine degrades gracefully without them.
			this.armory.logger.warn(`Could not load base stat tables: ${err}`);
		}
	}

	private getBaseStats(raceId: number, classId: number, level: number): IBaseStatRow {
		const classStats = this.classLevelStats?.[classId]?.[level];
		const raceDelta = this.raceStats?.[raceId];
		if (classStats === undefined || raceDelta === undefined) {
			return { strength: 0, agility: 0, stamina: 0, intellect: 0, spirit: 0 };
		}
		return {
			strength: classStats.strength + raceDelta.strength,
			agility: classStats.agility + raceDelta.agility,
			stamina: classStats.stamina + raceDelta.stamina,
			intellect: classStats.intellect + raceDelta.intellect,
			spirit: classStats.spirit + raceDelta.spirit,
		};
	}

	private getBasePool(classId: number, level: number): { hp: number; mana: number } {
		return this.classBasePool?.[classId]?.[level] ?? { hp: 0, mana: 0 };
	}

	// Trade-skill line id -> mirrored WoW icon name.
	private static readonly professionIcons: { [skillId: number]: string } = {
		171: "trade_alchemy",
		164: "trade_blacksmithing",
		333: "trade_engraving",
		202: "trade_engineering",
		182: "trade_herbalism",
		773: "inv_inscription_tradeskill01",
		755: "inv_misc_gem_01",
		165: "trade_leatherworking",
		186: "trade_mining",
		393: "inv_misc_pelt_wolf_01",
		197: "trade_tailoring",
	};

	private static getProfessionIcon(skillId: number): string {
		return CharacterController.professionIcons[skillId] ?? "inv_misc_questionmark";
	}

	/**
	 * Pre-compute the stat contributions of every spell that can be triggered
	 * "on equip" by an item. Classic gear (e.g. Plagueheart, Atiesh) grants its
	 * spell power / hit / crit through these equip auras rather than through the
	 * item_template stat columns, so they must be decoded from Spell.dbc.
	 */
	private async loadSpellStatEffects(): Promise<void> {
		this.spellStatEffects = {};

		// SPELL_AURA_* effect aura indices (3.3.5a).
		const AURA_MOD_DAMAGE_DONE = 13; // spell power (misc = school mask)
		const AURA_MOD_ATTACK_POWER = 99;
		const AURA_MOD_RANGED_ATTACK_POWER = 124;
		const AURA_MOD_RATING = 189; // misc = combat-rating bitmask
		const AURA_MOD_SPELL_CRIT = 57; // direct % spell crit
		const AURA_MOD_POWER_REGEN = 85; // mana per 5s (misc = power type, 0 = mana)
		const SCHOOL_MASK_PHYSICAL = 1;

		// Combat-rating bit -> ItemMod rating type.
		const CR_BIT_TO_ITEM_MOD: { [bit: number]: number } = {
			1: ItemMod.DEFENSE_SKILL_RATING,
			2: ItemMod.DODGE_RATING,
			3: ItemMod.PARRY_RATING,
			4: ItemMod.BLOCK_RATING,
			5: ItemMod.HIT_MELEE_RATING,
			6: ItemMod.HIT_RANGED_RATING,
			7: ItemMod.HIT_SPELL_RATING,
			8: ItemMod.CRIT_MELEE_RATING,
			9: ItemMod.CRIT_RANGED_RATING,
			10: ItemMod.CRIT_SPELL_RATING,
			17: ItemMod.HASTE_MELEE_RATING,
			18: ItemMod.HASTE_RANGED_RATING,
			19: ItemMod.HASTE_SPELL_RATING,
			23: ItemMod.EXPERTISE_RATING,
			24: ItemMod.ARMOR_PENETRATION_RATING,
		};

		for await (const spell of this.armory.dbc.spell()) {
			const effects: { type: number; value: number }[] = [];
			for (let i = 0; i < 3; i++) {
				const aura = (spell as any)[`effectAura${i}`] as number | undefined;
				if (!aura) {
					continue;
				}
				// EffectBasePoints is stored as (value - 1) in the client DBC.
				const value = (((spell as any)[`effectBasePoints${i}`] as number) || 0) + 1;
				const misc = ((spell as any)[`effectMiscValue${i}`] as number) || 0;
				if (value <= 0) {
					continue;
				}

				switch (aura) {
					case AURA_MOD_DAMAGE_DONE:
						// Only count general magic spell power (ignore physical-only).
						if (misc !== SCHOOL_MASK_PHYSICAL) {
							effects.push({ type: ItemMod.SPELL_POWER, value });
						}
						break;
					case AURA_MOD_ATTACK_POWER:
						effects.push({ type: ItemMod.ATTACK_POWER, value });
						break;
					case AURA_MOD_RANGED_ATTACK_POWER:
						effects.push({ type: ItemMod.RANGED_ATTACK_POWER, value });
						break;
					case AURA_MOD_POWER_REGEN:
						if (misc === 0) {
							effects.push({ type: ItemMod.MANA_REGENERATION, value });
						}
						break;
					case AURA_MOD_SPELL_CRIT:
						// Direct % spell crit -> equivalent crit rating (14 rating = 1% at level 60).
						effects.push({ type: ItemMod.CRIT_SPELL_RATING, value: value * 14 });
						break;
					case AURA_MOD_RATING:
						for (const bitStr of Object.keys(CR_BIT_TO_ITEM_MOD)) {
							const bit = parseInt(bitStr, 10);
							if (misc & (1 << bit)) {
								effects.push({ type: CR_BIT_TO_ITEM_MOD[bit], value });
							}
						}
						break;
				}
			}
			if (effects.length > 0) {
				this.spellStatEffects[spell.id] = effects;
			}
		}
	}

	public async character(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const charName = req.params.name as string;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			// Could not find realm
			return next(404);
		}

		const charData = await this.getCharacterData(realm, charName);
		if (charData === null) {
			// Could not find character
			return next(404);
		}

		const equipmentData = await this.getEquipmentData(realmName, charData.guid);
		const customization = this.getCustomizationOptions(charData);
		const equipment = equipmentData.map((row) => {
			row.icon = this.itemIcons[row.itemEntry];
			row.gems = this.getGemsFromEnchantments(row.enchantments as string);
			row.enchantments = this.filterEnchantments(row.itemEntry, row.enchantments as string);
			return row;
		});
		const mounts = await this.getMounts(realmName, charData.guid);
		const transmogs: number[][] | undefined = this.armory.config.transmogModule ? [] : undefined;
		const characterModelItems = await this.getModelViewerItems(equipmentData, charData.class, transmogs);

		// Reconstruct an approximate character sheet from base + gear stats.
		// Falls back to a default panel set if anything is missing (e.g. base-stat tables absent).
		const { gear, avgItemLevel } = this.summarizeGear(equipment);
		let stats;
		try {
			const baseStats = this.getBaseStats(charData.race, charData.class, charData.level);
			const basePool = this.getBasePool(charData.class, charData.level);
			stats = StatCalculator.calculate(
				charData.level,
				charData.class,
				baseStats,
				gear,
				basePool.hp,
				basePool.mana,
				charData.health,
				charData.power1,
			);
		} catch (err) {
			this.armory.logger.warn(`Could not compute stats for ${charData.name}: ${err}`);
			stats = StatCalculator.defaultStats();
		}

		// Condensed previews for the clickable overview cards.
		const skills = await this.getSkills(realm.name, charData.guid);
		const professions = skills
			.filter((skill) => skill.categoryId === 11)
			.map((p) => ({ name: p.skill, value: p.value, max: p.max, icon: CharacterController.getProfessionIcon(p.id) }));
		const talentSpec = await this.getTalentSpecSummary(charData.class, realm.name, charData.guid, charData.activeTalentGroup);
		const recentAchievements = await this.getRecentAchievements(realm.name, charData.guid, 5);

		res.render("character.hbs", {
			title: `${charData.name}`,
			...(await this.makeSharedDataObject(realm, charData)),
			...buildLayoutRenderModel("character"),
			avgItemLevel,
			page: "overview",
			contentPath: this.armory.config.useZamCdn ? "" : this.armory.config.websiteRoot + "/static/data/",
			overview: {
				stats,
				professions,
				talentSpec,
				recentAchievements,
				mountCount: mounts.length,
				mountIcons: mounts.slice(0, 8).map((m) => m.icon),
				kills: {
					total: charData.totalKills,
					today: charData.todayKills,
					yesterday: charData.yesterdayKills,
				},
				honorPoints: charData.totalHonorPoints,
				arenaPoints: charData.arenaPoints,
				money: {
					gold: Math.floor(charData.money / 10000),
					silver: Math.floor((charData.money % 10000) / 100),
					copper: charData.money % 100,
				},
				playedHours: Math.floor(charData.totaltime / 3600),
			},
			data: {
				race: charData.race,
				gender: charData.gender,
				class: charData.class,
				flags: charData.playerFlags,
				characterModelItems,
				characterModelTransmogs: transmogs,
				customizationOptions: customization,
				equipment,
				mounts,
			},
		});

		this.armory.gc();
	}

	private summarizeGear(equipment: IEquipmentData[]): { gear: IEquippedItemStats; avgItemLevel: number } {
		const gear: IEquippedItemStats = {
			armor: 0,
			stats: [],
			resistance: { holy: 0, fire: 0, nature: 0, frost: 0, shadow: 0, arcane: 0 },
		};

		// Average item level excludes shirt (slot 3) and tabard (slot 18).
		const ilvlSlots = equipment.filter((e) => ![3, 18].includes(e.slot) && (e.itemLevel ?? 0) > 0);
		let ilvlSum = 0;

		for (const item of equipment) {
			gear.armor += item.armor ?? 0;
			if (item.resistance !== undefined) {
				gear.resistance.holy += item.resistance.holy;
				gear.resistance.fire += item.resistance.fire;
				gear.resistance.nature += item.resistance.nature;
				gear.resistance.frost += item.resistance.frost;
				gear.resistance.shadow += item.resistance.shadow;
				gear.resistance.arcane += item.resistance.arcane;
			}
			for (const stat of item.stats ?? []) {
				gear.stats.push(stat);
			}
			if (![3, 18].includes(item.slot) && (item.itemLevel ?? 0) > 0) {
				ilvlSum += item.itemLevel as number;
			}
		}

		const avgItemLevel = ilvlSlots.length > 0 ? Math.round(ilvlSum / ilvlSlots.length) : 0;
		return { gear, avgItemLevel };
	}

	private async getTalentSpecSummary(
		classId: number,
		realm: string,
		charGuid: number,
		activeGroup: number,
	): Promise<{ trees: { name: string; icon: string; points: number }[]; primary: string }> {
		const talents = await this.getTalents(realm, charGuid);
		const trees = await this.getTalentTrees(classId);
		const learned = new Set(talents[activeGroup] ?? talents[0] ?? []);

		const summary = trees.map((tree) => {
			let points = 0;
			for (const spell of tree.spells) {
				const ranks = [spell.spellRank0, spell.spellRank1, spell.spellRank2, spell.spellRank3, spell.spellRank4];
				for (let rank = ranks.length - 1; rank >= 0; rank--) {
					if (ranks[rank] && learned.has(ranks[rank])) {
						points += rank + 1;
						break;
					}
				}
			}
			return { name: tree.name, icon: tree.icon, points };
		});

		const primary = summary.reduce((best, t) => (t.points > best.points ? t : best), { name: "", points: -1 });
		return {
			trees: summary,
			primary: primary.points > 0 ? `${primary.name} (${summary.map((t) => t.points).join("/")})` : "No talents",
		};
	}

	private async getRecentAchievements(
		realm: string,
		charGuid: number,
		limit: number,
	): Promise<{ title: string; icon: string; date: number }[]> {
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT achievement, date
				FROM character_achievement
				WHERE guid = ?
				ORDER BY date DESC
				LIMIT ?
			`,
			values: [charGuid, limit],
			timeout: this.armory.config.dbQueryTimeout,
		});

		const result: { title: string; icon: string; date: number }[] = [];
		for (const row of rows as RowDataPacket[]) {
			const dbc = this.achievementById[row.achievement];
			if (dbc === undefined) {
				continue;
			}
			const icon = await this.armory.dbc.spellIcon().find((i) => i.id === dbc.iconId);
			result.push({
				title: dbc.titleLang0,
				icon: this.processSpellIconTexture(icon?.textureFilename ?? ""),
				date: row.date,
			});
		}
		return result;
	}

	public async talents(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const charName = req.params.name as string;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			// Could not find realm
			return next(404);
		}

		const charData = await this.getCharacterData(realm, charName);
		if (charData === null) {
			// Could not find character
			return next(404);
		}

		res.render("character-talents.hbs", {
			title: `${charData.name} - Talents`,
			...(await this.makeSharedDataObject(realm, charData)),
			...buildLayoutRenderModel(characterLayoutPageId("talents")),
			page: "talents",
			data: {
				talents: await this.getTalents(realm.name, charData.guid),
				trees: await this.getTalentTrees(charData.class),
				glyphs: await this.getGlyphs(realm.name, charData.guid),
				activeSpec: charData.activeTalentGroup,
			},
		});
	}

    public async skills(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
        const realmName = req.params.realm as string;
        const charName = req.params.name as string;

        const realm = this.armory.getRealm(realmName);
        if (realm === undefined) {
            // Could not find realm
            return next(404);
        }

        const charData = await this.getCharacterData(realm, charName);
        if (charData === null) {
            // Could not find character
            return next(404);
        }
        const skills = await this.getSkills(realm.name, charData.guid);
        const professions = skills.filter((skill) => skill.categoryId === 11);
        const secondarySkills = skills.filter((skill) => skill.categoryId === 9);
        const weaponSkills = skills.filter((skill) => skill.categoryId === 6);
        const classSkills = skills.filter((skill) => skill.categoryId === 7);
        const armorSkills = skills.filter((skill) => skill.categoryId === 8);
        const languages = skills.filter((skill) => skill.categoryId === 10);
        res.render("character-skills.hbs", {
            title: `Armory - ${charData.name} - Skills`,
            ...(await this.makeSharedDataObject(realm, charData)),
            ...buildLayoutRenderModel(characterLayoutPageId("skills")),
            page: "skills",
            data: {
                skills: skills,
                professions: professions,
                secondarySkills: secondarySkills,
                weaponSkills: weaponSkills,
                classSkills: classSkills,
                armorSkills: armorSkills,
                languages: languages,
            },
        });
    }

    public async reputation(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
        const realmName = req.params.realm as string;
        const charName = req.params.name as string;

        const realm = this.armory.getRealm(realmName);
        if (realm === undefined) {
            return next(404);
        }

        const charData = await this.getCharacterData(realm, charName);
        if (charData === null) {
            return next(404);
        }

        const reputations = await this.getReputations(realm.name, charData.guid);
        // Group reputations by expansion/category
        const classicReps = reputations.filter(rep => rep.expansionId === 0);
        const tbcReps = reputations.filter(rep => rep.expansionId === 1);
        const wotlkReps = reputations.filter(rep => rep.expansionId === 2);

        res.render("character-reputation.hbs", {
            title: `Armory - ${charData.name} - Reputation`,
            ...(await this.makeSharedDataObject(realm, charData)),
            page: "reputation",
            data: {
                classic: classicReps,
                tbc: tbcReps,
                wotlk: wotlkReps
            },
        });
    }

    public async quests(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
        const realmName = req.params.realm as string;
        const charName = req.params.name as string;

        const realm = this.armory.getRealm(realmName);
        if (realm === undefined) {
            return next(404);
        }

        const charData = await this.getCharacterData(realm, charName);
        if (charData === null) {
            return next(404);
        }

        const quests = await this.getQuests(realm.name, charData.guid);

        // Group quests by zone/profession
        const questsByCategory = quests.reduce<{ [key: string]: IQuest[] }>((acc, quest) => {
            const category = quest.questSortID > 0 ?
                this.getZoneName(quest.questSortID):
                this.getProfessionName(quest.questSortID);

            if (!acc[category]) {
                acc[category] = [];
            }
            acc[category].push(quest);
            return acc;
        }, {});

        // Get list of all characters
        const allCharacters = await this.getAllCharacters(realm.name);

        res.render("character-quests.hbs", {
            title: `Armory - ${charData.name} - Quests`,
            ...(await this.makeSharedDataObject(realm, charData)),
            page: "quests",
            data: {
                categories: questsByCategory,
                otherCharacters: allCharacters.filter(c => c.guid !== charData.guid)
            },
        });
    }

    public async questsCompare(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
        const realmName = req.params.realm as string;
        const charName = req.params.name as string;
        const otherRealmName = req.params.otherRealm as string;
        const otherCharName = req.params.otherName as string;

        const realm = this.armory.getRealm(realmName);
        const otherRealm = this.armory.getRealm(otherRealmName);
        if (realm === undefined || otherRealm === undefined) {
            return next(404);
        }

        const charData = await this.getCharacterData(realm, charName);
        const otherCharData = await this.getCharacterData(otherRealm, otherCharName);
        if (charData === null || otherCharData === null) {
            return next(404);
        }

        const charQuests = await this.getQuests(realm.name, charData.guid);
        const otherQuests = await this.getQuests(otherRealm.name, otherCharData.guid);

        // Group quests by category
        interface IQuestComparison {
            id: number;
            title: string;
            questLevel: number;
            char1Status?: 'Completed' | 'In Progress';
            char2Status?: 'Completed' | 'In Progress';
        }

        const categories: { [key: string]: IQuestComparison[] } = {};

        const addQuestsToCategories = (quests: IQuest[], source: 'char1Status' | 'char2Status') => {
            quests.forEach(quest => {
                const category = quest.questSortID > 0 ?
                    this.getZoneName(quest.questSortID) :
                    this.getProfessionName(quest.questSortID);

                if (!categories[category]) {
                    categories[category] = [];
                }

                const existingQuest = categories[category].find(q => q.id === quest.id);
                if (existingQuest) {
                    existingQuest[source] = quest.status;
                    // Remove quest if both characters have completed it
                    if (existingQuest.char1Status === 'Completed' && existingQuest.char2Status === 'Completed') {
                        categories[category] = categories[category].filter(q => q.id !== quest.id);
                    }
                } else {
                    categories[category].push({
                        id: quest.id,
                        title: quest.title,
                        questLevel: quest.questLevel,
                        [source]: quest.status
                    });
                }
            });
        };

        addQuestsToCategories(charQuests, 'char1Status');
        addQuestsToCategories(otherQuests, 'char2Status');

        // Get list of all characters
        const allCharacters = await this.getAllCharacters(realm.name);

        res.render("character-quests-compare.hbs", {
            title: `Armory - Quest Compare - ${charData.name} vs ${otherCharData.name}`,
            ...(await this.makeSharedDataObject(realm, charData)),
            data: {
                categories: categories,
                char1: {
                    name: charData.name,
                    realm: realm.name
                },
                char2: {
                    name: otherCharData.name,
                    realm: otherRealm.name
                },
                otherCharacters: allCharacters.filter(c => c.guid !== charData.guid)
            },
        });
    }

    private async getReputations(realm: string, character: number): Promise<IReputation[]> {
        const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
            sql: `
				SELECT faction, standing, flags
				FROM character_reputation
				WHERE guid = ?
				-- FACTION_FLAG_VISIBLE (0x1): faction is shown in the reputation pane.
				-- Exclude FACTION_FLAG_HIDDEN (0x4) and FACTION_FLAG_INVISIBLE_FORCED (0x8).
				-- Also keep any faction where the character has earned/lost reputation.
				AND (
					((flags & 1) = 1 AND (flags & 4) = 0 AND (flags & 8) = 0)
					OR standing != 0
				)
			`,
            values: [character],
            timeout: this.armory.config.dbQueryTimeout,
        });

        const reputations = [];
        for (const row of rows as RowDataPacket[]) {
            const factionInfo = await this.armory.dbc.faction().find(f => f.id === row.faction);
            if (factionInfo && factionInfo.reputationId >= 0) {
                reputations.push({
                    id: row.faction,
                    name: factionInfo.name,
                    standing: this.getReputationStanding(row.standing),
                    value: row.standing,
                    valueInGrade: this.getReputationInGrade(row.standing),
                    max: this.getReputationMax(row.standing),
                    expansionId: this.getExpansionId(row.faction)
                });
            }
        }

        return reputations;
    }

    private getReputationStanding(value: number): string {
        if (value < -6000) {
            return 'Hated'; // These are probably not correct, but I don't have a character to test against
        }
        if (value < -3000) {
            return 'Hostile'; // These are probably not correct, but I don't have a character to test against
        }
        if (value < 0) {
            return 'Unfriendly'; // These are probably not correct, but I don't have a character to test against
        }
        if (value < 1000) {
            return 'Neutral'; // These are probably not correct, but I don't have a character to test against
        }
        if (value < 6000) {
            return 'Friendly';
        }
        if (value < 19000) {
            return 'Honored';
        }
        if (value < 40000) {
            return 'Revered';
        }

        return 'Exalted'; // These are probably not correct, but I don't have a character to test against
    }

    private getReputationMax(value: number): number {
        if (value < -6000) {
            return -6000; // These are probably not correct, but I don't have a character to test against
        }
        if (value < -3000) {
            return -3000; // These are probably not correct, but I don't have a character to test against
        }
        if (value < 0) {
            return 0; // These are probably not correct, but I don't have a character to test against
        }
        if (value < 1000) {
            return 3000;
        }
        if (value < 6000) {
            return 6000;
        }
        if (value < 12000) {
            return 12000;
        }
        if (value < 21000) {
            return 21000;
        }

        return 40000; // This might not be right, but I dont have an exalted character to test against
    }

    private getReputationInGrade(value: number): number {
        if (value < -6000) {
            return value + 3000; // These are probably not correct, but I don't have a character to test against
        }
        if (value < -3000) {
            return value; // These are probably not correct, but I don't have a character to test against
        }
        if (value < 0) {
            return value; // These are probably not correct, but I don't have a character to test against
        }
        if (value < 1000) {
            return value + 2000; // These are probably not correct, but I don't have a character to test against
        }
        if (value < 6000) {
            return value - 1000;
        }
        if (value < 12000) {
            return value - 5900;  // For some reason I needed to remove an extra 100 from this one to get it to match the client
        }
        if (value < 21000) {
            return value - 16900; // For some reason I needed to remove an extra 100 from this one to get it to match the client
        }

        return value - 21100; // This might not be right, but I dont have an exalted character to test against
    }

    private getExpansionId(factionId: number): number {
        // Classic factions
        if (factionId < 900) {
            return 0;
        }
        // TBC factions
        if (factionId < 1100) {
            return 1;
        }

        // WotLK factions
        return 2;
    }

    private async getQuests(realm: string, character: number): Promise<IQuest[]> {
        // Get completed and rewarded quests
        const [completedRows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
            sql: `
				SELECT quest FROM (
					SELECT quest FROM character_queststatus
					WHERE guid = ? AND status = 1
					UNION
					SELECT quest FROM character_queststatus_rewarded
					WHERE guid = ?
				) AS completed_quests
			`,
            values: [character, character],
            timeout: this.armory.config.dbQueryTimeout,
        });

        // Get in progress quests
        const [inProgressRows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
            sql: `
				SELECT quest
				FROM character_queststatus
				WHERE guid = ? AND status = 3
			`,
            values: [character],
            timeout: this.armory.config.dbQueryTimeout,
        });

        const quests: IQuest[] = [];

        // Process completed quests
        for (const row of completedRows as RowDataPacket[]) {
            const questInfo = await this.getQuestInfo(row.quest);
            if (questInfo) {
                quests.push({
                    id: row.quest,
                    title: questInfo.title,
                    status: 'Completed',
                    minLevel: questInfo.minLevel,
                    questLevel: questInfo.questLevel,
                    questSortID: questInfo.questSortID
                });
            }
        }

        // Process in progress quests
        for (const row of inProgressRows as RowDataPacket[]) {
            const questInfo = await this.getQuestInfo(row.quest);
            if (questInfo) {
                quests.push({
                    id: row.quest,
                    title: questInfo.title,
                    status: 'In Progress',
                    minLevel: questInfo.minLevel,
                    questLevel: questInfo.questLevel,
                    questSortID: questInfo.questSortID
                });
            }
        }

        return quests;
    }

    private async getQuestInfo(questId: number): Promise<IQuest | undefined> {
        const [rows] = await this.armory.worldDb.query<RowDataPacket[]>({
            sql: `
				SELECT ID, LogTitle as title, MinLevel as minLevel, QuestLevel as questLevel, QuestSortID as questSortID
				FROM quest_template
				WHERE ID = ?
			`,
            values: [questId],
            timeout: this.armory.config.dbQueryTimeout,
        });

        return rows[0] as IQuest | undefined;
    }

    private getZoneName(zoneId: number): string {
        this.areaById[zoneId]?.zoneName;

        return this.areaById[zoneId]?.zoneName || `Zone ${zoneId}`;
    }

    private getProfessionName(professionId: number): string {
        const questTypes: { [key: number]: string } = {
            // Negative IDs (Classes and Professions)
            // Classes
            "-61": "Warlock",
            "-81": "Warrior",
            "-82": "Shaman",
            "-141": "Paladin",
            "-161": "Mage",
            "-162": "Rogue",
            "-261": "Hunter",
            "-262": "Priest",
            "-263": "Druid",
            "-372": "Death Knight",
            // Professions
            "-24": "Herbalism",
            "-101": "Fishing",
            "-121": "Blacksmithing",
            "-181": "Alchemy",
            "-182": "Leatherworking",
            "-201": "Engineering",
            "-264": "Tailoring",
            "-304": "Cooking",
            "-324": "First Aid",
            "-371": "Inscription",
            "-373": "Jewelcrafting",
            "-762": "Riding",
            // Misc
            "-1": "Epic",
            "-21": "Wailing Caverns",
            "-22": "Seasonal",
            "-23": "Undercity",
            "-25": "Battlegrounds",
            "-41": "Uldaman",
            "-221": "Treasure Map",
            "-241": "Tournament",
            "-284": "Special",
            "-344": "Legendary",
            "-364": "Darkmoon Faire",
            "-365": "Ahn'Qiraj War",
            "-366": "Lunar Festival",
            "-367": "Reputation",
            "-368": "Invasion",
            "-369": "Midsummer",
            "-370": "Brewfest",
            "-374": "Noblegarden",
            "-375": "Pilgrim's Bounty",
            "-376": "Love is in the Air"
        };

        return questTypes[professionId] || `Category ${professionId}`;
    }

    private getQuestExpansionId(questLevel: number): number {
        if (questLevel <= 60) {
            return 0; // Classic
        }
        if (questLevel <= 70) {
            return 1; // TBC
        }

        return 2; // WotLK
    }

	public async achievements(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const charName = req.params.name as string;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			// Could not find realm
			return next(404);
		}

		const charData = await this.getCharacterData(realm, charName);
		if (charData === null) {
			// Could not find character
			return next(404);
		}

		res.render("character-achievements.hbs", {
			title: `${charData.name} - Achievements`,
			...(await this.makeSharedDataObject(realm, charData)),
			...buildLayoutRenderModel(characterLayoutPageId("achievements")),
			page: "achievements",
		});
	}

	public async achievementsData(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const character = parseInt(req.params.character as string) || -1;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			// Could not find realm
			return next(404);
		}

		const charData = await this.getCharacterData(realm, character);
		if (charData === null) {
			// Could not find character
			return next(404);
		}

		res.json({
			categories: await this.armory.dbc.achievementCategory().toArray(),
			...(await this.getAchievements(realm.name, charData)),
		});
	}

	public async progression(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const charName = req.params.name as string;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			// Could not find realm
			return next(404);
		}

		const charData = await this.getCharacterData(realm, charName);
		if (charData === null) {
			// Could not find character
			return next(404);
		}

		// Hide the page entirely when the progression module is not installed on the server.
		if (!(await this.armory.isProgressionModuleInstalled(realm.name))) {
			return next(404);
		}

		res.render("character-progression.hbs", {
			title: `${charData.name} - Progression`,
			...(await this.makeSharedDataObject(realm, charData)),
			...buildLayoutRenderModel(characterLayoutPageId("progression")),
			page: "progression",
		});
	}

	public async records(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const charName = req.params.name as string;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			// Could not find realm
			return next(404);
		}

		const charData = await this.getCharacterData(realm, charName);
		if (charData === null) {
			// Could not find character
			return next(404);
		}

		// Hide the page entirely when the clear tracker module is not installed on the server.
		if (!(await this.armory.isLogsTrackerModuleInstalled(realm.name))) {
			return next(404);
		}

		res.render("character-records.hbs", {
			title: `${charData.name} - Logs`,
			...(await this.makeSharedDataObject(realm, charData)),
			...buildLayoutRenderModel(characterLayoutPageId("records")),
			page: "records",
		});
	}

	public async recordsData(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const character = parseInt(req.params.character as string) || -1;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			// Could not find realm
			return next(404);
		}

		const charData = await this.getCharacterData(realm, character);
		if (charData === null) {
			// Could not find character
			return next(404);
		}

		res.json(await this.getRecords(realm.name, charData.guid));
	}

	public async progressionData(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const character = parseInt(req.params.character as string) || -1;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			// Could not find realm
			return next(404);
		}

		const charData = await this.getCharacterData(realm, character);
		if (charData === null) {
			// Could not find character
			return next(404);
		}

		res.json(await this.getProgression(realm.name, charData.guid));
	}

	private async getProgression(realm: string, charGuid: number) {
		// `${mapId}:${bossEntry}:${difficulty}` for difficulty-aware matching and
		// `${mapId}:${bossEntry}` when difficulty is irrelevant (Classic/TBC).
		const killedExact: ProgressionKillCounts = new Map();
		const killedAnyDiff: ProgressionKillCounts = new Map();

		try {
			const killCountColumn = await this.getProgressionKillCountColumn(realm);
			const countExpr = killCountColumn === null
				? "COUNT(*)"
				: `SUM(GREATEST(COALESCE(\`${killCountColumn}\`, 0), 1))`;
			const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
				sql:
					"SELECT `boss_entry`, `map_id`, `difficulty`, " +
					`${countExpr} AS \`kill_count\` ` +
					"FROM `raid_progression_tracker` WHERE `player_guid` = ? " +
					"GROUP BY `boss_entry`, `map_id`, `difficulty`",
				values: [charGuid],
				timeout: this.armory.config.dbQueryTimeout,
			});
			for (const row of rows as RowDataPacket[]) {
				const count = Math.max(0, Number(row.kill_count) || 0);
				const exactKey = `${row.map_id}:${row.boss_entry}:${row.difficulty}`;
				const anyKey = `${row.map_id}:${row.boss_entry}`;
				killedExact.set(exactKey, (killedExact.get(exactKey) ?? 0) + count);
				killedAnyDiff.set(anyKey, (killedAnyDiff.get(anyKey) ?? 0) + count);
			}
		} catch {
			// The raid_progression_tracker table only exists when the server module
			// is installed; without it everything simply shows as 0 progress.
		}

		return this.buildTrackerSections((instances) => this.annotateProgressionInstances(instances, killedExact, killedAnyDiff));
	}

	/**
	 * Builds the { dungeon, raid, world } x { classic, tbc, wotlk } response shape shared by the
	 * Progression and Logs pages, from the raid tracker catalogue in the world database.
	 */
	private async buildTrackerSections<T>(annotate: (instances: ITrackerInstance[]) => T) {
		const result: { [content: string]: { [expansion: string]: T } } = {};
		for (const content of TrackerContents) {
			result[content] = {};
			for (const expansion of TrackerExpansions) {
				result[content][expansion] = annotate(await this.armory.raidTrackerCatalog.getSection(content, expansion));
			}
		}
		return result as { [content in TrackerContent]: { [expansion in TrackerExpansion]: T } };
	}

	private async getProgressionKillCountColumn(realm: string): Promise<string | null> {
		const candidates = ["kill_count", "killCount", "kills", "count", "kill_counter"];
		try {
			const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
				sql:
					"SELECT `COLUMN_NAME` FROM `information_schema`.`COLUMNS` " +
					"WHERE `TABLE_SCHEMA` = DATABASE() AND `TABLE_NAME` = 'raid_progression_tracker'",
				timeout: this.armory.config.dbQueryTimeout,
			});
			const columns = new Map((rows as RowDataPacket[]).map((row) => [String(row.COLUMN_NAME).toLowerCase(), String(row.COLUMN_NAME)]));
			for (const candidate of candidates) {
				const column = columns.get(candidate.toLowerCase());
				if (column !== undefined) {
					return column;
				}
			}
		} catch {
			// Older tracker builds may not expose schema metadata to this user; COUNT(*) still works.
		}
		return null;
	}

	private annotateProgressionInstances(instances: ITrackerInstance[], killedExact: ProgressionKillCounts, killedAnyDiff: ProgressionKillCounts) {
		const annotated = [];
		for (const instance of instances) {
			// Multi-difficulty instances (e.g. WotLK raids) become one card per difficulty;
			// single-difficulty instances are one unlabelled card whose kill match is exact only
			// when needed to disambiguate a shared map (see RaidTrackerCatalog.getVariants).
			for (const variant of RaidTrackerCatalog.getVariants(instance)) {
				const bosses = instance.bosses.map((boss) => {
					const killCount =
						variant.matchDifficulty === null
							? boss.npcIds.reduce((sum, id) => sum + (killedAnyDiff.get(`${instance.mapId}:${id}`) ?? 0), 0)
							: boss.npcIds.reduce((sum, id) => sum + (killedExact.get(`${instance.mapId}:${id}:${variant.matchDifficulty}`) ?? 0), 0);
					return { name: boss.name, done: killCount > 0, killCount, killCountLabel: this.formatKillCount(killCount) };
				});
				const bossesKilled = bosses.filter((b) => b.done).length;
				const totalKills = bosses.reduce((sum, boss) => sum + boss.killCount, 0);
				annotated.push({
					key: variant.difficulty === null ? instance.key : `${instance.key}-${variant.difficulty}`,
					name: instance.name,
					mapId: instance.mapId,
					image: instance.image,
					difficulty: variant.difficulty,
					difficultyLabel: variant.difficulty === null ? null : RaidDifficultyLabel[variant.difficulty] ?? null,
					earned: bossesKilled,
					total: bosses.length,
					totalKills,
					killCountLabel: bosses.length === 1 ? this.formatKillCount(totalKills) : `${bossesKilled} / ${bosses.length}`,
					totalKillCountLabel: this.formatKillCount(totalKills),
					bosses,
				});
			}
		}
		return annotated;
	}

	private formatKillCount(count: number): string {
		return count > 99 ? "99+" : String(Math.max(0, count));
	}

	private async getRecords(realm: string, charGuid: number) {
		const rows: ILogsTrackerRow[] = [];
		try {
			const [dbRows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
				sql:
					"SELECT `record_type`, `instance_id`, `boss_entry`, `difficulty`, `best_time_ms`, `last_time_ms`, `completions`, `last_seen` " +
					"FROM `raid_logs_tracker` WHERE `player_guid` = ?",
				values: [charGuid],
				timeout: this.armory.config.dbQueryTimeout,
			});
			for (const row of dbRows as RowDataPacket[]) {
				rows.push({
					recordType: row.record_type,
					instanceId: row.instance_id,
					bossEntry: row.boss_entry,
					difficulty: row.difficulty,
					bestTimeMs: row.best_time_ms,
					lastTimeMs: row.last_time_ms,
					completions: row.completions,
					lastSeen: row.last_seen,
				});
			}
		} catch {
			// The raid_logs_tracker table only exists when the server module is
			// installed; without it everything simply shows as no records.
		}

		const rowsByInstance = new Map<number, ILogsTrackerRow[]>();
		for (const row of rows) {
			const list = rowsByInstance.get(row.instanceId) ?? [];
			list.push(row);
			rowsByInstance.set(row.instanceId, list);
		}

		return this.buildTrackerSections((instances) => this.annotateRecordInstances(instances, rowsByInstance));
	}

	private annotateRecordInstances(instances: ITrackerInstance[], rowsByInstance: Map<number, ILogsTrackerRow[]>) {
		const annotated = [];
		for (const instance of instances) {
			const instanceRows = rowsByInstance.get(instance.id) ?? [];
			for (const variant of RaidTrackerCatalog.getVariants(instance)) {
				// A variant with an exact match difficulty only aggregates rows recorded on that
				// difficulty; unlabelled cards aggregate every difficulty the run was recorded on
				// (the module can record a difficulty outside the seeded list, e.g. 10-man TBC raids).
				const matches = (row: ILogsTrackerRow) => variant.matchDifficulty === null || row.difficulty === variant.matchDifficulty;

				const clear = this.aggregateClearRows(instanceRows.filter((r) => r.recordType === 0 && matches(r)));
				const bosses = instance.bosses.map((boss) => {
					const best = this.aggregateClearRows(
						instanceRows.filter((r) => r.recordType === 1 && boss.npcIds.includes(r.bossEntry) && matches(r)),
					);
					return { name: boss.name, ...best };
				});

				// World bosses have no enter-to-clear timer; their single boss kill record is the
				// card's headline time.
				const headline = instance.content === "world" && instance.bosses.length === 1 ? bosses[0] : clear;

				annotated.push({
					key: variant.difficulty === null ? instance.key : `${instance.key}-${variant.difficulty}`,
					name: instance.name,
					mapId: instance.mapId,
					image: instance.image,
					difficulty: variant.difficulty,
					difficultyLabel: variant.difficulty === null ? null : RaidDifficultyLabel[variant.difficulty] ?? null,
					clear: { bestMs: headline.bestMs, lastMs: headline.lastMs, completions: headline.completions, lastSeen: headline.lastSeen },
					timed: bosses.filter((b) => b.bestMs > 0).length,
					total: bosses.length,
					bosses,
				});
			}
		}
		return annotated;
	}

	/** Folds several clear-tracker rows (one per recorded difficulty) into one best/latest summary. */
	private aggregateClearRows(rows: ILogsTrackerRow[]) {
		const result = { bestMs: 0, lastMs: 0, completions: 0, lastSeen: 0 };
		for (const row of rows) {
			result.completions += row.completions;
			if (row.bestTimeMs > 0 && (result.bestMs === 0 || row.bestTimeMs < result.bestMs)) {
				result.bestMs = row.bestTimeMs;
			}
			if (row.lastSeen >= result.lastSeen) {
				result.lastSeen = row.lastSeen;
				result.lastMs = row.lastTimeMs;
			}
		}
		return result;
	}

	public async pvp(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const charName = req.params.name as string;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			// Could not find realm
			return next(404);
		}

		const charData = await this.getCharacterData(realm, charName);
		if (charData === null) {
			// Could not find character
			return next(404);
		}

		res.render("character-pvp.hbs", {
			title: `${charData.name} - PvP`,
			...(await this.makeSharedDataObject(realm, charData)),
			page: "pvp",
			faction: Utils.getFactionFromRaceId(charData.race),
			kills: await this.getPvpKills(realm.name, charData.guid),
			arenaTeams: await this.getArenaTeams(realm.name, charData.guid),
		});
	}

	public async mountsPage(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const charName = req.params.name as string;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			return next(404);
		}

		const charData = await this.getCharacterData(realm, charName);
		if (charData === null) {
			return next(404);
		}

		const mounts = await this.getMounts(realm.name, charData.guid);
		res.render("character-mounts.hbs", {
			title: `${charData.name} - Mounts`,
			...(await this.makeSharedDataObject(realm, charData)),
			page: "mounts",
			contentPath: this.armory.config.useZamCdn ? "" : this.armory.config.websiteRoot + "/static/data/",
			mounts,
			mountCount: mounts.length,
		});
	}

	public async companions(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const charName = req.params.name as string;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			return next(404);
		}

		const charData = await this.getCharacterData(realm, charName);
		if (charData === null) {
			return next(404);
		}

		const companions = await this.getCompanions(realm.name, charData.guid);
		res.render("character-companions.hbs", {
			title: `${charData.name} - Companions`,
			...(await this.makeSharedDataObject(realm, charData)),
			page: "companions",
			companions,
			companionCount: companions.length,
		});
	}

	public async pets(req: express.Request, res: express.Response, next: express.NextFunction): Promise<void> {
		const realmName = req.params.realm as string;
		const charName = req.params.name as string;

		const realm = this.armory.getRealm(realmName);
		if (realm === undefined) {
			return next(404);
		}

		const charData = await this.getCharacterData(realm, charName);
		if (charData === null) {
			return next(404);
		}

		res.render("character-pets.hbs", {
			title: `${charData.name} - Pets`,
			...(await this.makeSharedDataObject(realm, charData)),
			page: "pets",
			pets: await this.getPets(realm.name, charData.guid),
		});
	}

	private async makeSharedDataObject(realm: IRealmConfig, charData: ICharacterData) {
		const [hasPets, companionCount, progressionEnabled, recordsEnabled] = await Promise.all([
			this.countHunterPets(realm.name, charData.guid),
			this.countCompanions(realm.name, charData.guid),
			this.armory.isProgressionModuleInstalled(realm.name),
			this.armory.isLogsTrackerModuleInstalled(realm.name),
		]);
		return {
			realm: realm.name,
			name: charData.name,
			displayName: this.formatCharacterDisplayName(charData),
			guid: charData.guid,
			raceId: charData.race,
			race: RaceDisplayName[charData.race],
			classId: charData.class,
			class: ClassDisplayName[charData.class],
			level: charData.level,
			online: charData.online === 1,
			guild: charData.guild,
            zone: charData.zone,
            zoneName: this.getZoneName(charData.zone),
            faction: Utils.getFactionFromRaceId(charData.race),
            hasPets: hasPets > 0,
            companionCount,
            progressionEnabled,
            recordsEnabled,
            // The Tracking tab is shown when either tracker module is installed; it links to
            // whichever sub-page is available (Progression preferred).
            trackingEnabled: progressionEnabled || recordsEnabled,
            trackingDefaultPage: progressionEnabled ? "progression" : "logs",
		};
	}

	private formatCharacterDisplayName(charData: ICharacterData): string {
		const title = this.charTitleByMaskId[charData.chosenTitle] ?? this.charTitleById[charData.chosenTitle];
		if (title === undefined) {
			return charData.name;
		}

		const format = ((charData.gender === 1 ? title.name1Lang0 : title.nameLang0) || title.nameLang0 || title.name1Lang0 || "").trim();
		if (format.length === 0) {
			return charData.name;
		}

		return format.includes("%s") ? format.replace(/%s/g, charData.name) : `${charData.name} ${format}`;
	}

	private async countHunterPets(realm: string, charGuid: number): Promise<number> {
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: "SELECT COUNT(*) AS `count` FROM `character_pet` WHERE `owner` = ? AND `PetType` = 1",
			values: [charGuid],
			timeout: this.armory.config.dbQueryTimeout,
		});
		return (rows as RowDataPacket[])[0]?.count ?? 0;
	}

	private async countCompanions(realm: string, charGuid: number): Promise<number> {
		if (this.companionSpells.length === 0) {
			return 0;
		}
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: "SELECT COUNT(*) AS `count` FROM `character_spell` WHERE `guid` = ? AND `spell` IN (?)",
			values: [charGuid, this.companionSpells],
			timeout: this.armory.config.dbQueryTimeout,
		});
		return (rows as RowDataPacket[])[0]?.count ?? 0;
	}

	private async getCharacterData(realm: IRealmConfig, character: string | number): Promise<ICharacterData | null> {
		const where = typeof character === "string" ? "LOWER(`characters`.`name`) = LOWER(?)" : "`characters`.`guid` = ?";
		const [rows] = await this.armory.getCharactersDb(realm.name).query<RowDataPacket[]>({
			sql: `
				SELECT \`characters\`.\`guid\`, \`characters\`.\`name\`, \`race\`, \`class\`, \`gender\`, \`level\`, \`skin\`, \`face\`, \`hairStyle\`, \`hairColor\`, \`facialStyle\`, \`playerFlags\`, \`online\`, \`map\`, \`zone\`, \`position_x\`, \`position_y\`, \`money\`, \`totaltime\`, \`health\`, \`power1\`, \`totalKills\`, \`todayKills\`, \`yesterdayKills\`, \`totalHonorPoints\`, \`arenaPoints\`, \`activeTalentGroup\`, \`chosenTitle\`, \`guild\`.\`name\` AS \`guild\`
				FROM \`characters\`
				LEFT JOIN \`guild_member\` ON \`guild_member\`.\`guid\` = \`characters\`.\`guid\`
				LEFT JOIN \`guild\` ON \`guild\`.\`guildid\` = \`guild_member\`.\`guildid\`
				LEFT JOIN \`${realm.authDatabase}\`.\`account_access\` ON \`account_access\`.\`id\` = \`characters\`.\`account\` AND \`account_access\`.\`RealmID\` IN (-1, ${realm.realmId}) AND \`account_access\`.\`gmlevel\` > 0
				WHERE
					${where}
					AND (\`account_access\`.\`id\` IS NULL OR ? = 0)
			`,
			values: [character, this.armory.config.hideGameMasters ? 1 : 0],
			timeout: this.armory.config.dbQueryTimeout,
		});

		if ((rows as RowDataPacket[]).length === 0) {
			return null;
		}
		return rows[0] as ICharacterData;
	}

	private async getEquipmentData(realm: string, charGuid: number): Promise<IEquipmentData[]> {
		const transmogSelect = this.armory.config.transmogModule ? ", custom_transmogrification.FakeEntry AS transmog" : "";
		const transmogJoin = this.armory.config.transmogModule
			? "LEFT JOIN custom_transmogrification ON custom_transmogrification.GUID = item_instance.guid"
			: "";
		let [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT
					character_inventory.slot, item_instance.itemEntry, item_instance.flags, item_instance.enchantments, item_instance.randomPropertyId
					${transmogSelect}
				FROM character_inventory
				JOIN item_instance ON item_instance.guid = character_inventory.item
				${transmogJoin}
				WHERE character_inventory.guid = ? AND character_inventory.bag = 0 AND character_inventory.slot IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18)
			`,
			values: [charGuid],
			timeout: this.armory.config.dbQueryTimeout,
		});

		const data = rows as RowDataPacket[] as IEquipmentData[];
		if (data.length === 0) {
			return [];
		}

		for (const row of data) {
			const item = await this.armory.dbc.item().find((item) => item.id === row.itemEntry);
			if (item === undefined) {
				continue;
			}
			row.classId = item.classId;
			row.subclassId = item.subclassId;
		}

		[rows] = await this.armory.worldDb.query<RowDataPacket[]>({
			sql: `
				SELECT
					entry, quality, name, ItemLevel, armor,
					RequiredLevel, MaxDurability, InventoryType, itemset,
					holy_res, fire_res, nature_res, frost_res, shadow_res, arcane_res,
					stat_type1, stat_value1, stat_type2, stat_value2, stat_type3, stat_value3,
					stat_type4, stat_value4, stat_type5, stat_value5, stat_type6, stat_value6,
					stat_type7, stat_value7, stat_type8, stat_value8, stat_type9, stat_value9,
					stat_type10, stat_value10,
					socketColor_1, socketColor_2, socketColor_3,
					spellid_1, spelltrigger_1, spellid_2, spelltrigger_2, spellid_3, spelltrigger_3,
					spellid_4, spelltrigger_4, spellid_5, spelltrigger_5
				FROM item_template
				WHERE entry IN (?)
			`,
			values: [data.map((row) => row.itemEntry)],
			timeout: this.armory.config.dbQueryTimeout,
		});
		for (const row of rows as RowDataPacket[]) {
			const item = data.find((item) => item.itemEntry === row.entry);
			if (item === undefined) {
				continue;
			}
			item.quality = row.quality;
			item.name = row.name;
			item.itemLevel = row.ItemLevel;
			item.armor = row.armor ?? 0;
			item.requiredLevel = row.RequiredLevel ?? 0;
			item.maxDurability = row.MaxDurability ?? 0;
			item.inventoryType = row.InventoryType ?? 0;
			item.itemset = row.itemset ?? 0;
			const itemType = CharacterController.getItemType(item.classId, item.subclassId, item.inventoryType ?? 0);
			item.typeLeft = itemType.left;
			item.typeRight = itemType.right;
			item.resistance = {
				holy: row.holy_res ?? 0,
				fire: row.fire_res ?? 0,
				nature: row.nature_res ?? 0,
				frost: row.frost_res ?? 0,
				shadow: row.shadow_res ?? 0,
				arcane: row.arcane_res ?? 0,
			};
			item.stats = [];
			for (let i = 1; i <= 10; i++) {
				const type = row[`stat_type${i}`];
				const value = row[`stat_value${i}`];
				if (type !== undefined && value) {
					item.stats.push({ type, value });
				}
			}

			// Gem sockets (socketColor: 1 = Meta, 2 = Red, 4 = Yellow, 8 = Blue).
			item.sockets = [];
			for (let i = 1; i <= 3; i++) {
				const color = row[`socketColor_${i}`];
				if (color) {
					item.sockets.push(color);
				}
			}

			// Classic gear delivers spell power / hit / crit through on-equip spell
			// auras (spelltrigger = 1) rather than the stat columns; decode them here.
			const ITEM_SPELLTRIGGER_ON_EQUIP = 1;
			for (let i = 1; i <= 5; i++) {
				if (row[`spelltrigger_${i}`] !== ITEM_SPELLTRIGGER_ON_EQUIP) {
					continue;
				}
				const effects = this.spellStatEffects[row[`spellid_${i}`]];
				if (effects !== undefined) {
					item.stats.push(...effects);
				}
			}
		}

		await this.attachItemSets(data);

		return data;
	}

	private static readonly inventoryTypeNames: { [key: number]: string } = {
		1: "Head", 2: "Neck", 3: "Shoulder", 4: "Shirt", 5: "Chest", 6: "Waist", 7: "Legs",
		8: "Feet", 9: "Wrist", 10: "Hands", 11: "Finger", 12: "Trinket", 13: "One-Hand",
		14: "Shield", 15: "Ranged", 16: "Back", 17: "Two-Hand", 19: "Tabard", 20: "Chest",
		21: "Main Hand", 22: "Off Hand", 23: "Held In Off-hand", 25: "Thrown", 26: "Ranged", 28: "Relic",
	};
	private static readonly armorSubclassNames: { [key: number]: string } = {
		1: "Cloth", 2: "Leather", 3: "Mail", 4: "Plate", 6: "Shield",
		7: "Libram", 8: "Idol", 9: "Totem", 10: "Sigil",
	};
	private static readonly weaponSubclassNames: { [key: number]: string } = {
		0: "Axe", 1: "Axe", 2: "Bow", 3: "Gun", 4: "Mace", 5: "Mace", 6: "Polearm",
		7: "Sword", 8: "Sword", 10: "Staff", 13: "Fist Weapon", 15: "Dagger",
		16: "Thrown", 18: "Crossbow", 19: "Wand", 20: "Fishing Pole",
	};

	// Returns the WoW tooltip "type" line, split into a left (slot/hand) and
	// right (material/weapon type) part, e.g. { left: "Chest", right: "Cloth" }.
	private static getItemType(classId: number, subclassId: number, inventoryType: number): { left: string; right: string } {
		const slot = CharacterController.inventoryTypeNames[inventoryType] ?? "";
		if (classId === 2) {
			// Weapons: left is the hand, right is the weapon type.
			return { left: slot, right: CharacterController.weaponSubclassNames[subclassId] ?? "" };
		}
		if (classId === 4) {
			// Armor: left is the slot, right is the material (omit for misc).
			return { left: slot, right: CharacterController.armorSubclassNames[subclassId] ?? "" };
		}
		return { left: slot, right: "" };
	}

	private async attachItemSets(items: IEquipmentData[]): Promise<void> {
		const setIds = [...new Set(items.filter((i) => i.itemset).map((i) => i.itemset as number))];
		if (setIds.length === 0) {
			return;
		}

		const equippedEntries = new Set(items.map((i) => i.itemEntry));

		// Full roster of each set (every item that belongs to it).
		const [memberRows] = await this.armory.worldDb.query<RowDataPacket[]>({
			sql: `SELECT entry, name, itemset FROM item_template WHERE itemset IN (?)`,
			values: [setIds],
			timeout: this.armory.config.dbQueryTimeout,
		});
		const membersBySet: { [key: number]: { name: string; owned: boolean }[] } = {};
		for (const row of memberRows as RowDataPacket[]) {
			(membersBySet[row.itemset] = membersBySet[row.itemset] || []).push({
				name: row.name,
				owned: equippedEntries.has(row.entry),
			});
		}

		// Set display names.
		const setNames: { [key: number]: string } = {};
		const [nameRows] = await this.armory.worldDb.query<RowDataPacket[]>({
			sql: `SELECT entry, name FROM item_set_names WHERE entry IN (?)`,
			values: [setIds],
			timeout: this.armory.config.dbQueryTimeout,
		});
		for (const row of nameRows as RowDataPacket[]) {
			if (row.name) {
				setNames[row.entry] = row.name;
			}
		}

		// Set bonuses and proper set names come from ItemSet.dbc (data/dbc).
		const bonusesBySet: { [key: number]: { threshold: number; text: string }[] } = {};
		for (const setId of setIds) {
			const setDbc = await this.armory.dbc.itemSet().find((s) => s.id === setId);
			if (setDbc === undefined) {
				continue;
			}
			const spellIds = [
				setDbc.setSpellId0, setDbc.setSpellId1, setDbc.setSpellId2, setDbc.setSpellId3,
				setDbc.setSpellId4, setDbc.setSpellId5, setDbc.setSpellId6, setDbc.setSpellId7,
			];
			const thresholds = [
				setDbc.setThreshold0, setDbc.setThreshold1, setDbc.setThreshold2, setDbc.setThreshold3,
				setDbc.setThreshold4, setDbc.setThreshold5, setDbc.setThreshold6, setDbc.setThreshold7,
			];
			const list: { threshold: number; text: string }[] = [];
			for (let i = 0; i < spellIds.length; i++) {
				const spellId = spellIds[i];
				const threshold = thresholds[i];
				if (spellId && threshold) {
					const spell = await this.armory.dbc.spell().find((s) => s.id === spellId);
					list.push({ threshold, text: this.formatSpellDescription(spell) || (spell?.nameLang0 ?? "") });
				}
			}
			list.sort((a, b) => a.threshold - b.threshold);
			bonusesBySet[setId] = list;
			if (!setNames[setId] && setDbc.nameLang) {
				setNames[setId] = setDbc.nameLang;
			}
		}

		for (const setId of setIds) {
			const members = membersBySet[setId] || [];
			if (members.length === 0) {
				continue;
			}
			const ownedCount = members.filter((m) => m.owned).length;
			const name = setNames[setId] || CharacterController.commonPrefixName(members.map((m) => m.name)) || "Item Set";
			const bonuses = (bonusesBySet[setId] || []).map((b) => ({ ...b, active: ownedCount >= b.threshold }));

			const set = { name, ownedCount, totalCount: members.length, members, bonuses };
			for (const item of items) {
				if (item.itemset === setId) {
					item.set = set;
				}
			}
		}
	}

	// Best-effort set name when the proper name table has no entry: the shared
	// word prefix of the member item names (e.g. "Plagueheart Robe" -> "Plagueheart").
	private static commonPrefixName(names: string[]): string {
		if (names.length === 0) {
			return "";
		}
		let prefix = names[0];
		for (const n of names) {
			let i = 0;
			while (i < prefix.length && i < n.length && prefix[i] === n[i]) {
				i++;
			}
			prefix = prefix.slice(0, i);
		}
		// Drop a trailing partial word.
		if (prefix.length && !/\s$/.test(prefix) && /\s/.test(prefix)) {
			prefix = prefix.slice(0, prefix.lastIndexOf(" "));
		}
		return prefix.trim();
	}

	private async getMounts(realm: string, charGuid: number): Promise<IMount[]> {
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT spell
				FROM character_spell
				WHERE guid = ? AND spell IN (?)
			`,
			values: [charGuid, this.mountSpells],
			timeout: this.armory.config.dbQueryTimeout,
		});

		return (rows as RowDataPacket[]).map((row) => this.mountBySpellId[row.spell]).filter((m) => m !== undefined);
	}

	private async getCompanions(realm: string, charGuid: number): Promise<ICompanion[]> {
		if (this.companionSpells.length === 0) {
			return [];
		}
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT spell
				FROM character_spell
				WHERE guid = ? AND spell IN (?)
			`,
			values: [charGuid, this.companionSpells],
			timeout: this.armory.config.dbQueryTimeout,
		});

		const seen = new Set<number>();
		const companions: ICompanion[] = [];
		for (const row of rows as RowDataPacket[]) {
			const companion = this.companionBySpellId[row.spell];
			if (companion !== undefined && !seen.has(companion.spell)) {
				seen.add(companion.spell);
				companions.push(companion);
			}
		}
		companions.sort((a, b) => a.name.localeCompare(b.name));
		return companions;
	}

	private async getModelViewerItems(equipmentData: IEquipmentData[], charClass: number, transmogOut?: number[][]): Promise<number[][]> {
		if (charClass !== 3) {
			// Keep ranged weapon only if the character is a hunter
			equipmentData = equipmentData.filter((row) => row.slot !== 17);
		}
		const visibleEquipment = equipmentData.filter(
			(item) =>
				[0, 2, 3, 4, 5, 6, 7, 8, 9, 14, 15, 16, 17, 18].includes(item.slot) && // visible slots
				item.itemEntry !== 5976, // filter out Guild Tabard (displays blank otherwise)
		);

		const items: number[][] = [];
		for (const equipment of visibleEquipment) {
			const modifiedAppearance = await this.armory.dbc.itemModifiedAppearance().find((row) => row.itemId === equipment.itemEntry);
			if (modifiedAppearance === undefined) {
				continue;
			}
			let appearance = await this.armory.dbc.itemAppearance().find((row) => row.id === modifiedAppearance.itemAppearanceId);
			if (appearance === undefined) {
				continue;
			}
			appearance = { ...appearance };

			let invType = this.itemInventoryTypes[equipment.itemEntry];
			items.push([invType, appearance.itemDisplayInfoId]);

			if (transmogOut !== undefined) {
				if (equipment.transmog !== undefined) {
					if (equipment.transmog === 1) {
						appearance.itemDisplayInfoId = -1; // Hidden gear piece from transmog
					} else {
						const modifiedAppearance = await this.armory.dbc.itemModifiedAppearance().find((row) => row.itemId === equipment.transmog);
						if (modifiedAppearance !== undefined) {
							const tmogAppearance = await this.armory.dbc.itemAppearance().find((row) => row.id === modifiedAppearance.itemAppearanceId);
							if (tmogAppearance !== undefined) {
								appearance = tmogAppearance;
								invType = this.itemInventoryTypes[equipment.transmog];
							}
						}
					}
				}
				transmogOut.push([invType, appearance.itemDisplayInfoId]);
			}
		}

		return items;
	}

	private parseEnchantmentsString(enchantments: string): number[] {
		return enchantments
			.trim()
			.split(" ")
			.map((enchant) => parseInt(enchant))
			.filter((enchant) => enchant !== 0);
	}

	private getGemsFromEnchantments(enchantments: string): number[] {
		return this.parseEnchantmentsString(enchantments)
			.filter((enchant) => enchant in this.enchantSrcItems && this.enchantSrcItems[enchant] in this.gemItems)
			.map((enchant) => this.enchantSrcItems[enchant]);
	}

	private filterEnchantments(item: number, enchantments: string): number[] {
		const socketBonus = this.itemSocketBonuses[item];
		return this.parseEnchantmentsString(enchantments).filter(
			(enchant) => enchant in this.enchantSrcItems && !(this.enchantSrcItems[enchant] in this.gemItems) && enchant !== socketBonus,
		);
	}

	private getCustomizationOptions(charData: ICharacterData): ICustomizationOption[] {
		const data = this.armory.characterCustomization.getCharacterCustomizationData(charData.race, charData.gender) as any;
		const options: ICustomizationOption[] = [];
		// When the model-viewer data is not bundled (see CharacterCustomization), there are no
		// customization options to resolve; the page still renders without the 3D character model.
		if (data === null || data === undefined || !Array.isArray(data.Options)) {
			return options;
		}
		const setOptionByChoiceIndex = (optionName: string, choiceIndex: number | undefined) => {
			const option = data["Options"].find((opt: any) => opt.Name === optionName);
			if (option !== undefined) {
				const choice = option.Choices.find((choice: any) => choice.OrderIndex === choiceIndex);
				if (choice !== undefined) {
					options.push({ optionId: option.Id, choiceId: choice.Id });
				}
			}
		};
		const setOptionByChoiceName = (optionName: string, choiceName: string | undefined) => {
			const option = data["Options"].find((opt: any) => opt.Name === optionName);
			if (option !== undefined) {
				const choice = option.Choices.find((ch: any) => ch.Name === choiceName);
				if (choice !== undefined) {
					options.push({ optionId: option.Id, choiceId: choice.Id });
				}
			}
		};
		const setOptionByChoiceId = (optionName: string, choiceId: number | undefined) => {
			const option = data["Options"].find((opt: any) => opt.Name === optionName);
			if (option !== undefined && choiceId !== undefined) {
				options.push({ optionId: option.Id, choiceId: choiceId });
			}
		};

		const optionMapping: { [key: string]: number } = {
			Face: charData.face,
			"Skin Color": charData.skin,
			"Hair Style": charData.hairStyle,
			"Hair Color": charData.hairColor,
		};
		for (const optionName in optionMapping) {
			setOptionByChoiceIndex(optionName, optionMapping[optionName]);
		}

		// Race-specific customization options
		switch (charData.race) {
			case 1: // Human
				if (charData.gender === 0) {
					setOptionByChoiceName(
						"Mustache",
						{ 0: "Horseshoe", 1: "Brush", 2: "Horseshoe", 3: "None", 4: "Brush", 5: "Brush", 6: "Horseshoe", 7: "Brush", 8: "None" }[
							charData.facialStyle
						],
					);
					setOptionByChoiceName(
						"Beard",
						{ 0: "Short", 1: "Chin Puff", 2: "Soul Patch", 3: "Goatee", 4: "Goatee", 5: "None", 6: "Goatee", 7: "None", 8: "None" }[
							charData.facialStyle
						],
					);
					setOptionByChoiceName(
						"Sideburns",
						{ 0: "Medium", 1: "None", 2: "None", 3: "Medium", 4: "Long", 5: "Long", 6: "None", 8: "None", 7: "None" }[charData.facialStyle],
					);
					setOptionByChoiceName("Eyebrows", "Natural");
					setOptionByChoiceName("Face Shape", "Narrow");
					setOptionByChoiceId(
						"Eye Color",
						{ 0: 4138, 1: 4140, 2: 4130, 3: 4136, 4: 4141, 5: 4134, 6: 4130, 7: 4138, 8: 4144, 9: 4135, 10: 4126, 11: 4136 }[charData.face],
					);
				} else {
					setOptionByChoiceIndex("Piercings", charData.facialStyle);
					setOptionByChoiceName("Eyebrows", "Natural");
					setOptionByChoiceName("Face Shape", "Narrow");
					setOptionByChoiceName("Makeup", "None");
					setOptionByChoiceName("Necklace", "None");
					setOptionByChoiceId(
						"Eye Color",
						{
							0: 4162,
							1: 4153,
							2: 4161,
							3: 4164,
							4: 4154,
							5: 4160,
							6: 4160,
							7: 4157,
							8: 4152,
							9: 4154,
							10: 4155,
							11: 4165,
							12: 4163,
							13: 4155,
							14: 4151,
						}[charData.face],
					);
				}
				if (charData.class === 6) {
					// Death Knight
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 4534 : 4535);
				}
				break;
			case 3: // Dwarf
				setOptionByChoiceName("Tattoo", "None");
				setOptionByChoiceIndex("Tattoo Color", 0);
				setOptionByChoiceIndex("Eyebrows", 0);
				if (charData.gender === 0) {
					setOptionByChoiceName(
						"Mustache",
						{
							0: "Trimmed",
							1: "Bushy",
							2: "Grand",
							3: "Thin Braids",
							4: "Wise",
							5: "Thick Braids",
							6: "Fancy",
							7: "Bold",
							8: "Tied",
							9: "None",
							10: "None",
						}[charData.facialStyle],
					);
					setOptionByChoiceIndex("Beard", charData.facialStyle);
					setOptionByChoiceName("Earrings", "None");
					setOptionByChoiceName("Nose Ring", "None");
					setOptionByChoiceIndex("Eye Color", 0); // TODO
				} else {
					setOptionByChoiceIndex("Earrings", { 0: 0, 1: 1, 2: 2, 3: 3, 4: 0, 5: 4 }[charData.facialStyle]);
					setOptionByChoiceName(
						"Piercings",
						{ 0: "None", 1: "None", 2: "None", 3: "None", 4: "Right Nostril", 5: "None" }[charData.facialStyle],
					);
					setOptionByChoiceIndex("Eye Color", 0); // TODO
				}
				if (charData.class === 6) {
					// Death Knight
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 5559 : 5587);
				}
				break;
			case 7: // Gnome
				if (charData.gender === 0) {
					setOptionByChoiceIndex("Mustache", charData.facialStyle > 1 ? charData.facialStyle - 1 : 0);
					setOptionByChoiceIndex("Beard", charData.facialStyle < 7 ? charData.facialStyle : 0);
					setOptionByChoiceIndex("Eyebrows", charData.facialStyle < 6 ? charData.facialStyle : 1);
					setOptionByChoiceIndex("Eye Color", 0); // TODO
				} else {
					setOptionByChoiceIndex("Earrings", charData.facialStyle);
					setOptionByChoiceId("Earring Color", 8796);
					setOptionByChoiceIndex("Eye Color", 0); // TODO
				}
				if (charData.class === 6) {
					// Death Knight
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 5629 : 5643);
				}
				break;
			case 4: // Night Elf
				setOptionByChoiceName("Vines", "None");
				setOptionByChoiceIndex("Vine Color", 0);
				setOptionByChoiceName("Ears", "Thin");
				setOptionByChoiceName("Scars", "None");
				if (charData.gender === 0) {
					setOptionByChoiceName(
						"Sideburns",
						{ 0: "None", 1: "Groomed", 2: "None", 3: "Short", 4: "Medium", 5: "Groomed" }[charData.facialStyle],
					);
					setOptionByChoiceName("Mustache", { 0: "None", 1: "Groomed", 2: "None", 3: "Thin", 4: "None", 5: "None" }[charData.facialStyle]);
					setOptionByChoiceName("Beard", { 0: "None", 1: "Trimmed", 2: "Full", 3: "None", 4: "Short", 5: "Long" }[charData.facialStyle]);
					setOptionByChoiceName("Eyebrows", { 0: "Shaved", 1: "Short", 2: "Long", 3: "Flat", 4: "Short", 5: "Owl" }[charData.facialStyle]);
				} else {
					setOptionByChoiceName("Eyebrows", "Long");
					setOptionByChoiceIndex("Markings", charData.facialStyle + 1);
					setOptionByChoiceIndex("Markings Color", { 0: 1, 1: 2, 2: 3, 3: 4, 4: 5, 5: 3, 6: 6, 7: 7 }[charData.hairColor]);
				}
				setOptionByChoiceName("Blindfold", "");
				setOptionByChoiceName("Headdress", "None");
				setOptionByChoiceName("Earrings", "None");
				setOptionByChoiceName("Nose Ring", "None");
				setOptionByChoiceName("Necklace", "None");
				setOptionByChoiceName("Horns", "None");
				setOptionByChoiceName("Tattoo", "None");
				setOptionByChoiceName("Tattoo Color", "None");
				if (charData.class === 6) {
					// Death Knight
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 7618 : 7634);
				} else {
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 7610 : 7619);
				}
				break;
			case 11: // Draenei
				setOptionByChoiceName("Circlet", "None");
				setOptionByChoiceId("Jewelry Color", charData.gender === 0 ? 8707 : 8646);
				setOptionByChoiceName("Horn Decoration", "None");
				setOptionByChoiceName("Tail", charData.gender === 0 ? "Long" : "Short");
				if (charData.gender === 0) {
					setOptionByChoiceName(
						"Facial Hair",
						{ 0: "Bare", 1: "Bare", 2: "Burns", 3: "Chops", 4: "Mustache", 5: "Soul Patch", 6: "Handlebar", 7: "Bare" }[
							charData.facialStyle
						],
					);
					setOptionByChoiceName(
						"Tendrils",
						{ 0: "None", 1: "Splayed", 2: "Double", 3: "Fanned", 4: "Single", 5: "Paired", 6: "Uniform", 7: "Twin" }[charData.facialStyle],
					);
				} else {
					setOptionByChoiceName(
						"Horns",
						{ 0: "Sweeping", 1: "Curled", 2: "Curved", 3: "Thick", 4: "Wide", 5: "Grand", 6: "Short" }[charData.facialStyle],
					);
				}
				if (charData.class === 6) {
					// Death Knight
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 6977 : 6979);
				} else {
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 6976 : 6978);
				}
				break;
			case 2: // Orc
				setOptionByChoiceName("Scars", "None");
				setOptionByChoiceName("Grime", "None");
				setOptionByChoiceName("Tattoo", "None");
				setOptionByChoiceName("War Paint", "None");
				setOptionByChoiceName("War Paint Color", "None");
				if (charData.gender === 0) {
					setOptionByChoiceName(
						"Beard",
						{
							0: "None",
							1: "Stubble",
							2: "Thick",
							3: "Full",
							4: "Tied",
							5: "Braid",
							6: "Twin Braids",
							7: "None",
							8: "Ringed",
							9: "Split",
							10: "Goatee",
						}[charData.facialStyle],
					);
					setOptionByChoiceName(
						"Sideburns",
						{ 0: "None", 1: "None", 2: "Full", 3: "Low", 4: "Full", 5: "None", 6: "None", 7: "Braids", 8: "None", 9: "Full", 10: "Thick" }[
							charData.facialStyle
						],
					);
					setOptionByChoiceName("Earrings", "None");
					setOptionByChoiceName("Nose Ring", "None");
					setOptionByChoiceName("Tusks", "Natural");
					setOptionByChoiceName("Upright", "Hunched");
					setOptionByChoiceIndex("Eye Color", 0); // TODO
				} else {
					setOptionByChoiceIndex("Earrings", { 0: 0, 1: 1, 2: 2, 3: 0, 4: 1, 5: 2, 6: 4 }[charData.facialStyle]);
					setOptionByChoiceIndex("Nose Ring", { 0: 0, 1: 0, 2: 0, 3: 1, 4: 1, 5: 1, 6: 0 }[charData.facialStyle]);
					setOptionByChoiceName("Necklace", "None");
					setOptionByChoiceIndex("Eye Color", 0); // TODO
				}
				if (charData.class === 6) {
					// Death Knight
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 9289 : 9313);
				}
				break;
			case 5: // Undead
				setOptionByChoiceName("Skin Type", "Bony");
				if (charData.gender === 0) {
					setOptionByChoiceName(
						"Jaw Features",
						{
							0: "Intact",
							1: "Rot-Kissed",
							2: "Intact",
							3: "Slackjawed",
							4: "Drooler",
							5: "Intact",
							6: "Slackjawed",
							7: "Drooler",
							8: "Bonejawed",
							9: "Jawsome",
							10: "Toothy",
							11: "Unhinged",
							12: "Cheeky",
							13: "Loose",
							14: "Intact",
							15: "Slackjawed",
							16: "Slobber",
						}[charData.facialStyle],
					);
					setOptionByChoiceIndex(
						"Face Features",
						{ 0: 0, 1: 0, 2: 1, 3: 1, 4: 1, 5: 2, 6: 3, 7: 3, 8: 0, 9: 0, 10: 0, 11: 0, 12: 0, 13: 0, 14: 4, 15: 4, 16: 0 }[
							charData.facialStyle
						],
					);
					setOptionByChoiceId(
						"Eye Color",
						{
							0: 5330,
							1: 5330,
							2: 6304,
							3: 6304,
							4: 6304,
							5: 5330,
							6: 5330,
							7: 5330,
							8: 5330,
							9: 5330,
							10: 6304,
							11: 6304,
							12: 5330,
							13: 5330,
							14: 5330,
							15: 5330,
							16: 5330,
						}[charData.facialStyle],
					);
				} else {
					setOptionByChoiceName(
						"Face Features",
						{ 0: "None", 1: "None", 2: "Strapped", 3: "Rotting", 4: "None", 5: "None", 6: "None", 7: "Putrid" }[charData.facialStyle],
					);
					setOptionByChoiceName(
						"Jaw Features",
						{ 0: "Intact", 1: "Stitched", 2: "Intact", 3: "Intact", 4: "Bonejawed", 5: "Toothy", 6: "Cheeky", 7: "Intact" }[
							charData.facialStyle
						],
					);
					setOptionByChoiceId(
						"Eye Color",
						{ 0: 5337, 1: 5337, 2: 6305, 3: 5337, 4: 5337, 5: 6305, 6: 5337, 7: 5337 }[charData.facialStyle],
					);
				}
				if (charData.class === 6) {
					// Death Knight
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 5344 : 5345);
				}
				break;
			case 6: // Tauren
				setOptionByChoiceIndex("Horn Style", charData.hairStyle);
				setOptionByChoiceIndex("Horn Color", charData.hairColor);
				setOptionByChoiceName("Foremane", "Short");
				setOptionByChoiceName("Face Paint", "None");
				setOptionByChoiceName("Headdress", "None");
				setOptionByChoiceName("Necklace", "None");
				setOptionByChoiceIndex("Jewelry Color", 0);
				setOptionByChoiceName("Flower", "None");
				setOptionByChoiceName("Body Paint", "None");
				setOptionByChoiceIndex("Paint Color", 0);
				if (charData.gender === 0) {
					setOptionByChoiceName(
						"Hair",
						{ 0: "Mane", 1: "Braids", 2: "Chops", 3: "Sideburns", 4: "Mane", 5: "Wrapped", 6: "Braids" }[charData.facialStyle],
					);
					setOptionByChoiceName(
						"Facial Hair",
						{ 0: "Clean", 1: "Braid", 2: "Beard", 3: "Wrapped", 4: "Curtain", 5: "Clean", 6: "Split" }[charData.facialStyle],
					);
					setOptionByChoiceName(
						"Nose Ring",
						{ 0: "None", 1: "Small", 2: "Open", 3: "None", 4: "None", 5: "Bead", 6: "Open" }[charData.facialStyle],
					);
					setOptionByChoiceIndex("Eye Color", 0); // TODO
				} else {
					setOptionByChoiceIndex("Hair", charData.facialStyle);
					setOptionByChoiceName("Earrings", "None");
					setOptionByChoiceName("Nose Ring", "None");
					setOptionByChoiceIndex("Eye Color", 0); // TODO
				}
				if (charData.class === 6) {
					// Death Knight
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 7281 : 7289);
				}
				break;
			case 8: // Troll
				setOptionByChoiceName("Body Paint", "None");
				setOptionByChoiceName("Body Paint Color", "None");
				setOptionByChoiceName("Piercing", "None");
				if (charData.gender === 0) {
					setOptionByChoiceName(
						"Tusks",
						{
							0: "Tusked",
							1: "Gougers",
							2: "Mammoth",
							3: "Spears",
							4: "Bridle",
							5: "Tusked",
							6: "Gougers",
							7: "Mammoth",
							8: "Spears",
							9: "Bridle",
							10: "Gougers",
						}[charData.facialStyle],
					);
					setOptionByChoiceName(
						"Face Paint",
						{
							0: "None",
							1: "None",
							2: "None",
							3: "None",
							4: "None",
							5: "Berserker",
							6: "Fangs",
							7: "Mask",
							8: "Oni",
							9: "Prophet",
							10: "War",
						}[charData.facialStyle],
					);
					setOptionByChoiceIndex("Face Paint Color", charData.hairColor + 1);
					setOptionByChoiceName("Earrings", "None");
					setOptionByChoiceIndex("Eye Color", 0); // TODO
				} else {
					setOptionByChoiceIndex("Tusks", charData.facialStyle);
					setOptionByChoiceName("Face Paint", "None");
					setOptionByChoiceIndex("Face Paint Color", 0);
					setOptionByChoiceName("Earrings", "Hoops");
					setOptionByChoiceIndex("Eye Color", 0); // TODO
				}
				if (charData.class === 6) {
					// Death Knight
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 8451 : 8468);
				}
				break;
			case 10: // Blood Elf
				setOptionByChoiceName("Ears", "Long");
				setOptionByChoiceName("Horns", "None");
				setOptionByChoiceName("Blindfold", "None");
				setOptionByChoiceName("Tattoo", "None");
				setOptionByChoiceIndex("Tattoo Color", 0);
				if (charData.gender === 0) {
					setOptionByChoiceIndex("Facial Hair", charData.facialStyle);
				} else {
					setOptionByChoiceIndex("Earrings", charData.facialStyle);
					setOptionByChoiceIndex("Jewelry Color", 0);
					setOptionByChoiceName("Necklace", "None");
					setOptionByChoiceName("Armbands", "None");
					setOptionByChoiceName("Bracelets", "None");
				}
				if (charData.class === 6) {
					// Death Knight
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 6586 : 6605);
				} else {
					setOptionByChoiceId("Eye Color", charData.gender === 0 ? 6570 : 6589);
				}
				break;
		}
		if ([4, 6].includes(charData.race)) {
			// Races that can choose the druid class
			setOptionByChoiceIndex("Bear Form", 0);
			setOptionByChoiceIndex("Cat Form", 0);
			setOptionByChoiceIndex("Aquatic Form", 0);
			setOptionByChoiceIndex("Travel Form", 0);
			setOptionByChoiceIndex("Flight Form", 0);
			setOptionByChoiceIndex("Moonkin Form", 0);
		}

		return options;
	}

    private async getSkills(realm: string, character: number): Promise<ISkills[]> {
        const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
            sql: `
				SELECT skill, value, max
				FROM character_skills
				WHERE guid = ?
			`,
            values: [character],
            timeout: this.armory.config.dbQueryTimeout,
        });

        const skills: { id: number, categoryId: number, skill: string; value: number; max: number }[] = [];
        for (const row of rows as RowDataPacket[]) {
            skills.push({
                id: row.skill,
                categoryId: this.skillById[row.skill].categoryId,
                skill: this.skillById[row.skill].name,
                value: row.value,
                max: row.max,
            });
        }

        return skills;
    }

	private async getTalents(realm: string, character: number): Promise<number[][]> {
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT spell, specMask
				FROM character_talent
				WHERE guid = ?
			`,
			values: [character],
			timeout: this.armory.config.dbQueryTimeout,
		});

		const talents: number[][] = [[], []];
		for (const row of rows as RowDataPacket[]) {
			if (row.specMask === 1 || row.specMask === 3) {
				talents[0].push(row.spell);
			}
			if (row.specMask === 2 || row.specMask === 3) {
				talents[1].push(row.spell);
			}
		}

		return talents;
	}

	private async getTalentTrees(classId: number) {
		const items = await this.armory.dbc
			.talentTab()
			.filter((tab) => tab.classMask === Math.pow(2, classId - 1))
			.map(async (tab) => {
				const icon = await this.armory.dbc.spellIcon().find((icon) => icon.id === tab.spellIconId);
				const spells = await this.armory.dbc
					.talent()
					.filter((row) => row.tabId === tab.id)
					.map(async (row) => {
						const spell = await this.armory.dbc.spell().find((spell) => spell.id === row.spellRank0);
						const icon = await this.armory.dbc.spellIcon().find((icon) => icon.id === spell?.spellIconId);
						return {
							...row,
							icon: this.processSpellIconTexture(icon?.textureFilename ?? ""),
							name: spell?.nameLang0 ?? "",
							description: this.formatSpellDescription(spell),
						};
					})
					.toArray();
				return {
					name: tab.nameLang0,
					icon: this.processSpellIconTexture(icon?.textureFilename ?? ""),
					spells: await Promise.all(spells),
				};
			})
			.toArray();
		return await Promise.all(items);
	}

	private processSpellIconTexture(texturePath: string): string {
		return texturePath.toLowerCase().replace("interface\\icons\\", "").replace("interface\\spellbook\\", "").replace(/\.$/, "");
	}

	// Turns a raw WotLK spell description into readable text by resolving the
	// most common $-placeholders (effect base points) and stripping the rest.
	private formatSpellDescription(spell?: ISpellDbc): string {
		if (spell === undefined || !spell.descriptionLang0) {
			return "";
		}
		// $s/$m display the effect value (stored as actual - 1). Use the absolute
		// value for plain tokens (damage etc.) and the signed value inside math
		// blocks so divisions like ${$m2/-1000} come out correctly.
		const bpAbs = [
			Math.abs((spell.effectBasePoints0 ?? 0)) + 1,
			Math.abs((spell.effectBasePoints1 ?? 0)) + 1,
			Math.abs((spell.effectBasePoints2 ?? 0)) + 1,
		];
		const bpSigned = [(spell.effectBasePoints0 ?? 0) + 1, (spell.effectBasePoints1 ?? 0) + 1, (spell.effectBasePoints2 ?? 0) + 1];
		// Periodic tick interval ($t), in seconds.
		const periods = [
			Math.round((spell.effectAmplitude0 ?? 0) / 1000),
			Math.round((spell.effectAmplitude1 ?? 0) / 1000),
			Math.round((spell.effectAmplitude2 ?? 0) / 1000),
		];
		// Chain / jump targets ($x).
		const chainTargets = [spell.effectChainTargets0 ?? 0, spell.effectChainTargets1 ?? 0, spell.effectChainTargets2 ?? 0];
		const procChance = spell.procChance ?? 0;
		const procCharges = spell.procCharges ?? 0;
		const maxTargets = spell.maxTargets ?? 0;
		// Effect radius ($a), in yards, resolved through SpellRadius.dbc.
		const radii = [
			this.spellRadiusById[spell.effectRadiusIndex0 ?? 0] ?? 0,
			this.spellRadiusById[spell.effectRadiusIndex1 ?? 0] ?? 0,
			this.spellRadiusById[spell.effectRadiusIndex2 ?? 0] ?? 0,
		];
		// Duration ($d), resolved through SpellDuration.dbc (stored in ms).
		const durationMs = this.spellDurationById[spell.durationIndex ?? 0] ?? 0;

		const trimNum = (v: number): string => `${Math.round(v * 100) / 100}`;
		const formatDuration = (ms: number): string => {
			if (ms <= 0) {
				return "";
			}
			if (ms % 60000 === 0) {
				return `${ms / 60000} min`;
			}
			if (ms >= 60000) {
				return `${trimNum(ms / 60000)} min`;
			}
			return `${trimNum(ms / 1000)} sec`;
		};

		let text = spell.descriptionLang0;
		// Conditional tokens $?s12345[then][else] -> keep the first branch.
		text = text.replace(/\$\?[a-zA-Z]?\d+(\[[^\]]*\])(\[[^\]]*\])?/g, (_m, first) => first.slice(1, -1));
		// Gender/pluralisation tokens $gMale:Female; / $ldamage:damages; -> first form.
		text = text.replace(/\$[lgG]([^:;]*):[^;]*;/g, "$1");
		// Math blocks ${ expr }[.precision] -> evaluate, e.g. ${$m2/-1000}.1 -> 0.5.
		text = text.replace(/\$\{([^}]*)\}(?:\.(\d))?/g, (_m, expr: string, prec?: string) => {
			let e = expr
				.replace(/\$(?:\d+)?[sm]([1-3])/gi, (_x, idx) => `(${bpSigned[parseInt(idx, 10) - 1]})`)
				.replace(/\$(?:\d+)?t([1-3])/gi, (_x, idx) => `(${periods[parseInt(idx, 10) - 1]})`)
				.replace(/\$[a-zA-Z][a-zA-Z0-9]*/g, "0");
			if (!/^[-0-9+*/(). ]+$/.test(e)) {
				return "";
			}
			try {
				// eslint-disable-next-line no-new-func
				const v = Function(`"use strict";return (${e})`)();
				if (typeof v !== "number" || !isFinite(v)) {
					return "";
				}
				return prec !== undefined ? v.toFixed(parseInt(prec, 10)) : trimNum(v);
			} catch (err) {
				return "";
			}
		});
		// Division tokens $/divisor;s1 -> base points / divisor (cast-time / cooldown reductions).
		text = text.replace(/\$\/(\d+);(?:\d+)?[sm]([1-3])/gi, (_m, div, idx) => trimNum(bpAbs[parseInt(idx, 10) - 1] / parseInt(div, 10)));
		// Multiplier tokens $*6;s1 -> base points * multiplier.
		text = text.replace(/\$\*(\d+);(?:\d+)?[sm]([1-3])/gi, (_m, mult, idx) => trimNum(bpAbs[parseInt(idx, 10) - 1] * parseInt(mult, 10)));
		// $s1/$m1 and $<spellId>s1 -> effect base points of this spell.
		text = text.replace(/\$(?:\d+)?[sm]([1-3])/gi, (_m, idx) => `${bpAbs[parseInt(idx, 10) - 1]}`);
		// $t1 -> periodic tick interval (seconds).
		text = text.replace(/\$(?:\d+)?t([1-3])/gi, (_m, idx) => `${periods[parseInt(idx, 10) - 1]}`);
		// $x1 -> chain / jump targets.
		text = text.replace(/\$(?:\d+)?x([1-3])/gi, (_m, idx) => `${chainTargets[parseInt(idx, 10) - 1]}`);
		// $h -> proc chance, $n/$u -> charges, $i -> max targets.
		text = text.replace(/\$(?:\d+)?h\b/gi, `${procChance}`);
		text = text.replace(/\$(?:\d+)?[nu]\b/gi, `${procCharges}`);
		text = text.replace(/\$(?:\d+)?i\b/gi, `${maxTargets}`);
		// $a1 -> effect radius (yards); $d -> duration (with unit).
		text = text.replace(/\$(?:\d+)?a([1-3])?/gi, (_m, idx?: string) => {
			const r = radii[idx ? parseInt(idx, 10) - 1 : 0];
			return r ? trimNum(r) : "";
		});
		text = text.replace(/\$(?:\d+)?d\b/gi, () => formatDuration(durationMs));
		// Named variables $<percent>, multipliers $*9 -> drop.
		text = text.replace(/\$<[^>]*>/g, "");
		text = text.replace(/\$\*\d+/g, "");
		// Any remaining unresolved tokens -> drop.
		text = text.replace(/\$(?:\d+)?[a-zA-Z]\d*/g, "");
		text = text.replace(/\$\/\d+;\w+/g, "");

		// Tidy fragments left behind by dropped radius/duration tokens so the
		// sentence still reads naturally (e.g. "in  yds for ." -> ".").
		text = text
			.replace(/\bwithin\s+(?:yds?|yards?|meters?)\b/gi, "")
			.replace(/\bin\s+(?:a\s+)?(?:yds?|yards?|meters?)\b/gi, "")
			.replace(/\b(?:yds?|yards?)\s+radius\b/gi, "")
			.replace(/\b(?:for|over|within|lasting|lasts?|lasted)\s*(?=[.,;]|$)/gi, "");

		// Collapse whitespace and fix punctuation spacing.
		text = text
			.replace(/\(\s*\)/g, "")
			.replace(/\s{2,}/g, " ")
			.replace(/\s+([.,;%])/g, "$1")
			.replace(/\s+\)/g, ")")
			.replace(/\(\s+/g, "(")
			.trim();
		return text;
	}

	private async getGlyphs(realm: string, character: number): Promise<number[][]> {
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT guid, talentGroup, glyph1, glyph2, glyph3, glyph4, glyph5, glyph6
				FROM character_glyphs
				WHERE guid = ?
			`,
			values: [character],
			timeout: this.armory.config.dbQueryTimeout,
		});

		const glyphs: number[][] = [[], []];
		for (const row of rows as RowDataPacket[]) {
			const glyphIds = [row.glyph1, row.glyph2, row.glyph3, row.glyph4, row.glyph5, row.glyph6].filter((id) => id !== 0);
			for (const glyphId of glyphIds) {
				const glyph = await this.armory.dbc.glyphProperties().find((g) => g.id === glyphId);
				if (glyph === undefined) {
					continue;
				}
				glyphs[row.talentGroup].push(glyph.spellId);
			}
		}

		return glyphs;
	}

	private async getAchievements(
		realm: string,
		charData: ICharacterData,
	): Promise<{ achievements: IAchievement[]; earned: { [key: number]: number } }> {
		const promises = await this.armory.dbc
			.achievement()
			.filter((ach) => ach.faction === -1 || ach.faction === Utils.getFactionFromRaceId(charData.race))
			.map(async (ach) => {
				const icon = await this.armory.dbc.spellIcon().find((icon) => icon.id === ach.iconId);
				return {
					id: ach.id,
					category: ach.category,
					title: ach.titleLang0,
					description: ach.descriptionLang0,
					points: ach.points,
					icon: this.processSpellIconTexture(icon?.textureFilename ?? ""),
				};
			})
			.toArray();
		const achievements = await Promise.all(promises);

		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT achievement, date
				FROM character_achievement
				WHERE guid = ?
			`,
			values: [charData.guid],
			timeout: this.armory.config.dbQueryTimeout,
		});
		const earned: { [key: number]: number } = {};
		for (const row of rows as RowDataPacket[]) {
			earned[row.achievement] = row.date;
		}

		return {
			achievements,
			earned,
		};
	}

	private async getPvpKills(realm: string, charGuid: number): Promise<{ total: number; today: number; yesterday: number }> {
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT totalKills, todayKills, yesterdayKills
				FROM characters
				WHERE guid = ?
			`,
			values: [charGuid],
			timeout: this.armory.config.dbQueryTimeout,
		});
		const row = rows[0];

		return {
			total: row.totalKills,
			today: row.todayKills,
			yesterday: row.yesterdayKills,
		};
	}

	private async getPets(realm: string, charGuid: number): Promise<IPet[]> {
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT entry, name, level, modelid, PetType, slot
				FROM character_pet
				WHERE owner = ?
				ORDER BY slot ASC, level DESC
			`,
			values: [charGuid],
			timeout: this.armory.config.dbQueryTimeout,
		});

		const pets = rows as RowDataPacket[];
		if (pets.length === 0) {
			return [];
		}

		// Resolve the creature species names from the world DB.
		const entries = [...new Set(pets.map((p) => p.entry))];
		const speciesByEntry: { [key: number]: string } = {};
		const [speciesRows] = await this.armory.worldDb.query<RowDataPacket[]>({
			sql: "SELECT entry, name FROM creature_template WHERE entry IN (?)",
			values: [entries],
			timeout: this.armory.config.dbQueryTimeout,
		});
		for (const row of speciesRows as RowDataPacket[]) {
			speciesByEntry[row.entry] = row.name;
		}

		const petTypeName = (type: number): string => (type === 1 ? "Hunter Pet" : "Summon");
		const slotName = (slot: number): string => {
			if (slot >= 0 && slot <= 2) {
				return "Active";
			}
			return "Stabled";
		};

		return pets.map((row) => ({
			entry: row.entry,
			name: row.name,
			species: speciesByEntry[row.entry] ?? "Unknown",
			level: row.level,
			modelId: row.modelid,
			petType: petTypeName(row.PetType),
			slotName: slotName(row.slot),
		}));
	}

	private async getArenaTeams(realm: string, charGuid: number): Promise<IArenaTeam[]> {
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT
					arena_team.arenaTeamId AS id, arena_team.name, arena_team.type, arena_team.rating, arena_team.seasonWins, arena_team.seasonGames,
					arena_team.backgroundColor AS background, arena_team.emblemStyle, arena_team.emblemColor, arena_team.borderStyle, arena_team.borderColor
				FROM arena_team_member
				LEFT JOIN arena_team ON arena_team_member.arenaTeamId = arena_team.arenaTeamId
				WHERE guid = ?
				ORDER BY arena_team.type ASC
			`,
			values: [charGuid],
			timeout: this.armory.config.dbQueryTimeout,
		});

		return (rows as IArenaTeam[]).map((row) => {
			row.emblem = Utils.makeEmblemObject(row, false);
			return row;
		});
	}

    private async getAllCharacters(currentRealm: string): Promise<Array<{name: string, realmName: string, guid: number}>> {
        const [rows] = await this.armory.getCharactersDb(currentRealm).query<RowDataPacket[]>({
            sql: `SELECT name, guid
                FROM characters
                    LEFT JOIN acore_auth.account ON account.id = characters.account
                    LEFT JOIN acore_auth.account_access ON account_access.id = characters.account AND account_access.gmlevel > 0
                WHERE level > 1
                    AND account.username != 'AHBOT'
                    AND account_access.id IS NULL
                ORDER BY name ASC`,
            timeout: this.armory.config.dbQueryTimeout,
        });

        return (rows as RowDataPacket[]).map(row => ({
            name: row.name,
            guid: row.guid,
            realmName: currentRealm
        }));
    }

	public areItemsAvailable(): boolean {
		return Object.keys(this.itemIcons ?? {}).length > 0;
	}

	public getItemIconFile(itemEntry: number): string | null {
		const icon = this.itemIcons?.[itemEntry];
		if (icon === undefined) {
			return null;
		}
		return String(icon).toLowerCase();
	}

	public async lookupItemTemplates(
		entries: number[],
	): Promise<Map<number, { name: string; quality: number; itemLevel: number }>> {
		const result = new Map<number, { name: string; quality: number; itemLevel: number }>();
		if (entries.length === 0) {
			return result;
		}

		const [rows] = await this.armory.worldDb.query<RowDataPacket[]>({
			sql: "SELECT `entry`, `quality`, `name`, `ItemLevel` FROM `item_template` WHERE `entry` IN (?)",
			values: [entries],
			timeout: this.armory.config.dbQueryTimeout,
		});

		for (const row of rows) {
			result.set(row.entry, {
				name: row.name,
				quality: row.quality,
				itemLevel: row.ItemLevel ?? 0,
			});
		}
		return result;
	}
}
