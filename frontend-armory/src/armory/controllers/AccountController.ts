import * as express from "express";
import { RowDataPacket, ResultSetHeader } from "mysql2/promise";

import { Armory } from "../Armory";
import { IRealmConfig } from "../Config";
import {
	AccountProfile,
	AccountProfileStore,
	DISPLAY_NAME_MAX_LENGTH,
	DISPLAY_NAME_MIN_LENGTH,
	isValidDisplayName,
	maskEmail,
	normalizeDisplayName,
} from "../auth/AccountProfileStore";
import { isValidEmail, normalizeEmail } from "../auth/EmailAddress";
import {
	MAX_RESENDS_PER_HOUR,
	PendingRegistrationStore,
	VERIFICATION_EXPIRY_HOURS,
} from "../auth/PendingRegistrationStore";
import { generateCredentials, verifyPassword } from "../auth/Srp6";
import {
	setSession,
	getSession,
	clearSession,
	isAccountSession,
	isPendingSession,
	IAccountSession,
	IPendingSession,
} from "../auth/Session";
import { createVerificationToken, hashVerificationToken } from "../auth/VerificationToken";
import { EmailService } from "../services/EmailService";
import { GuildBankService } from "../services/GuildBankService";
import { CharacterController } from "./CharacterController";
import { EFaction, IEmblemSource, Utils } from "../Utils";

const USERNAME_REGEX = /^[A-Za-z0-9_-]{3,16}$/;
const MYSQL_DUP_ENTRY = "ER_DUP_ENTRY";
const DUPLICATE_EMAIL_MESSAGE =
	"Unable to create an account with this email. Try signing in or use a different email.";

export class AccountController {
	private pendingStore: PendingRegistrationStore | null = null;
	private emailService: EmailService | null = null;
	private profileStore: AccountProfileStore | null = null;
	private itemLookup: CharacterController | null = null;
	private guildBankService: GuildBankService | null = null;

	public constructor(private readonly armory: Armory) {}

	public setItemLookup(controller: CharacterController): void {
		this.itemLookup = controller;
		this.guildBankService = new GuildBankService(this.armory, controller);
	}

	private get realm(): IRealmConfig {
		return this.armory.config.realms[0];
	}

	private get root(): string {
		return this.armory.config.websiteRoot ?? "";
	}

	private get accounts() {
		return this.armory.config.accounts;
	}

	private get emailMode(): boolean {
		return this.accounts.emailConfirmationEnabled;
	}

	private get emailReady(): boolean {
		return this.emailMode && this.accounts.emailConfigured;
	}

	private getPendingStore(): PendingRegistrationStore {
		if (!this.pendingStore) {
			this.pendingStore = new PendingRegistrationStore(
				this.armory.getAuthDb(this.realm.name),
				this.armory.config.dbQueryTimeout,
			);
		}
		return this.pendingStore;
	}

	private getEmailService(): EmailService {
		if (!this.emailService) {
			this.emailService = new EmailService(this.armory.config, this.armory.logger);
		}
		return this.emailService;
	}

	private getProfileStore(): AccountProfileStore {
		if (!this.profileStore) {
			this.profileStore = new AccountProfileStore(
				this.armory.getAuthDb(this.realm.name),
				this.armory.config.dbQueryTimeout,
			);
		}
		return this.profileStore;
	}

	public async initialize(): Promise<void> {
		if (!this.accounts.enabled) {
			return;
		}
		await this.getProfileStore().ensureTable();
		if (!this.emailMode) {
			return;
		}
		const store = this.getPendingStore();
		await store.ensureTable();
		await store.deleteExpired();
	}

	private safeReturnTo(value: unknown): string {
		if (typeof value === "string" && value.startsWith("/") && !value.startsWith("//")) {
			return value;
		}
		return `${this.root}/account`;
	}

	private redirectIncompleteSession(req: express.Request, res: express.Response): boolean {
		const session = getSession(req, this.accounts);
		if (!session || isAccountSession(session)) {
			return false;
		}
		if (session.state === "awaiting_username") {
			res.redirect(`${this.root}/choose-username`);
			return true;
		}
		res.redirect(`${this.root}/verify-email-pending`);
		return true;
	}

	private buildVerifyUrl(rawToken: string): string {
		const base = (this.armory.config.websiteUrl ?? "").replace(/\/+$/, "");
		return `${base}${this.root}/verify-email?token=${encodeURIComponent(rawToken)}`;
	}

	private verificationExpiryDate(): Date {
		return new Date(Date.now() + VERIFICATION_EXPIRY_HOURS * 60 * 60 * 1000);
	}

