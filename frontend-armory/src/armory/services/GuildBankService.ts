import { RowDataPacket } from "mysql2/promise";

import { Armory } from "../Armory";
import { CharacterController } from "../controllers/CharacterController";

export const BANK_SLOTS_PER_TAB = 98;
export const BANK_GRID_COLUMNS = 14;

export interface GuildBankMoney {
	copper: number;
	gold: number;
	silver: number;
	copperRemainder: number;
	label: string;
}

export interface GuildBankTabInfo {
	tabId: number;
	name: string;
	icon: string;
	text: string;
}

export interface GuildBankSlotItem {
	slotId: number;
	itemEntry: number;
	icon: string;
	quality: number;
	name: string;
	itemLevel: number;
}

export interface GuildBankTabView {
	tabId: number;
	name: string;
	text: string;
	slots: (GuildBankSlotItem | null)[];
}

export interface GuildBankView {
	enabled: boolean;
	disabledReason: string | null;
	money: GuildBankMoney | null;
	tabs: GuildBankTabInfo[];
	activeTabId: number;
	activeTab: GuildBankTabView | null;
}

export class GuildBankService {
	public constructor(
		private readonly armory: Armory,
		private readonly items: CharacterController | null = null,
	) {}

	public static areItemAssetsAvailable(armory: Armory, items: CharacterController | null): boolean {
		if ((armory.config.assetProxyUrl ?? "").trim().length > 0) {
			return true;
		}
		return items?.areItemsAvailable() ?? false;
	}

	public async getView(
		realmName: string,
		guildId: number,
		accountId: number,
		tabId = 0,
	): Promise<GuildBankView> {
		const realm = this.armory.getRealm(realmName);
		if (!realm) {
			return this.disabledView("Realm not found.");
		}

		if (!(await this.isGuildMember(realm.name, guildId, accountId))) {
			return this.disabledView("You must be a member of this guild to view the bank.");
		}

		if (!(await this.armory.hasGuildBankTables(realm.name))) {
			return this.disabledView("Guild bank is not available on this realm.");
		}

		if (!GuildBankService.areItemAssetsAvailable(this.armory, this.items)) {
			return this.disabledView(
				"Guild bank requires armory item data — upload armory data on the platform.",
			);
		}

		const tabs = await this.getTabs(realm.name, guildId);
		if (tabs.length === 0) {
			return this.disabledView("Your guild has not purchased any bank tabs in-game yet.");
		}

		const activeTabId = tabs.some((tab) => tab.tabId === tabId) ? tabId : tabs[0].tabId;
		const money = await this.getBankMoney(realm.name, guildId);
		const activeTab = await this.getTabView(realm.name, guildId, activeTabId);

		return {
			enabled: true,
			disabledReason: null,
			money,
			tabs,
			activeTabId,
			activeTab,
		};
	}

	private disabledView(reason: string): GuildBankView {
		return {
			enabled: false,
			disabledReason: reason,
			money: null,
			tabs: [],
			activeTabId: 0,
			activeTab: null,
		};
	}

	private async isGuildMember(realm: string, guildId: number, accountId: number): Promise<boolean> {
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT 1
				FROM characters c
				INNER JOIN guild_member gm ON gm.guid = c.guid
				WHERE c.account = ? AND gm.guildid = ? AND c.deleteInfos_Account IS NULL
				LIMIT 1
			`,
			values: [accountId, guildId],
			timeout: this.armory.config.dbQueryTimeout,
		});
		return rows.length > 0;
	}

	private async getBankMoney(realm: string, guildId: number): Promise<GuildBankMoney> {
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: "SELECT `BankMoney` FROM `guild` WHERE `guildid` = ? LIMIT 1",
			values: [guildId],
			timeout: this.armory.config.dbQueryTimeout,
		});
		const copper = Number(rows[0]?.BankMoney ?? 0);
		return formatBankMoney(copper);
	}

	private async getTabs(realm: string, guildId: number): Promise<GuildBankTabInfo[]> {
		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT \`TabId\`, \`TabName\`, \`TabIcon\`, \`TabText\`
				FROM \`guild_bank_tab\`
				WHERE \`guildid\` = ?
				ORDER BY \`TabId\` ASC
			`,
			values: [guildId],
			timeout: this.armory.config.dbQueryTimeout,
		});

		return rows.map((row) => ({
			tabId: row.TabId,
			name: row.TabName || `Tab ${row.TabId + 1}`,
			icon: normalizeTabIcon(row.TabIcon),
			text: row.TabText ?? "",
		}));
	}

	private async getTabView(realm: string, guildId: number, tabId: number): Promise<GuildBankTabView> {
		const tabs = await this.getTabs(realm, guildId);
		const tab = tabs.find((entry) => entry.tabId === tabId) ?? tabs[0];
		const slots: (GuildBankSlotItem | null)[] = Array.from({ length: BANK_SLOTS_PER_TAB }, () => null);

		const [rows] = await this.armory.getCharactersDb(realm).query<RowDataPacket[]>({
			sql: `
				SELECT gbi.\`SlotId\`, ii.\`itemEntry\`
				FROM \`guild_bank_item\` gbi
				INNER JOIN \`item_instance\` ii ON ii.\`guid\` = gbi.\`item_guid\`
				WHERE gbi.\`guildid\` = ? AND gbi.\`TabId\` = ?
			`,
			values: [guildId, tabId],
			timeout: this.armory.config.dbQueryTimeout,
		});

		const entries = [...new Set(rows.map((row) => row.itemEntry as number))];
		const templates = this.items ? await this.items.lookupItemTemplates(entries) : new Map();

		for (const row of rows) {
			const slotId = row.SlotId as number;
			if (slotId < 0 || slotId >= BANK_SLOTS_PER_TAB) {
				continue;
			}
			const itemEntry = row.itemEntry as number;
			const template = templates.get(itemEntry);
			const iconFile = this.items?.getItemIconFile(itemEntry);
			if (!template || !iconFile) {
				continue;
			}
			slots[slotId] = {
				slotId,
				itemEntry,
				icon: iconFile,
				quality: template.quality,
				name: template.name,
				itemLevel: template.itemLevel,
			};
		}

		return {
			tabId: tab.tabId,
			name: tab.name,
			text: tab.text,
			slots,
		};
	}
}

export function formatBankMoney(copper: number): GuildBankMoney {
	const gold = Math.floor(copper / 10000);
	const silver = Math.floor((copper % 10000) / 100);
	const copperRemainder = copper % 100;
	return {
		copper,
		gold,
		silver,
		copperRemainder,
		label: `${gold}g ${silver}s ${copperRemainder}c`,
	};
}

function normalizeTabIcon(icon: unknown): string {
	const raw = String(icon ?? "").trim();
	if (!raw) {
		return "inv_misc_questionmark";
	}
	return raw.toLowerCase();
}
