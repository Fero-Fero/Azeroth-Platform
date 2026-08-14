import { Pool, RowDataPacket, ResultSetHeader } from "mysql2/promise";

export const PROFILE_TABLE = "armory_account_profile";
export const DISPLAY_NAME_MIN_LENGTH = 3;
export const DISPLAY_NAME_MAX_LENGTH = 32;
export const DISPLAY_NAME_REGEX = /^[A-Za-z0-9][A-Za-z0-9 -]{1,30}[A-Za-z0-9]$/;

export interface AccountProfile {
	accountId: number;
	displayName: string;
	hideUsername: boolean;
	updatedAt: Date;
}

interface AccountProfileDbRow extends RowDataPacket {
	account_id: number;
	display_name: string;
	hide_username: number;
	updated_at: Date;
}

export function normalizeDisplayName(value: string): string {
	return value.trim().replace(/\s+/g, " ");
}

export function isValidDisplayName(value: string): boolean {
	const normalized = normalizeDisplayName(value);
	return (
		normalized.length >= DISPLAY_NAME_MIN_LENGTH &&
		normalized.length <= DISPLAY_NAME_MAX_LENGTH &&
		DISPLAY_NAME_REGEX.test(normalized)
	);
}

export function maskEmail(email: string): string {
	const trimmed = email.trim();
	const at = trimmed.indexOf("@");
	if (at <= 0) {
		return "***";
	}
	const local = trimmed.slice(0, at);
	const domain = trimmed.slice(at);
	const maskedLocal = local.length <= 1 ? "*" : `${local[0]}***`;
	return `${maskedLocal}${domain}`;
}

export class AccountProfileStore {
	public constructor(
		private readonly db: Pool,
		private readonly queryTimeout: number,
	) {}

	public async ensureTable(): Promise<void> {
		// Tables are created by the platform manager (root) during stack provisioning.
		await this.db.query({
			sql: `SELECT 1 FROM \`${PROFILE_TABLE}\` LIMIT 0`,
			timeout: this.queryTimeout,
		});
	}

	public async findByAccountId(accountId: number): Promise<AccountProfile | null> {
		const [rows] = await this.db.query<AccountProfileDbRow[]>({
			sql: `
				SELECT \`account_id\`, \`display_name\`, \`hide_username\`, \`updated_at\`
				FROM \`${PROFILE_TABLE}\`
				WHERE \`account_id\` = ?
				LIMIT 1
			`,
			values: [accountId],
			timeout: this.queryTimeout,
		});
		return rows.length > 0 ? mapRow(rows[0]) : null;
	}

	public async isDisplayNameTaken(displayName: string, excludeAccountId?: number): Promise<boolean> {
		const normalized = normalizeDisplayName(displayName);
		const values: (string | number)[] = [];
		let sql = `
			SELECT \`account_id\`
			FROM \`${PROFILE_TABLE}\`
			WHERE LOWER(\`display_name\`) = LOWER(?)
		`;
		values.push(normalized);
		if (excludeAccountId !== undefined) {
			sql += " AND `account_id` != ?";
			values.push(excludeAccountId);
		}
		sql += " LIMIT 1";

		const [rows] = await this.db.query<RowDataPacket[]>({
			sql,
			values,
			timeout: this.queryTimeout,
		});
		return rows.length > 0;
	}

	public async createProfile(accountId: number, displayName: string): Promise<AccountProfile> {
		const normalized = normalizeDisplayName(displayName);
		await this.db.query<ResultSetHeader>({
			sql: `
				INSERT INTO \`${PROFILE_TABLE}\` (\`account_id\`, \`display_name\`, \`hide_username\`)
				VALUES (?, ?, 1)
			`,
			values: [accountId, normalized],
			timeout: this.queryTimeout,
		});
		const profile = await this.findByAccountId(accountId);
		if (!profile) {
			throw new Error(`Failed to create profile for account ${accountId}`);
		}
		return profile;
	}

	public async updateDisplayName(accountId: number, displayName: string): Promise<void> {
		const normalized = normalizeDisplayName(displayName);
		// Only UPDATE on armory_account_profile (never acore_auth.account). Caller must verify JWT first.
		await this.db.query({
			sql: `
				UPDATE \`${PROFILE_TABLE}\`
				SET \`display_name\` = ?, \`updated_at\` = NOW()
				WHERE \`account_id\` = ?
			`,
			values: [normalized, accountId],
			timeout: this.queryTimeout,
		});
	}

	public async resolveAvailableDisplayName(baseName: string, accountId: number): Promise<string> {
		const normalized = normalizeDisplayName(baseName);
		if (!isValidDisplayName(normalized)) {
			return this.resolveAvailableDisplayName(`Player${accountId}`, accountId);
		}
		if (!(await this.isDisplayNameTaken(normalized, accountId))) {
			return normalized;
		}

		for (let suffix = 2; suffix <= 99; suffix++) {
			const candidate = normalizeDisplayName(`${normalized} ${suffix}`);
			if (
				candidate.length <= DISPLAY_NAME_MAX_LENGTH &&
				isValidDisplayName(candidate) &&
				!(await this.isDisplayNameTaken(candidate, accountId))
			) {
				return candidate;
			}
		}

		return `Player${accountId}`;
	}

	public async getOrCreate(accountId: number, preferredName: string): Promise<AccountProfile> {
		const existing = await this.findByAccountId(accountId);
		if (existing) {
			return existing;
		}
		const displayName = await this.resolveAvailableDisplayName(preferredName, accountId);
		return this.createProfile(accountId, displayName);
	}
}

function mapRow(row: AccountProfileDbRow): AccountProfile {
	return {
		accountId: row.account_id,
		displayName: row.display_name,
		hideUsername: row.hide_username === 1,
		updatedAt: row.updated_at,
	};
}
