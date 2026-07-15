import express from "express";

import { Config } from "../Config";
import { getSession, isAccountSession, isPendingSession } from "../auth/Session";

const ALLOWED_PENDING_PREFIXES = [
	"/verify-email",
	"/verify-email-pending",
	"/choose-username",
	"/logout",
	"/login",
	"/register",
	"/health",
	"/js/",
	"/css/",
	"/img/",
	"/static/",
];

function isAllowedWhilePending(pathname: string): boolean {
	return ALLOWED_PENDING_PREFIXES.some((prefix) => pathname === prefix || pathname.startsWith(prefix));
}

/**
 * Blocks incomplete pending registrations from browsing the armory until email is verified and a
 * username is chosen. Active account sessions pass through unchanged.
 */
export function createEmailConfirmationGate(config: Config) {
	return (req: express.Request, res: express.Response, next: express.NextFunction): void => {
		if (!config.accounts.enabled || !config.accounts.emailConfirmationEnabled) {
			next();
			return;
		}

		const session = getSession(req, config.accounts);
		if (!session || isAccountSession(session)) {
			next();
			return;
		}

		if (!isPendingSession(session)) {
			next();
			return;
		}

		const root = config.websiteRoot ?? "";
		const pathname = req.path.startsWith(root) ? req.path.slice(root.length) || "/" : req.path;
		if (isAllowedWhilePending(pathname)) {
			next();
			return;
		}

		if (session.state === "awaiting_username") {
			res.redirect(`${root}/choose-username`);
			return;
		}

		res.redirect(`${root}/verify-email-pending`);
	};
}
