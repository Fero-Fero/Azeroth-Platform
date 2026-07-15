import { Pool, RowDataPacket, ResultSetHeader } from "mysql2/promise";

import { normalizeEmail } from "./EmailAddress";

export const PENDING_TABLE = "armory_pending_registration";
export const VERIFICATION_EXPIRY_HOURS = 48;
export const MAX_RESENDS_PER_HOUR = 3;

export interface PendingRegistrationRow {
	id: number;
	email: string;
	salt: Buffer;
	verifier: Buffer;
	verificationTokenHash: string;
	expiresAt: Date;
	createdAt: Date;
	verifiedAt: Date | null;
	accountId: number | null;
	resendCount: number;
	resendWindowStartedAt: Date | null;
}

interface PendingRegistrationDbRow extends RowDataPacket {
	id: number;
	email: string;
	salt: Buffer;
	verifier: Buffer;
	verification_token_hash: string;
	expires_at: Date;
	created_at: Date;
	verified_at: Date | null;
	account_id: number | null;
	resend_count: number;
	resend_window_started_at: Date | null;
}

export class PendingRegistrationStore {
	public constructor(
		private readonly db: Pool,
		private readonly queryTimeout: number,
	) {}

	public async ensureTable(): Promise<void> {
		await this.db.query({
			sql: `
				CREATE TABLE IF NOT EXISTS \`${PENDING_TABLE}\` (
					\`id\` INT UNSIGNED NOT NULL AUTO_INCREMENT,
					\`email\` VARCHAR(255) NOT NULL,
					\`salt\` BINARY(32) NOT NULL,
					\`verifier\` BINARY(32) NOT NULL,
					\`verification_token_hash\` VARCHAR(64) NOT NULL,
					\`expires_at\` DATETIME NOT NULL,
					\`created_at\` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
					\`verified_at\` DATETIME NULL DEFAULT NULL,
					\`account_id\` INT UNSIGNED NULL DEFAULT NULL,
					\`resend_count\` TINYINT UNSIGNED NOT NULL DEFAULT 0,
					\`resend_window_started_at\` DATETIME NULL DEFAULT NULL,
					PRIMARY KEY (\`id\`),
					UNIQUE KEY \`ux_armory_pending_email\` (\`email\`)
				) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
			`,
			timeout: this.queryTimeout,
		});
	}

	public async hasAnyPending(): Promise<boolean> {
		const [rows] = await this.db.query<RowDataPacket[]>({
			sql: `SELECT 1 FROM \`${PENDING_TABLE}\` WHERE \`account_id\` IS NULL LIMIT 1`,
			timeout: this.queryTimeout,
		});
		return rows.length > 0;
	}

	public async findActiveByEmail(email: string): Promise<PendingRegistrationRow | null> {
		const normalized = normalizeEmail(email);
		const [rows] = await this.db.query<PendingRegistrationDbRow[]>({
			sql: `
				SELECT
					\`id\`, \`email\`, \`salt\`, \`verifier\`,
					\`verification_token_hash\`, \`expires_at\`, \`created_at\`,
					\`verified_at\`, \`account_id\`, \`resend_count\`, \`resend_window_started_at\`
				FROM \`${PENDING_TABLE}\`
				WHERE \`email\` = ? AND \`account_id\` IS NULL
				LIMIT 1
			`,
			values: [normalized],
			timeout: this.queryTimeout,
		});
		return rows.length > 0 ? mapRow(rows[0]) : null;
	}

	public async findById(id: number): Promise<PendingRegistrationRow | null> {
		const [rows] = await this.db.query<PendingRegistrationDbRow[]>({
			sql: `
				SELECT
					\`id\`, \`email\`, \`salt\`, \`verifier\`,
					\`verification_token_hash\`, \`expires_at\`, \`created_at\`,
					\`verified_at\`, \`account_id\`, \`resend_count\`, \`resend_window_started_at\`
				FROM \`${PENDING_TABLE}\`
				WHERE \`id\` = ?
				LIMIT 1
			`,
			values: [id],
			timeout: this.queryTimeout,
		});
		return rows.length > 0 ? mapRow(rows[0]) : null;
	}

