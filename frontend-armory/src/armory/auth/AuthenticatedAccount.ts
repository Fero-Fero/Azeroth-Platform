import { IAccountSession } from "./Session";

/**
 * Ensures a mutating armory action targets the authenticated account from a valid JWT session.
 * MySQL grants intentionally omit UPDATE/DELETE on acore_auth.account; profile edits use
 * armory_account_profile and must always pass this check before writing.
 */
export function assertAuthenticatedAccountWrite(
	session: IAccountSession,
	targetAccountId: number,
): void {
	if (session.id !== targetAccountId) {
		throw new Error("Account write denied: authenticated session does not match the target account.");
	}
}