	public loginForm(req: express.Request, res: express.Response): void {
		if (this.redirectIncompleteSession(req, res)) {
			return;
		}
		const session = getSession(req, this.accounts);
		if (session && isAccountSession(session)) {
			res.redirect(`${this.root}/account`);
			return;
		}

		res.render("login.hbs", {
			title: "Sign in",
			allowRegistration: this.accounts.allowRegistration && (!this.emailMode || this.emailReady),
			returnTo: this.safeReturnTo(req.query.returnTo),
			emailConfirmationEnabled: this.emailMode,
			registrationDisabled: this.emailMode && !this.emailReady,
		});
	}

	public async login(req: express.Request, res: express.Response): Promise<void> {
		if (this.emailMode && !this.emailReady) {
			res.status(503).render("login.hbs", {
				title: "Sign in",
				authError: "Sign-in is disabled until email delivery is configured for this realm.",
				allowRegistration: false,
				returnTo: this.safeReturnTo(req.body?.returnTo),
				emailConfirmationEnabled: true,
				registrationDisabled: true,
			});
			return;
		}
		if (this.emailMode) {
			await this.loginWithEmail(req, res);
			return;
		}
		await this.loginWithUsername(req, res);
	}

	private async loginWithUsername(req: express.Request, res: express.Response): Promise<void> {
		const username = String(req.body?.username ?? "").trim();
		const password = String(req.body?.password ?? "");
		const returnTo = this.safeReturnTo(req.body?.returnTo);

		const renderError = (message: string): void => {
			res.status(401).render("login.hbs", {
				title: "Sign in",
				authError: message,
				username,
				allowRegistration: this.accounts.allowRegistration,
				returnTo,
				emailConfirmationEnabled: false,
			});
		};

		if (!USERNAME_REGEX.test(username) || password.length === 0) {
			renderError("Invalid username or password.");
			return;
		}

		const db = this.armory.getAuthDb(this.realm.name);
		let rows: RowDataPacket[];
		try {
			[rows] = await db.query<RowDataPacket[]>({
				sql: "SELECT `id`, `username`, `salt`, `verifier` FROM `account` WHERE `username` = ? LIMIT 1",
				values: [username.toUpperCase()],
				timeout: this.armory.config.dbQueryTimeout,
			});
		} catch (err) {
			this.armory.logger.error(`Login lookup failed for ${username}: ${err}`);
			renderError("Sign-in is temporarily unavailable. Please try again shortly.");
			return;
		}

		if (rows.length === 0 || !verifyPassword(username, password, rows[0].salt, rows[0].verifier)) {
			renderError("Invalid username or password.");
			return;
		}

		const session: IAccountSession = { kind: "account", id: rows[0].id, username: rows[0].username };
		setSession(req, res, session, this.accounts, this.armory.config.websiteUrl);
		res.redirect(returnTo);
	}

	private async loginWithEmail(req: express.Request, res: express.Response): Promise<void> {
		const email = normalizeEmail(String(req.body?.email ?? req.body?.username ?? ""));
		const password = String(req.body?.password ?? "");
		const returnTo = this.safeReturnTo(req.body?.returnTo);

		const renderError = (message: string): void => {
			res.status(401).render("login.hbs", {
				title: "Sign in",
				authError: message,
				email,
				allowRegistration: this.accounts.allowRegistration && this.emailReady,
				returnTo,
				emailConfirmationEnabled: true,
				registrationDisabled: !this.emailReady,
			});
		};

		if (!isValidEmail(email) || password.length === 0) {
			renderError("Invalid email or password.");
			return;
		}

		const db = this.armory.getAuthDb(this.realm.name);
		try {
			const [accountRows] = await db.query<RowDataPacket[]>({
				sql: `
					SELECT \`id\`, \`username\`, \`salt\`, \`verifier\`
					FROM \`account\`
					WHERE LOWER(\`email\`) = ? OR LOWER(\`reg_mail\`) = ?
					LIMIT 1
				`,
				values: [email, email],
				timeout: this.armory.config.dbQueryTimeout,
			});

			if (accountRows.length > 0) {
				const row = accountRows[0];
				if (verifyPassword(row.username, password, row.salt, row.verifier)) {
					const session: IAccountSession = { kind: "account", id: row.id, username: row.username };
					setSession(req, res, session, this.accounts, this.armory.config.websiteUrl);
					res.redirect(returnTo);
					return;
				}
				renderError("Invalid email or password.");
				return;
			}

			const pending = await this.getPendingStore().findActiveByEmail(email);
			if (!pending || pending.expiresAt.getTime() < Date.now()) {
				renderError("Invalid email or password.");
				return;
			}

			const srpIdentity = pending.email.toUpperCase();
			if (!verifyPassword(srpIdentity, password, pending.salt, pending.verifier)) {
				renderError("Invalid email or password.");
				return;
			}

			const pendingSession: IPendingSession = {
				kind: "pending",
				pendingId: pending.id,
				email: pending.email,
				state: pending.verifiedAt ? "awaiting_username" : "awaiting_verification",
			};
			setSession(req, res, pendingSession, this.accounts, this.armory.config.websiteUrl);
			res.redirect(pendingSession.state === "awaiting_username" ? `${this.root}/choose-username` : `${this.root}/verify-email-pending`);
		} catch (err) {
			this.armory.logger.error(`Email login failed for ${email}: ${err}`);
			renderError("Sign-in is temporarily unavailable. Please try again shortly.");
		}
	}