	public async findByTokenHash(tokenHash: string): Promise<PendingRegistrationRow | null> {
		const [rows] = await this.db.query<PendingRegistrationDbRow[]>({
			sql: `
				SELECT
					\`id\`, \`email\`, \`salt\`, \`verifier\`,
					\`verification_token_hash\`, \`expires_at\`, \`created_at\`,
					\`verified_at\`, \`account_id\`, \`resend_count\`, \`resend_window_started_at\`
				FROM \`${PENDING_TABLE}\`
				WHERE \`verification_token_hash\` = ? AND \`account_id\` IS NULL
				LIMIT 1
			`,
			values: [tokenHash],
			timeout: this.queryTimeout,
		});
		return rows.length > 0 ? mapRow(rows[0]) : null;
	}

	public async isEmailTaken(email: string): Promise<boolean> {
		const normalized = normalizeEmail(email);
		const pending = await this.findActiveByEmail(normalized);
		if (pending) {
			return true;
		}

		const [accounts] = await this.db.query<RowDataPacket[]>({
			sql: `
				SELECT \`id\` FROM \`account\`
				WHERE LOWER(\`email\`) = ? OR LOWER(\`reg_mail\`) = ?
				LIMIT 1
			`,
			values: [normalized, normalized],
			timeout: this.queryTimeout,
		});
		return accounts.length > 0;
	}

	public async createPending(input: {
		email: string;
		salt: Buffer;
		verifier: Buffer;
		verificationTokenHash: string;
		expiresAt: Date;
	}): Promise<number> {
		const [result] = await this.db.query<ResultSetHeader>({
			sql: `
				INSERT INTO \`${PENDING_TABLE}\`
					(\`email\`, \`salt\`, \`verifier\`, \`verification_token_hash\`, \`expires_at\`)
				VALUES (?, ?, ?, ?, ?)
			`,
			values: [
				normalizeEmail(input.email),
				input.salt,
				input.verifier,
				input.verificationTokenHash,
				input.expiresAt,
			],
			timeout: this.queryTimeout,
		});
		return result.insertId;
	}

	public async markVerified(id: number): Promise<void> {
		await this.db.query({
			sql: `UPDATE \`${PENDING_TABLE}\` SET \`verified_at\` = NOW() WHERE \`id\` = ?`,
			values: [id],
			timeout: this.queryTimeout,
		});
	}

	public async rotateVerificationToken(id: number, tokenHash: string, expiresAt: Date): Promise<void> {
		await this.db.query({
			sql: `
				UPDATE \`${PENDING_TABLE}\`
				SET \`verification_token_hash\` = ?, \`expires_at\` = ?
				WHERE \`id\` = ?
			`,
			values: [tokenHash, expiresAt, id],
			timeout: this.queryTimeout,
		});
	}

	public async recordResend(id: number, resendCount: number, windowStartedAt: Date): Promise<void> {
		await this.db.query({
			sql: `
				UPDATE \`${PENDING_TABLE}\`
				SET \`resend_count\` = ?, \`resend_window_started_at\` = ?
				WHERE \`id\` = ?
			`,
			values: [resendCount, windowStartedAt, id],
			timeout: this.queryTimeout,
		});
	}

	public async linkAccount(id: number, accountId: number): Promise<void> {
		await this.db.query({
			sql: `UPDATE \`${PENDING_TABLE}\` SET \`account_id\` = ? WHERE \`id\` = ?`,
			values: [accountId, id],
			timeout: this.queryTimeout,
		});
	}

	public async deleteExpired(): Promise<void> {
		await this.db.query({
			sql: `
				DELETE FROM \`${PENDING_TABLE}\`
				WHERE \`account_id\` IS NULL AND \`expires_at\` < NOW()
			`,
			timeout: this.queryTimeout,
		});
	}
}

function mapRow(row: PendingRegistrationDbRow): PendingRegistrationRow {
	return {
		id: row.id,
		email: row.email,
		salt: row.salt,
		verifier: row.verifier,
		verificationTokenHash: row.verification_token_hash,
		expiresAt: row.expires_at,
		createdAt: row.created_at,
		verifiedAt: row.verified_at,
		accountId: row.account_id,
		resendCount: row.resend_count,
		resendWindowStartedAt: row.resend_window_started_at,
	}
}
