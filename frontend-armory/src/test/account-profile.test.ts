import assert from "node:assert/strict";
import { describe, it } from "node:test";

import {
	isValidDisplayName,
	maskEmail,
	normalizeDisplayName,
} from "../armory/auth/AccountProfileStore";
import { formatBankMoney } from "../armory/services/GuildBankService";

describe("AccountProfileStore helpers", () => {
	it("normalizes display names", () => {
		assert.equal(normalizeDisplayName("  Hero   Name  "), "Hero Name");
	});

	it("accepts valid display names", () => {
		assert.equal(isValidDisplayName("Thrall"), true);
		assert.equal(isValidDisplayName("Player 42"), true);
	});

	it("rejects invalid display names", () => {
		assert.equal(isValidDisplayName("ab"), false);
		assert.equal(isValidDisplayName("bad_name"), false);
		assert.equal(isValidDisplayName(""), false);
	});

	it("masks email addresses", () => {
		assert.equal(maskEmail("fero@example.com"), "f***@example.com");
		assert.equal(maskEmail("a@test.com"), "*@test.com");
	});
});

describe("GuildBankService helpers", () => {
	it("formats bank money", () => {
		assert.deepEqual(formatBankMoney(1234567), {
			copper: 1234567,
			gold: 123,
			silver: 45,
			copperRemainder: 67,
			label: "123g 45s 67c",
		});
	});
});