	public registerForm(req: express.Request, res: express.Response): void {
		if (!this.accounts.allowRegistration) {
			res.redirect(`${this.root}/login`);
			return;
		}
		if (this.redirectIncompleteSession(req, res)) {
			return;
		}
		const session = getSession(req, this.accounts);
		if (session && isAccountSession(session)) {
			res.redirect(`${this.root}/account`);
			return;
		}

		if (this.emailMode && !this.emailReady) {
			res.render("register.hbs", {
				title: "Create account",
				authError: "Registration is disabled until email delivery is configured for this realm.",
				emailConfirmationEnabled: true,
				registrationDisabled: true,
				...this.passwordHints(),
			});
			return;
		}

		res.render("register.hbs", {
			title: "Create account",
			emailConfirmationEnabled: this.emailMode,
			registrationDisabled: false,
			...this.passwordHints(),
		});
	}

	public async register(req: express.Request, res: express.Response): Promise<void> {
		if (!this.accounts.allowRegistration) {
			res.redirect(`${this.root}/login`);
			return;
		}
		if (this.emailMode) {
			await this.registerWithEmail(req, res);
			return;
		}
		await this.registerWithUsername(req, res);
	}

	private async registerWithUsername(req: express.Request, res: express.Response): Promise<void> {
		const username = String(req.body?.username ?? "").trim();
		const password = String(req.body?.password ?? "");
		const confirm = String(req.body?.confirmPassword ?? "");

		const renderError = (authError: string): void => {
			res.status(400).render("register.hbs", {
				title: "Create account",
				authError,
				username,
				emailConfirmationEnabled: false,
				registrationDisabled: false,
				...this.passwordHints(),
			});
		};

		if (!USERNAME_REGEX.test(username)) {
			renderError("Username must be 3-16 characters: letters, numbers, dashes or underscores.");
			return;
		}
		if (password.length < this.accounts.minPasswordLength || password.length > this.accounts.maxPasswordLength) {
			renderError(`Password must be between ${this.accounts.minPasswordLength} and ${this.accounts.maxPasswordLength} characters.`);
			return;
		}
		if (password !== confirm) {
			renderError("Passwords do not match.");
			return;
		}

		const normalized = username.toUpperCase();
		const db = this.armory.getAuthDb(this.realm.name);

		const [existing] = await db.query<RowDataPacket[]>({
			sql: "SELECT `id` FROM `account` WHERE `username` = ? LIMIT 1",
			values: [normalized],
			timeout: this.armory.config.dbQueryTimeout,
		});
		if (existing.length > 0) {
			renderError("That username is already taken.");
			return;
		}

		const { salt, verifier } = generateCredentials(normalized, password);
		try {
			const [result] = await db.query<ResultSetHeader>({
				sql: "INSERT INTO `account` (`username`, `salt`, `verifier`, `email`, `reg_mail`, `joindate`, `expansion`) VALUES (?, ?, ?, '', '', NOW(), 2)",
				values: [normalized, salt, verifier],
				timeout: this.armory.config.dbQueryTimeout,
			});

			const session: IAccountSession = { kind: "account", id: result.insertId, username: normalized };
			setSession(req, res, session, this.accounts, this.armory.config.websiteUrl);
			this.armory.logger.info(`New account registered via armory: ${normalized}`);
			res.redirect(`${this.root}/account`);
		} catch (err) {
			if ((err as { code?: string })?.code === MYSQL_DUP_ENTRY) {
				renderError("That username is already taken.");
				return;
			}
			throw err;
		}
	}

