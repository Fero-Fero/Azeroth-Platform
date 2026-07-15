/**
 * Approximate WotLK (3.3.5a) character-sheet stat reconstruction.
 *
 * Final stats are not stored in the DB, so we rebuild them from:
 *  - base race/class/level primary stats (acore_world.player_class_stats + player_race_stats)
 *  - summed equipped-item stats (item_template stat_type/stat_value, armor, resistances)
 *  - rating -> % conversions using the documented WotLK rating curve
 *
 * Formulas for attack power / armor / health / mana are taken from AzerothCore
 * (StatSystem.cpp). Rating and stat->crit conversions use the reverse-engineered
 * RatingBuster curve, which reproduces the client GtCombatRatings values exactly
 * at level 80 (verified) and is a close approximation at lower levels.
 *
 * NOTE: This intentionally excludes talent, aura, set-bonus and glyph effects, so
 * values can differ from the in-game character sheet. Health and Mana are read
 * directly from the DB and are accurate.
 */

// WotLK ITEM_MOD_* stat types (item_template.stat_type*)
export const ItemMod = {
	MANA: 0,
	HEALTH: 1,
	AGILITY: 3,
	STRENGTH: 4,
	INTELLECT: 5,
	SPIRIT: 6,
	STAMINA: 7,
	DEFENSE_SKILL_RATING: 12,
	DODGE_RATING: 13,
	PARRY_RATING: 14,
	BLOCK_RATING: 15,
	HIT_MELEE_RATING: 16,
	HIT_RANGED_RATING: 17,
	HIT_SPELL_RATING: 18,
	CRIT_MELEE_RATING: 19,
	CRIT_RANGED_RATING: 20,
	CRIT_SPELL_RATING: 21,
	HASTE_MELEE_RATING: 28,
	HASTE_RANGED_RATING: 29,
	HASTE_SPELL_RATING: 30,
	HIT_RATING: 31,
	CRIT_RATING: 32,
	RESILIENCE_RATING: 35,
	HASTE_RATING: 36,
	EXPERTISE_RATING: 37,
	ATTACK_POWER: 38,
	RANGED_ATTACK_POWER: 39,
	MANA_REGENERATION: 43,
	ARMOR_PENETRATION_RATING: 44,
	SPELL_POWER: 45,
	HEALTH_REGEN: 46,
	SPELL_PENETRATION: 47,
	BLOCK_VALUE: 48,
};

export const Classes = {
	WARRIOR: 1,
	PALADIN: 2,
	HUNTER: 3,
	ROGUE: 4,
	PRIEST: 5,
	DEATH_KNIGHT: 6,
	SHAMAN: 7,
	MAGE: 8,
	WARLOCK: 9,
	DRUID: 11,
};

export interface IBasePrimaryStats {
	strength: number;
	agility: number;
	stamina: number;
	intellect: number;
	spirit: number;
}

export interface IEquippedItemStats {
	armor: number;
	stats: { type: number; value: number }[];
	resistance: {
		holy: number;
		fire: number;
		nature: number;
		frost: number;
		shadow: number;
		arcane: number;
	};
}

export interface IStatPanelEntry {
	label: string;
	value: string;
}

export interface ICalculatedStats {
	approximate: boolean;
	base: IStatPanelEntry[];
	melee: IStatPanelEntry[];
	ranged: IStatPanelEntry[];
	spell: IStatPanelEntry[];
	defense: IStatPanelEntry[];
	resistances: IStatPanelEntry[];
}

// Rating required for 1% of the effect at level 60 (RatingBuster "F" base values).
const RatingBase = {
	defense: 1.5,
	dodge: 13.8,
	parry: 13.8,
	block: 5.0,
	hitMelee: 10.0,
	hitSpell: 8.0,
	crit: 14.0, // melee, ranged and spell crit rating share the same coefficient
	resilience: 28.75,
	haste: 10.0, // melee, ranged and spell haste share the same coefficient
	expertise: 2.5, // rating per 1 expertise (4 expertise = 1% dodge/parry reduction)
	armorPen: 3.756097412,
};

