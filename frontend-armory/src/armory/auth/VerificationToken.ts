import { createHash, randomBytes } from "crypto";

const TOKEN_BYTES = 32;

export function createVerificationToken(): { raw: string; hash: string } {
	const raw = randomBytes(TOKEN_BYTES).toString("hex");
	return { raw, hash: hashVerificationToken(raw) };
}

export function hashVerificationToken(raw: string): string {
	return createHash("sha256").update(raw, "utf8").digest("hex");
}