	private async registerWithEmail(req: express.Request, res: express.Response): Promise<void> {
		const email = normalizeEmail(String(req.body?.email ?? ""));
		const password = String(req.body?.password ?? "");
		const confirm = String(req.body?.confirmPassword ?? "");

		const renderError = (authError: string, keepEmail = email): void => {
			res.status(400).render("register.hbs", {
				title: "Create account",
				authError,
				email: keepEmail,
				emailConfirmationEnabled: true,
				registrationDisabled: !this.emailReady,
				...this.passwordHints(),
			});
		};

		if (!this.emailReady) {
			renderError("Registration is disabled until email delivery is configured for this realm.");
			return;
		}
		if (!isValidEmail(email)) {
			renderError("Enter a valid email address.");
			return;
		}
		if (password.length < this.accounts.minPasswordLength || password.length > this.accounts.maxPasswordLength) {
			renderError(`Password must be between ${this.accounts.minPasswordLength} and ${this.accounts.maxPasswordLength} characters.`);
			return;
		}
		if (password !== confirm) {
			renderError("Passwords do not match.");
			return;
		}

		const store = this.getPendingStore();
		if (await store.isEmailTaken(email)) {
			renderError(DUPLICATE_EMAIL_MESSAGE);
			return;
		}

		const srpIdentity = email.toUpperCase();
		const { salt, verifier } = generateCredentials(srpIdentity, password);
		const { raw, hash } = createVerificationToken();
		const expiresAt = this.verificationExpiryDate();

		let pendingId: number;
		try {
			pendingId = await store.createPending({
				email,
				salt,
				verifier,
				verificationTokenHash: hash,
				expiresAt,
			});
		} catch (err) {
			if ((err as { code?: string })?.code === MYSQL_DUP_ENTRY) {
				renderError(DUPLICATE_EMAIL_MESSAGE);
				return;
			}
			throw err;
		}

		try {
			await this.getEmailService().sendVerificationEmail(email, this.buildVerifyUrl(raw));
		} catch (err) {
			this.armory.logger.error(`Failed to send verification email to ${email}: ${err}`);
			await this.armory.getAuthDb(this.realm.name).query({
				sql: `DELETE FROM \`armory_pending_registration\` WHERE \`id\` = ?`,
				values: [pendingId],
				timeout: this.armory.config.dbQueryTimeout,
			});
			renderError("Could not send the verification email. Please try again shortly.");
			return;
		}

		this.armory.logger.info(`Pending registration created for ${email}`);
		res.redirect(`${this.root}/verify-email-pending?email=${encodeURIComponent(email)}`);
	}

	public verifyEmailPendingForm(req: express.Request, res: express.Response): void {
		const session = getSession(req, this.accounts);
		const emailFromQuery = typeof req.query.email === "string" ? normalizeEmail(req.query.email) : "";
		const email =
			(session && isPendingSession(session) ? session.email : "") ||
			emailFromQuery;

		res.render("verify-email-pending.hbs", {
			title: "Verify your email",
			email,
			authError: typeof req.query.error === "string" ? req.query.error : "",
			authMessage: typeof req.query.message === "string" ? req.query.message : "",
		});
	}

	public async resendVerification(req: express.Request, res: express.Response): Promise<void> {
		const session = getSession(req, this.accounts);
		const email = normalizeEmail(
			String(req.body?.email ?? (session && isPendingSession(session) ? session.email : "")),
		);

		const redirectWithError = (message: string): void => {
			res.redirect(`${this.root}/verify-email-pending?email=${encodeURIComponent(email)}&error=${encodeURIComponent(message)}`);
		};

		if (!this.emailReady || !isValidEmail(email)) {
			redirectWithError("Unable to resend the verification email right now.");
			return;
		}

		const store = this.getPendingStore();
		const pending = await store.findActiveByEmail(email);
		if (!pending || pending.verifiedAt) {
			redirectWithError("Unable to resend the verification email right now.");
			return;
		}

		const now = new Date();
		let resendCount = pending.resendCount;
		let windowStartedAt = pending.resendWindowStartedAt;
		if (!windowStartedAt || now.getTime() - windowStartedAt.getTime() > 60 * 60 * 1000) {
			resendCount = 0;
			windowStartedAt = now;
		}
		if (resendCount >= MAX_RESENDS_PER_HOUR) {
			redirectWithError("Too many resend attempts. Please wait an hour and try again.");
			return;
		}

		const { raw, hash } = createVerificationToken();
		const expiresAt = this.verificationExpiryDate();
		await store.rotateVerificationToken(pending.id, hash, expiresAt);
		await store.recordResend(pending.id, resendCount + 1, windowStartedAt);

		try {
			await this.getEmailService().sendVerificationEmail(email, this.buildVerifyUrl(raw));
		} catch (err) {
			this.armory.logger.error(`Resend verification failed for ${email}: ${err}`);
			redirectWithError("Could not send the verification email. Please try again shortly.");
			return;
		}

		res.redirect(
			`${this.root}/verify-email-pending?email=${encodeURIComponent(email)}&message=${encodeURIComponent("Verification email sent.")}`,
		);
	}