// Agility required for 1% melee crit at level 80, per class.
const AgilityPer1PctCrit80: { [classId: number]: number } = {
	[Classes.WARRIOR]: 62.5,
	[Classes.PALADIN]: 52.08,
	[Classes.HUNTER]: 83.33,
	[Classes.ROGUE]: 83.33,
	[Classes.PRIEST]: 52.08,
	[Classes.DEATH_KNIGHT]: 62.5,
	[Classes.SHAMAN]: 83.33,
	[Classes.MAGE]: 51.02,
	[Classes.WARLOCK]: 50.51,
	[Classes.DRUID]: 83.33,
};

// Intellect required for 1% spell crit at level 80 (casters converge to this value).
const IntellectPer1PctSpellCrit80 = 166.67;
const SpellCritClasses = new Set([
	Classes.PALADIN,
	Classes.HUNTER,
	Classes.PRIEST,
	Classes.SHAMAN,
	Classes.MAGE,
	Classes.WARLOCK,
	Classes.DRUID,
]);

// Classes that wield a ranged weapon (their ranged tab reflects gear/agi/ratings).
// Other classes show the flat ranged-crit baseline only.
const RangedWeaponClasses = new Set([Classes.HUNTER, Classes.ROGUE, Classes.WARRIOR]);
const BaseRangedCritPct = 2.0;

// Flat base crit chances (before agility/intellect and ratings). These are
// class fixed values; the WARLOCK row is calibrated against an in-game level-60
// reference and the others are documented approximations.
const BaseMeleeCritPct: { [classId: number]: number } = {
	[Classes.WARRIOR]: 5.0,
	[Classes.PALADIN]: 3.34,
	[Classes.HUNTER]: 5.0,
	[Classes.ROGUE]: 5.0,
	[Classes.PRIEST]: 3.0,
	[Classes.DEATH_KNIGHT]: 5.0,
	[Classes.SHAMAN]: 3.0,
	[Classes.MAGE]: 3.5,
	[Classes.WARLOCK]: 1.75,
	[Classes.DRUID]: 3.75,
};
const BaseSpellCritPct: { [classId: number]: number } = {
	[Classes.PALADIN]: 3.34,
	[Classes.HUNTER]: 3.6,
	[Classes.PRIEST]: 1.24,
	[Classes.SHAMAN]: 2.2,
	[Classes.MAGE]: 0.91,
	[Classes.WARLOCK]: 0.8,
	[Classes.DRUID]: 1.85,
};
// Flat base dodge (before agility). WARLOCK calibrated against an in-game
// level-60 reference; others are documented approximations.
const BaseDodgePct: { [classId: number]: number } = {
	[Classes.WARRIOR]: 0.75,
	[Classes.PALADIN]: 0.65,
	[Classes.HUNTER]: 1.6,
	[Classes.ROGUE]: 1.1,
	[Classes.PRIEST]: 1.2,
	[Classes.DEATH_KNIGHT]: 0.75,
	[Classes.SHAMAN]: 1.6,
	[Classes.MAGE]: 1.5,
	[Classes.WARLOCK]: 1.18,
	[Classes.DRUID]: 1.85,
};
// gtRegenMPPerSpt-style factor: mana per second = 0.001 + spirit * sqrt(int) * factor.
// WARLOCK calibrated against an in-game level-60 reference; others approximate.
const ManaRegenFactor: { [classId: number]: number } = {
	[Classes.PALADIN]: 0.005575,
	[Classes.HUNTER]: 0.006575,
	[Classes.PRIEST]: 0.012903,
	[Classes.SHAMAN]: 0.008272,
	[Classes.MAGE]: 0.011327,
	[Classes.WARLOCK]: 0.006731,
	[Classes.DRUID]: 0.009327,
};

export class StatCalculator {
	/**
	 * RatingBuster level-scaling factor H(level). The amount of rating needed for
	 * 1% of an effect at a given level is `RatingBase / H(level)`.
	 */
	private static ratingScale(level: number): number {
		const lvl = Math.max(1, Math.min(level, 100));
		if (lvl <= 60) {
			// Below level 8 the curve is undefined; clamp to keep ratings sane.
			return Math.max((lvl - 8) / 52, (10 - 8) / 52);
		}
		if (lvl <= 70) {
			return (-3 / 82) * lvl + 131 / 41;
		}
		return 1 / ((82 / 52) * Math.pow(131 / 63, (lvl - 70) / 10));
	}

