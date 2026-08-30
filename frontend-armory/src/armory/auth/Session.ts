import express from "express";
import jwt from "jsonwebtoken";

import { IAccountsConfig } from "../Config";

export interface IAccountSession {
	kind: "account";
	/** acore_auth.account.id */
	id: number;
	/** Normalized (uppercase) account username. */
	username: string;
}

export interface IPendingSession {
	kind: "pending";
	pendingId: number;
	email: string;
	state: "awaiting_verification" | "awaiting_username";
}

export type ISession = IAccountSession | IPendingSession;

const COOKIE_NAME = "armory_session";

function isSecure(req: express.Request, websiteUrl: string | undefined): boolean {
	if (req.secure) {
		return true;
	}
	const forwardedProto = req.headers["x-forwarded-proto"];
	const proto = Array.isArray(forwardedProto) ? forwardedProto[0] : forwardedProto;
	if (typeof proto === "string" && proto.split(",")[0].trim().toLowerCase() === "https") {
		return true;
	}
	return (websiteUrl ?? "").toLowerCase().startsWith("https://");
}

export function isAccountSession(session: ISession): session is IAccountSession {
	return session.kind === "account";
}

export function isPendingSession(session: ISession): session is IPendingSession {
	return session.kind === "pending";
}

export function setSession(
	req: express.Request,
	res: express.Response,
	session: ISession,
	accounts: IAccountsConfig,
	websiteUrl?: string,
): void {
	const token = jwt.sign(session, accounts.sessionSecret, {
		expiresIn: `${accounts.sessionHours}h`,
	});
	res.cookie(COOKIE_NAME, token, {
		httpOnly: true,
		sameSite: "lax",
		secure: isSecure(req, websiteUrl),
		maxAge: accounts.sessionHours * 60 * 60 * 1000,
		path: "/",
	});
}

export function getSession(req: express.Request, accounts: IAccountsConfig): ISession | null {
	const token = (req as express.Request & { cookies?: Record<string, string> }).cookies?.[COOKIE_NAME];
	if (!token || !accounts.sessionSecret) {
		return null;
	}
	try {
		const payload = jwt.verify(token, accounts.sessionSecret) as jwt.JwtPayload & Record<string, unknown>;
		if (payload.kind === "pending") {
			if (
				typeof payload.pendingId !== "number" ||
				typeof payload.email !== "string" ||
				(payload.state !== "awaiting_verification" && payload.state !== "awaiting_username")
			) {
				return null;
			}
			return {
				kind: "pending",
				pendingId: payload.pendingId,
				email: payload.email,
				state: payload.state,
			};
		}

		if (typeof payload.id === "number" && typeof payload.username === "string") {
			return { kind: "account", id: payload.id, username: payload.username };
		}
		return null;
	} catch {
		return null;
	}
}

export function clearSession(res: express.Response): void {
	res.clearCookie(COOKIE_NAME, { path: "/" });
}