	public async verifyEmail(req: express.Request, res: express.Response): Promise<void> {
		const rawToken = String(req.query.token ?? "").trim();
		if (!rawToken) {
			res.render("verify-email.hbs", { title: "Email verification", status: "invalid" });
			return;
		}

		const store = this.getPendingStore();
		const pending = await store.findByTokenHash(hashVerificationToken(rawToken));
		if (!pending || pending.expiresAt.getTime() < Date.now()) {
			res.render("verify-email.hbs", { title: "Email verification", status: "expired" });
			return;
		}

		if (!pending.verifiedAt) {
			await store.markVerified(pending.id);
		}

		const pendingSession: IPendingSession = {
			kind: "pending",
			pendingId: pending.id,
			email: pending.email,
			state: "awaiting_username",
		};
		setSession(req, res, pendingSession, this.accounts, this.armory.config.websiteUrl);
		res.redirect(`${this.root}/choose-username`);
	}

	public chooseUsernameForm(req: express.Request, res: express.Response): void {
		const session = getSession(req, this.accounts);
		if (!session || !isPendingSession(session) || session.state !== "awaiting_username") {
			res.redirect(`${this.root}/login`);
			return;
		}

		res.render("choose-username.hbs", {
			title: "Choose your username",
			email: session.email,
			authError: "",
			displayName: "",
			username: "",
			...this.passwordHints(),
		});
	}

	public async chooseUsername(req: express.Request, res: express.Response): Promise<void> {
		const session = getSession(req, this.accounts);
		if (!session || !isPendingSession(session) || session.state !== "awaiting_username") {
			res.redirect(`${this.root}/login`);
			return;
		}

		const displayName = normalizeDisplayName(String(req.body?.displayName ?? ""));
		const username = String(req.body?.username ?? "").trim();
		const password = String(req.body?.password ?? "");

		const renderError = (authError: string): void => {
			res.status(400).render("choose-username.hbs", {
				title: "Choose your username",
				email: session.email,
				authError,
				displayName,
				username,
				...this.passwordHints(),
			});
		};

		if (!isValidDisplayName(displayName)) {
			renderError(
				`Display name must be ${DISPLAY_NAME_MIN_LENGTH}–${DISPLAY_NAME_MAX_LENGTH} characters and use only letters, numbers, spaces, and dashes.`,
			);
			return;
		}

		const profileStore = this.getProfileStore();
		if (await profileStore.isDisplayNameTaken(displayName)) {
			renderError("That display name is already taken. Try another.");
			return;
		}

		if (!USERNAME_REGEX.test(username)) {
			renderError("Username must be 3-16 characters: letters, numbers, dashes or underscores.");
			return;
		}
		if (password.length === 0) {
			renderError("Enter the password you used when registering.");
			return;
		}

		const store = this.getPendingStore();
		const pending = await store.findById(session.pendingId);
		if (!pending || !pending.verifiedAt || pending.accountId) {
			res.redirect(`${this.root}/login`);
			return;
		}

		const srpIdentity = pending.email.toUpperCase();
		if (!verifyPassword(srpIdentity, password, pending.salt, pending.verifier)) {
			renderError("That password does not match your registration.");
			return;
		}

		const normalized = username.toUpperCase();
		const db = this.armory.getAuthDb(this.realm.name);
		const [existing] = await db.query<RowDataPacket[]>({
			sql: "SELECT `id` FROM `account` WHERE `username` = ? LIMIT 1",
			values: [normalized],
			timeout: this.armory.config.dbQueryTimeout,
		});
		if (existing.length > 0) {
			renderError("That username is already taken.");
			return;
		}

		const { salt, verifier } = generateCredentials(normalized, password);
		try {
			const [result] = await db.query<ResultSetHeader>({
				sql: `
					INSERT INTO \`account\` (\`username\`, \`salt\`, \`verifier\`, \`email\`, \`reg_mail\`, \`joindate\`, \`expansion\`)
					VALUES (?, ?, ?, ?, ?, NOW(), 2)
				`,
				values: [normalized, salt, verifier, pending.email, pending.email],
				timeout: this.armory.config.dbQueryTimeout,
			});

			await store.linkAccount(pending.id, result.insertId);
			try {
				await profileStore.createProfile(result.insertId, displayName);
			} catch (err) {
				if ((err as { code?: string })?.code === MYSQL_DUP_ENTRY) {
					renderError("That display name is already taken. Try another.");
					return;
				}
				throw err;
			}
			const accountSession: IAccountSession = { kind: "account", id: result.insertId, username: normalized };
			setSession(req, res, accountSession, this.accounts, this.armory.config.websiteUrl);
			this.armory.logger.info(`Pending registration completed: ${pending.email} -> ${normalized}`);
			res.redirect(`${this.root}/account`);
		} catch (err) {
			if ((err as { code?: string })?.code === MYSQL_DUP_ENTRY) {
				renderError("That username is already taken.");
				return;
			}
			throw err;
		}
	}