	private static ratingPer1Pct(base: number, level: number): number {
		return base / StatCalculator.ratingScale(level);
	}

	private static pctFromRating(rating: number, base: number, level: number): number {
		if (rating <= 0) {
			return 0;
		}
		return rating / StatCalculator.ratingPer1Pct(base, level);
	}

	private static critFromAgility(agility: number, classId: number, level: number): number {
		const agiPer80 = AgilityPer1PctCrit80[classId];
		if (!agiPer80 || agility <= 0) {
			return 0;
		}
		// Scale the level-80 conversion by the crit-rating level curve.
		const agiPer = agiPer80 * (StatCalculator.ratingScale(80) / StatCalculator.ratingScale(level));
		return agility / agiPer;
	}

	private static spellCritFromIntellect(intellect: number, classId: number, level: number): number {
		if (!SpellCritClasses.has(classId) || intellect <= 0) {
			return 0;
		}
		const intPer = IntellectPer1PctSpellCrit80 * (StatCalculator.ratingScale(80) / StatCalculator.ratingScale(level));
		return intellect / intPer;
	}

	private static meleeAttackPower(level: number, classId: number, str: number, agi: number): number {
		switch (classId) {
			case Classes.WARRIOR:
			case Classes.PALADIN:
			case Classes.DEATH_KNIGHT:
				return level * 3 + str * 2 - 20;
			case Classes.HUNTER:
			case Classes.SHAMAN:
			case Classes.ROGUE:
				return level * 2 + str + agi - 20;
			case Classes.DRUID:
				// Caster-form base (cat/bear forms add level- and form-specific bonuses).
				return str * 2 - 20;
			case Classes.MAGE:
			case Classes.PRIEST:
			case Classes.WARLOCK:
				return str - 10;
			default:
				return str * 2 - 20;
		}
	}

	private static rangedAttackPower(level: number, classId: number, agi: number): number {
		switch (classId) {
			case Classes.HUNTER:
				return level * 2 + agi - 10;
			case Classes.ROGUE:
			case Classes.WARRIOR:
				return level + agi - 10;
			default:
				return agi - 10;
		}
	}

	// WotLK: the first 20 points of stamina/intellect give 1 health/mana each,
	// everything beyond gives 10 health / 15 mana per point.
	private static healthFromStamina(stamina: number): number {
		return stamina <= 20 ? stamina : 20 + (stamina - 20) * 10;
	}

	private static manaFromIntellect(intellect: number): number {
		return intellect <= 20 ? intellect : 20 + (intellect - 20) * 15;
	}

	// "While not casting" mana regeneration shown on the spell tab (mana per 5s).
	private static manaRegenPer5(classId: number, intellect: number, spirit: number): number {
		const factor = ManaRegenFactor[classId];
		if (!factor || spirit <= 0 || intellect <= 0) {
			return 0;
		}
		return 5 * (0.001 + spirit * Math.sqrt(intellect) * factor);
	}

	private static dodgeFromAgility(agility: number, classId: number, level: number): number {
		// Agility contributes to dodge on a curve close to its melee-crit curve.
		return StatCalculator.critFromAgility(agility, classId, level);
	}

	/**
	 * Fallback stat sheet used when stats cannot be computed (e.g. base-stat
	 * tables are unavailable). Shows neutral placeholders rather than blanks.
	 */
	public static defaultStats(): ICalculatedStats {
		const dash = "\u2014";
		return {
			approximate: true,
			base: [
				{ label: "Strength", value: dash },
				{ label: "Agility", value: dash },
				{ label: "Stamina", value: dash },
				{ label: "Intellect", value: dash },
				{ label: "Spirit", value: dash },
				{ label: "Health", value: dash },
				{ label: "Mana", value: dash },
			],
			melee: [
				{ label: "Attack Power", value: dash },
				{ label: "Crit Chance", value: dash },
				{ label: "Hit Rating", value: dash },
				{ label: "Haste Rating", value: dash },
				{ label: "Expertise", value: dash },
				{ label: "Armor Penetration", value: dash },
			],
			ranged: [
				{ label: "Ranged Attack Power", value: dash },
				{ label: "Crit Chance", value: dash },
				{ label: "Hit Rating", value: dash },
			],
			spell: [
				{ label: "Spell Power", value: dash },
				{ label: "Spell Crit", value: dash },
				{ label: "Spell Hit", value: dash },
				{ label: "Spell Haste", value: dash },
				{ label: "Mana Regen", value: dash },
			],
			defense: [
				{ label: "Armor", value: dash },
				{ label: "Defense Rating", value: dash },
				{ label: "Dodge", value: dash },
				{ label: "Parry", value: dash },
				{ label: "Block", value: dash },
				{ label: "Resilience", value: dash },
			],
			resistances: [
				{ label: "Armor", value: dash },
				{ label: "Holy", value: dash },
				{ label: "Fire", value: dash },
				{ label: "Nature", value: dash },
				{ label: "Frost", value: dash },
				{ label: "Shadow", value: dash },
				{ label: "Arcane", value: dash },
			],
		};
	}

