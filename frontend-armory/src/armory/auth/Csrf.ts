import { randomBytes, timingSafeEqual } from "crypto";
import express from "express";

const COOKIE_NAME = "armory_csrf";
const FIELD_NAME = "_csrf";

function requestIsSecure(req: express.Request): boolean {
	if (req.secure) {
		return true;
	}
	const forwardedProto = req.headers["x-forwarded-proto"];
	const proto = Array.isArray(forwardedProto) ? forwardedProto[0] : forwardedProto;
	return typeof proto === "string" && proto.split(",")[0].trim().toLowerCase() === "https";
}

/**
 * Issues a per-browser CSRF token (double-submit cookie pattern) and exposes it to views as
 * <c>res.locals.csrfToken</c> so forms can embed it in a hidden <c>_csrf</c> field. The token lives
 * in an HttpOnly cookie a cross-site attacker can neither read nor predict, so it cannot forge a
 * matching form field. Apply globally so the navbar logout form always has a token available.
 */
export function issueCsrfToken(req: express.Request, res: express.Response, next: express.NextFunction): void {
	const cookies = (req as express.Request & { cookies?: Record<string, string> }).cookies;
	let token = cookies?.[COOKIE_NAME];
	if (!token || !/^[a-f0-9]{64}$/.test(token)) {
		token = randomBytes(32).toString("hex");
		res.cookie(COOKIE_NAME, token, {
			httpOnly: true,
			sameSite: "lax",
			secure: requestIsSecure(req),
			path: "/",
		});
	}
	res.locals.csrfToken = token;
	next();
}

function tokensMatch(a: string, b: string): boolean {
	if (a.length !== b.length) {
		return false;
	}
	return timingSafeEqual(Buffer.from(a), Buffer.from(b));
}

/**
 * Rejects state-changing requests whose submitted <c>_csrf</c> field does not match the CSRF cookie.
 * Use on POST routes (login/register/logout) after body parsing.
 */
export function verifyCsrf(req: express.Request, res: express.Response, next: express.NextFunction): void {
	const cookies = (req as express.Request & { cookies?: Record<string, string> }).cookies;
	const cookieToken = cookies?.[COOKIE_NAME];
	const submitted = String((req.body as Record<string, unknown> | undefined)?.[FIELD_NAME] ?? "");
	if (!cookieToken || !submitted || !tokensMatch(cookieToken, submitted)) {
		res.status(403).render("error.hbs", {
			status: 403,
			name: "Forbidden",
			description: "Your session could not be verified. Please reload the page and try again.",
			reqId: req.id,
		});
		return;
	}
	next();
}