	public logout(req: express.Request, res: express.Response): void {
		clearSession(res);
		res.redirect(`${this.root}/`);
	}

	private getGuildBankService(): GuildBankService {
		if (!this.guildBankService) {
			this.guildBankService = new GuildBankService(this.armory, this.itemLookup);
		}
		return this.guildBankService;
	}

	public async guildBank(req: express.Request, res: express.Response): Promise<void> {
		const session = getSession(req, this.accounts);
		if (!session || !isAccountSession(session)) {
			res.status(401).json({ error: "Sign in required." });
			return;
		}

		const realmName = String(req.query.realm ?? "");
		const guildId = parseInt(String(req.query.guildId ?? ""), 10);
		const tabId = parseInt(String(req.query.tabId ?? "0"), 10);

		if (!realmName || Number.isNaN(guildId)) {
			res.status(400).json({ error: "Invalid guild parameters." });
			return;
		}

		const view = await this.getGuildBankService().getView(
			realmName,
			guildId,
			session.id,
			Number.isNaN(tabId) ? 0 : tabId,
		);
		if (!view.enabled || !view.activeTab) {
			res.status(403).json({ error: view.disabledReason ?? "Guild bank is unavailable." });
			return;
		}

		res.json({
			...view.activeTab,
			money: view.money,
		});
	}

	public async ensureProfile(accountId: number): Promise<AccountProfile> {
		return this.armory.ensureAccountProfile(accountId);
	}

	public async updateProfile(req: express.Request, res: express.Response): Promise<void> {
		const session = getSession(req, this.accounts);
		if (!session || !isAccountSession(session)) {
			res.redirect(`${this.root}/login?returnTo=${encodeURIComponent(`${this.root}/account`)}`);
			return;
		}

		const displayName = normalizeDisplayName(String(req.body?.displayName ?? ""));
		const returnTab = typeof req.body?.tab === "string" ? req.body.tab : "details";

		const renderWithError = (message: string): void => {
			void this.renderAccountPage(req, res, session, {
				profileError: message,
				activeTab: returnTab === "characters" || returnTab === "guild" ? returnTab : "details",
			});
		};

		if (!isValidDisplayName(displayName)) {
			renderWithError(
				`Display name must be ${DISPLAY_NAME_MIN_LENGTH}–${DISPLAY_NAME_MAX_LENGTH} characters and use only letters, numbers, spaces, and dashes.`,
			);
			return;
		}

		const store = this.getProfileStore();
		await this.ensureProfile(session.id);

		if (await store.isDisplayNameTaken(displayName, session.id)) {
			renderWithError("That display name is already taken. Try another.");
			return;
		}

		await store.updateDisplayName(session.id, displayName);
		const path =
			returnTab === "characters"
				? "/account/characters"
				: returnTab === "guild"
					? "/account/guild"
					: "/account/details";
		res.redirect(`${this.root}${path}?saved=1`);
	}

	public async account(req: express.Request, res: express.Response): Promise<void> {
		const session = getSession(req, this.accounts);
		if (!session || !isAccountSession(session)) {
			res.redirect(`${this.root}/login?returnTo=${encodeURIComponent(`${this.root}/account`)}`);
			return;
		}

		const tabParam = typeof req.query.tab === "string" ? req.query.tab : "details";
		const profileSaved = req.query.saved === "1";
		await this.renderAccountPage(req, res, session, {
			activeTab: tabParam,
			profileSaved,
		});
	}

	public async accountDetails(req: express.Request, res: express.Response): Promise<void> {
		await this.accountWithTab(req, res, "details");
	}

	public async accountCharacters(req: express.Request, res: express.Response): Promise<void> {
		await this.accountWithTab(req, res, "characters");
	}

	public async accountGuild(req: express.Request, res: express.Response): Promise<void> {
		await this.accountWithTab(req, res, "guild");
	}

	private async accountWithTab(
		req: express.Request,
		res: express.Response,
		tab: "details" | "characters" | "guild",
	): Promise<void> {
		const session = getSession(req, this.accounts);
		if (!session || !isAccountSession(session)) {
			const returnTo = `${this.root}/account/${tab}`;
			res.redirect(`${this.root}/login?returnTo=${encodeURIComponent(returnTo)}`);
			return;
		}

		const profileSaved = req.query.saved === "1";
		await this.renderAccountPage(req, res, session, {
			activeTab: tab,
			profileSaved,
		});
	}