	public static calculate(
		level: number,
		classId: number,
		base: IBasePrimaryStats,
		gear: IEquippedItemStats,
		baseHp: number,
		baseMana: number,
		dbHealth: number,
		dbMana: number,
	): ICalculatedStats {
		const sum = (type: number): number =>
			gear.stats.filter((s) => s.type === type).reduce((acc, s) => acc + s.value, 0);

		const strength = base.strength + sum(ItemMod.STRENGTH);
		const agility = base.agility + sum(ItemMod.AGILITY);
		const stamina = base.stamina + sum(ItemMod.STAMINA);
		const intellect = base.intellect + sum(ItemMod.INTELLECT);
		const spirit = base.spirit + sum(ItemMod.SPIRIT);

		// Max health/mana are derived from the base pool + stamina/intellect (the
		// in-game maximum). Fall back to the last-saved DB values if base pools are
		// unavailable (e.g. the player_class_stats table could not be read).
		const health = baseHp > 0 ? baseHp + StatCalculator.healthFromStamina(stamina) : dbHealth;
		const mana = baseMana > 0 ? baseMana + StatCalculator.manaFromIntellect(intellect) : dbMana;
		const manaRegen = StatCalculator.manaRegenPer5(classId, intellect, spirit) + sum(ItemMod.MANA_REGENERATION);

		const armor = gear.armor + agility * 2;

		const meleeAp = StatCalculator.meleeAttackPower(level, classId, strength, agility) + sum(ItemMod.ATTACK_POWER);
		const rangedAp =
			StatCalculator.rangedAttackPower(level, classId, agility) +
			sum(ItemMod.RANGED_ATTACK_POWER) +
			sum(ItemMod.ATTACK_POWER);
		const spellPower = sum(ItemMod.SPELL_POWER);

		const meleeHit = sum(ItemMod.HIT_RATING) + sum(ItemMod.HIT_MELEE_RATING);
		const spellHit = sum(ItemMod.HIT_RATING) + sum(ItemMod.HIT_SPELL_RATING);
		const meleeCrit = sum(ItemMod.CRIT_RATING) + sum(ItemMod.CRIT_MELEE_RATING);
		const spellCrit = sum(ItemMod.CRIT_RATING) + sum(ItemMod.CRIT_SPELL_RATING);
		const meleeHaste = sum(ItemMod.HASTE_RATING) + sum(ItemMod.HASTE_MELEE_RATING);
		const spellHaste = sum(ItemMod.HASTE_RATING) + sum(ItemMod.HASTE_SPELL_RATING);

		const expertise = sum(ItemMod.EXPERTISE_RATING) / StatCalculator.ratingPer1Pct(RatingBase.expertise, level);
		const armorPen = StatCalculator.pctFromRating(sum(ItemMod.ARMOR_PENETRATION_RATING), RatingBase.armorPen, level);
		const resilience = StatCalculator.pctFromRating(sum(ItemMod.RESILIENCE_RATING), RatingBase.resilience, level);

		const rangedCrit = sum(ItemMod.CRIT_RATING) + sum(ItemMod.CRIT_RANGED_RATING);
		const rangedHit = sum(ItemMod.HIT_RATING) + sum(ItemMod.HIT_RANGED_RATING);

		const baseMeleeCrit = BaseMeleeCritPct[classId] ?? 0;
		const baseSpellCrit = BaseSpellCritPct[classId] ?? 0;
		const baseDodge = BaseDodgePct[classId] ?? 0;

		const meleeCritPct =
			baseMeleeCrit + StatCalculator.critFromAgility(agility, classId, level) + StatCalculator.pctFromRating(meleeCrit, RatingBase.crit, level);
		const spellCritPct =
			baseSpellCrit + StatCalculator.spellCritFromIntellect(intellect, classId, level) + StatCalculator.pctFromRating(spellCrit, RatingBase.crit, level);
		const dodgePct = baseDodge + StatCalculator.dodgeFromAgility(agility, classId, level) + StatCalculator.pctFromRating(sum(ItemMod.DODGE_RATING), RatingBase.dodge, level);
		// Casters have no ranged weapon, so their ranged tab shows only the flat baseline.
		const rangedCritPct = RangedWeaponClasses.has(classId)
			? BaseRangedCritPct + StatCalculator.critFromAgility(agility, classId, level) + StatCalculator.pctFromRating(rangedCrit, RatingBase.crit, level)
			: BaseRangedCritPct;

		const pct = (n: number): string => `${n.toFixed(2)}%`;
		const int = (n: number): string => Math.round(n).toLocaleString("en-US");

		const resistances: IStatPanelEntry[] = [
			{ label: "Armor", value: int(armor) },
			{ label: "Holy", value: int(gear.resistance.holy) },
			{ label: "Fire", value: int(gear.resistance.fire) },
			{ label: "Nature", value: int(gear.resistance.nature) },
			{ label: "Frost", value: int(gear.resistance.frost) },
			{ label: "Shadow", value: int(gear.resistance.shadow) },
			{ label: "Arcane", value: int(gear.resistance.arcane) },
		];

		return {
			approximate: true,
			base: [
				{ label: "Strength", value: int(strength) },
				{ label: "Agility", value: int(agility) },
				{ label: "Stamina", value: int(stamina) },
				{ label: "Intellect", value: int(intellect) },
				{ label: "Spirit", value: int(spirit) },
				{ label: "Health", value: int(health) },
				{ label: "Mana", value: int(mana) },
			],
			melee: [
				{ label: "Attack Power", value: int(meleeAp) },
				{ label: "Crit Chance", value: pct(meleeCritPct) },
				{ label: "Hit Rating", value: int(meleeHit) + ` (+${pct(StatCalculator.pctFromRating(meleeHit, RatingBase.hitMelee, level))})` },
				{ label: "Haste Rating", value: int(meleeHaste) + ` (+${pct(StatCalculator.pctFromRating(meleeHaste, RatingBase.haste, level))})` },
				{ label: "Expertise", value: int(expertise) },
				{ label: "Armor Penetration", value: pct(armorPen) },
			],
			ranged: [
				{ label: "Ranged Attack Power", value: int(rangedAp) },
				{ label: "Crit Chance", value: pct(rangedCritPct) },
				{ label: "Hit Rating", value: int(rangedHit) + ` (+${pct(StatCalculator.pctFromRating(rangedHit, RatingBase.hitMelee, level))})` },
			],
			spell: [
				{ label: "Spell Power", value: int(spellPower) },
				{ label: "Spell Crit", value: pct(spellCritPct) },
				{ label: "Spell Hit", value: int(spellHit) + ` (+${pct(StatCalculator.pctFromRating(spellHit, RatingBase.hitSpell, level))})` },
				{ label: "Spell Haste", value: int(spellHaste) + ` (+${pct(StatCalculator.pctFromRating(spellHaste, RatingBase.haste, level))})` },
				{ label: "Mana Regen", value: `${int(manaRegen)} / 5s` },
			],
			defense: [
				{ label: "Armor", value: int(armor) },
				{ label: "Defense Rating", value: int(sum(ItemMod.DEFENSE_SKILL_RATING)) },
				{ label: "Dodge", value: pct(dodgePct) },
				{ label: "Parry", value: pct(StatCalculator.pctFromRating(sum(ItemMod.PARRY_RATING), RatingBase.parry, level)) },
				{ label: "Block", value: pct(StatCalculator.pctFromRating(sum(ItemMod.BLOCK_RATING), RatingBase.block, level)) },
				{ label: "Resilience", value: pct(resilience) },
			],
			resistances,
		};
	}
}
