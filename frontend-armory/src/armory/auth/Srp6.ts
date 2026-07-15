import { createHash, randomBytes, timingSafeEqual } from "crypto";

/**
 * WoW 3.3.5a SRP6 credential helper. Mirrors the backend's SrpHelper.cs so accounts created here can
 * log in to the game (and vice-versa). AzerothCore stores a 32-byte little-endian `salt` and
 * `verifier` in `acore_auth.account` (there is no `sha_pass_hash` in this build).
 *
 *   x        = SHA1( salt || SHA1("USERNAME:PASSWORD") )   (little-endian integer)
 *   verifier = g^x mod N                                   (32-byte little-endian)
 */

// SRP6 safe prime N and generator g used by WoW 3.3.5a.
const N = BigInt("0x894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7");
const G = 7n;
const KEY_LENGTH = 32;

function sha1(...buffers: Buffer[]): Buffer {
	const hash = createHash("sha1");
	for (const buffer of buffers) {
		hash.update(buffer);
	}
	return hash.digest();
}

/** Interprets a little-endian byte buffer as an unsigned BigInt. */
function leToBigInt(buffer: Buffer): bigint {
	let result = 0n;
	for (let i = buffer.length - 1; i >= 0; i--) {
		result = (result << 8n) | BigInt(buffer[i]);
	}
	return result;
}

/** Serializes a BigInt to a fixed-length little-endian buffer (zero-padded / truncated). */
function bigIntToLe(value: bigint, length: number): Buffer {
	const buffer = Buffer.alloc(length);
	let remaining = value;
	for (let i = 0; i < length; i++) {
		buffer[i] = Number(remaining & 0xffn);
		remaining >>= 8n;
	}
	return buffer;
}

function modPow(base: bigint, exponent: bigint, modulus: bigint): bigint {
	let result = 1n;
	let b = base % modulus;
	let e = exponent;
	while (e > 0n) {
		if (e & 1n) {
			result = (result * b) % modulus;
		}
		e >>= 1n;
		b = (b * b) % modulus;
	}
	return result;
}

function computeVerifier(username: string, password: string, salt: Buffer): Buffer {
	const identity = `${username.toUpperCase()}:${password.toUpperCase()}`;
	const identityHash = sha1(Buffer.from(identity, "utf8"));
	const x = leToBigInt(sha1(salt, identityHash));
	const verifier = modPow(G, x, N);
	return bigIntToLe(verifier, KEY_LENGTH);
}

/** Generates a fresh (salt, verifier) pair for a new account. */
export function generateCredentials(username: string, password: string): { salt: Buffer; verifier: Buffer } {
	const salt = randomBytes(KEY_LENGTH);
	const verifier = computeVerifier(username, password, salt);
	return { salt, verifier };
}

/**
 * Constant-time verification of a password against the stored SRP6 salt + verifier. Returns false for
 * malformed input rather than throwing, so callers can treat it as a simple boolean.
 */
export function verifyPassword(username: string, password: string, salt: Buffer, storedVerifier: Buffer): boolean {
	if (!Buffer.isBuffer(salt) || !Buffer.isBuffer(storedVerifier) || storedVerifier.length === 0) {
		return false;
	}

	const computed = computeVerifier(username, password, salt);
	// Pad to equal length so timingSafeEqual never throws on a short/long stored verifier.
	const length = Math.max(computed.length, storedVerifier.length);
	const a = Buffer.alloc(length);
	const b = Buffer.alloc(length);
	computed.copy(a);
	storedVerifier.copy(b);
	return timingSafeEqual(a, b);
}