	private async renderAccountPage(
		req: express.Request,
		res: express.Response,
		session: IAccountSession,
		options: {
			activeTab?: string;
			profileError?: string;
			profileSaved?: boolean;
		} = {},
	): Promise<void> {
		const profile = await this.ensureProfile(session.id);
		const maskedEmail = this.emailMode ? await this.loadMaskedEmail(session.id) : null;

		const realmGroups = await Promise.all(
			this.armory.config.realms.map(async (realm) => {
				const charsDb = this.armory.getCharactersDb(realm.name);
				const [characters] = await charsDb.query<RowDataPacket[]>({
					sql: `
						SELECT c.name, c.race, c.class, c.level, c.gender, c.online,
						       g.name AS guildName, g.guildid AS guildId
						FROM characters c
						LEFT JOIN guild_member gm ON gm.guid = c.guid
						LEFT JOIN guild g ON g.guildid = gm.guildid
						WHERE c.account = ? AND c.deleteInfos_Account IS NULL
						ORDER BY c.level DESC, c.name ASC
					`,
					values: [session.id],
					timeout: this.armory.config.dbQueryTimeout,
				});

				return {
					realm: realm.name,
					characters: characters.map((c) => ({
						name: c.name,
						level: c.level,
						class: c.class,
						className: Utils.classNames[c.class] ?? "unknown",
						race: c.race,
						raceName: Utils.raceNames[c.race] ?? "unknown",
						online: c.online === 1,
						guildName: c.guildName ?? null,
						guildUrl:
							c.guildName && c.guildId
								? `${this.root}/guild/${encodeURIComponent(realm.name)}/${encodeURIComponent(c.guildName)}`
								: null,
						url: `${this.root}/character/${encodeURIComponent(realm.name)}/${encodeURIComponent(c.name)}`,
					})),
				};
			}),
		);

		const guilds = await this.loadAccountGuilds(session.id);
		const totalCharacters = realmGroups.reduce((sum, group) => sum + group.characters.length, 0);
		const hasGuilds = guilds.length > 0;

		const tabParam = options.activeTab ?? (typeof req.query.tab === "string" ? req.query.tab : "details");
		const activeTab =
			tabParam === "characters" || (tabParam === "guild" && hasGuilds) ? tabParam : "details";

		const guildParam = typeof req.query.guild === "string" ? req.query.guild : "";
		let selectedGuild = guilds.find((g) => g.key === guildParam) ?? null;
		if (!selectedGuild && guilds.length > 0) {
			selectedGuild = this.defaultGuildSelection(realmGroups, guilds);
		}

		const guildTabParam = typeof req.query.guildTab === "string" ? req.query.guildTab : "members";
		const guildSubTab =
			guildTabParam === "bank" ? "bank" : guildTabParam === "info" ? "info" : "members";

		let bankView = null;
		if (activeTab === "guild" && guildSubTab === "bank" && selectedGuild) {
			const bankTabId = parseInt(String(req.query.bankTab ?? "0"), 10);
			bankView = await this.getGuildBankService().getView(
				selectedGuild.realm,
				selectedGuild.id,
				session.id,
				Number.isNaN(bankTabId) ? 0 : bankTabId,
			);
		} else if (activeTab === "guild" && guildSubTab === "bank") {
			bankView = {
				enabled: false,
				disabledReason: "Select a guild to view the bank.",
				money: null,
				tabs: [],
				activeTabId: 0,
				activeTab: null,
			};
		}

		res.render("account.hbs", {
			title: "My account",
			displayName: profile.displayName,
			username: session.username,
			hideUsername: profile.hideUsername,
			maskedEmail,
			profileError: options.profileError ?? null,
			profileSaved: options.profileSaved ?? false,
			realms: realmGroups,
			hasCharacters: totalCharacters > 0,
			hasGuilds,
			guilds,
			selectedGuild,
			activeTab,
			isDetailsTab: activeTab === "details",
			isCharactersTab: activeTab === "characters",
			isGuildTab: activeTab === "guild",
			guildSubTab,
			isGuildMembersTab: guildSubTab === "members",
			isGuildBankTab: guildSubTab === "bank",
			isGuildInfoTab: guildSubTab === "info",
			bankView,
			emailConfirmationEnabled: this.emailMode,
		});
	}

	private async loadMaskedEmail(accountId: number): Promise<string | null> {
		const db = this.armory.getAuthDb(this.realm.name);
		const [rows] = await db.query<RowDataPacket[]>({
			sql: "SELECT `email`, `reg_mail` FROM `account` WHERE `id` = ? LIMIT 1",
			values: [accountId],
			timeout: this.armory.config.dbQueryTimeout,
		});
		if (rows.length === 0) {
			return null;
		}
		const raw = String(rows[0].email || rows[0].reg_mail || "").trim();
		return raw.length > 0 ? maskEmail(raw) : null;
	}

	private defaultGuildSelection(
		realmGroups: { realm: string; characters: { level: number; guildName: string | null }[] }[],
		guilds: Awaited<ReturnType<AccountController["loadAccountGuilds"]>>,
	): Awaited<ReturnType<AccountController["loadAccountGuilds"]>>[number] | null {
		let best: { level: number; guildName: string; realm: string } | null = null;
		for (const group of realmGroups) {
			for (const character of group.characters) {
				if (!character.guildName) {
					continue;
				}
				if (!best || character.level > best.level) {
					best = { level: character.level, guildName: character.guildName, realm: group.realm };
				}
			}
		}
		if (!best) {
			return guilds[0] ?? null;
		}
		return (
			guilds.find((g) => g.realm === best!.realm && g.name === best!.guildName) ?? guilds[0] ?? null
		);
	}

	private async loadAccountGuilds(accountId: number): Promise<
		{
			key: string;
			realm: string;
			id: number;
			name: string;
			leader: string;
			leaderUrl: string;
			faction: EFaction;
			membersCount: number;
			emblem: ReturnType<typeof Utils.makeEmblemObject>;
			guildPageUrl: string;
			motd: string;
			info: string;
		}[]
	> {
		const guilds: {
			key: string;
			realm: string;
			id: number;
			name: string;
			leader: string;
			leaderUrl: string;
			faction: EFaction;
			membersCount: number;
			emblem: ReturnType<typeof Utils.makeEmblemObject>;
			guildPageUrl: string;
			motd: string;
			info: string;
		}[] = [];

		for (const realm of this.armory.config.realms) {
			const db = this.armory.getCharactersDb(realm.name);
			const [rows] = await db.query<RowDataPacket[]>({
				sql: `
					SELECT DISTINCT g.guildid, g.name, g.leaderguid, g.info, g.motd,
					       g.EmblemStyle AS emblemStyle, g.EmblemColor AS emblemColor,
					       g.BorderStyle AS borderStyle, g.BorderColor AS borderColor,
					       g.BackgroundColor AS background
					FROM characters c
					INNER JOIN guild_member gm ON gm.guid = c.guid
					INNER JOIN guild g ON g.guildid = gm.guildid
					WHERE c.account = ? AND c.deleteInfos_Account IS NULL
				`,
				values: [accountId],
				timeout: this.armory.config.dbQueryTimeout,
			});

			for (const guild of rows) {
				const [leaderRows] = await db.query<RowDataPacket[]>({
					sql: "SELECT name, race FROM characters WHERE guid = ? LIMIT 1",
					values: [guild.leaderguid],
					timeout: this.armory.config.dbQueryTimeout,
				});
				const leader = leaderRows[0];

				const [countRows] = await db.query<RowDataPacket[]>({
					sql: "SELECT COUNT(guid) AS count FROM guild_member WHERE guildid = ?",
					values: [guild.guildid],
					timeout: this.armory.config.dbQueryTimeout,
				});

				const guildName = guild.name as string;
				guilds.push({
					key: `${realm.name}/${guild.guildid}`,
					realm: realm.name,
					id: guild.guildid,
					name: guildName,
					leader: leader?.name ?? "Unknown",
					leaderUrl: leader?.name
						? `${this.root}/character/${encodeURIComponent(realm.name)}/${encodeURIComponent(leader.name)}`
						: "#",
					faction: leader ? Utils.getFactionFromRaceId(leader.race) : EFaction.Horde,
					membersCount: countRows[0]?.count ?? 0,
					emblem: Utils.makeEmblemObject(guild as unknown as IEmblemSource),
					guildPageUrl: `${this.root}/guild/${encodeURIComponent(realm.name)}/${encodeURIComponent(guildName)}`,
					motd: String(guild.motd ?? "").trim(),
					info: String(guild.info ?? "").trim(),
				});
			}
		}

		return guilds.sort((a, b) => a.realm.localeCompare(b.realm) || a.name.localeCompare(b.name));
	}

	private passwordHints(): { allowRegistration: boolean; minPasswordLength: number; maxPasswordLength: number } {
		return {
			allowRegistration: this.accounts.allowRegistration,
			minPasswordLength: this.accounts.minPasswordLength,
			maxPasswordLength: this.accounts.maxPasswordLength,
		};
	}
}
