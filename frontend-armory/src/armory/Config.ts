import * as fs from "fs";
const fsp = fs.promises;

import * as winston from "winston";

export interface IDatabaseConfig {
	host: string;
	port: number;
	user: string;
	password: string;
	database: string;
}

export interface IRealmConfig {
	name: string;
	realmId: number;
	authDatabase: string;
	charactersDatabase: IDatabaseConfig;
}

export interface IIframeModeConfig {
	enabled: boolean;
	url: string;
}

export interface IAccountsConfig {
	/** Whether player login/registration is enabled on the armory. */
	enabled: boolean;
	/** Whether new-account self-registration is allowed (login can be enabled without registration). */
	allowRegistration: boolean;
	minPasswordLength: number;
	maxPasswordLength: number;
	/** Secret used to sign the session cookie. Auto-generated per stack by the platform. */
	sessionSecret: string;
	/** Session cookie lifetime in hours. */
	sessionHours: number;
	/** When true, registration/login use email and require verification before activation. */
	emailConfirmationEnabled: boolean;
	/** False until SMTP is configured on the platform. */
	emailConfigured: boolean;
}

export interface IEmailConfig {
	smtpHost: string;
	smtpPort: number;
	smtpSecurity: string;
	smtpUsername: string;
	smtpPassword: string;
	fromAddress: string;
	fromName: string;
	verificationSubject: string;
	verificationBodyHtml: string;
}

export class Config {
    public websiteUrl!: string;
	public websiteName!: string;
	public websiteRoot!: string;
	public iframeMode!: IIframeModeConfig;
	public loadDbcs!: boolean;
	public hideGameMasters!: boolean;
	public transmogModule!: boolean;
	public useZamCdn!: boolean;
	/** Whether the live Azeroth world map (online player positions) is available and linked in the nav. */
	public worldMapModule!: boolean;
	/**
	 * Base URL of the shared asset sidecar that serves the heavy 3D model-viewer data
	 * (meta/mo3/bone/textures). When set, the armory proxies its /data/* routes and reads
	 * customization metadata from here instead of the local (image-excluded) files, so the
	 * viewer works without baking multi-GB assets into every armory image. Blank = serve local.
	 */
	public assetProxyUrl!: string;
	public realms!: IRealmConfig[];
	public worldDatabase!: IDatabaseConfig;
	public dbQueryTimeout!: number;
	public accounts!: IAccountsConfig;
	public email!: IEmailConfig;

	private static envPrefix = "ACORE_ARMORY";
	private static checkedMissingField = false;

	private static isTruthyEnv(value: string | undefined): boolean {
		return value === "1" || value?.toLowerCase() === "true";
	}

	/** SMTP env vars are only injected when email confirmation is enabled and SMTP is configured. */
	private static isEmailEnvRequired(): boolean {
		return (
			Config.isTruthyEnv(process.env["ACORE_ARMORY_ACCOUNTS__EMAIL_CONFIRMATION_ENABLED"]) &&
			Config.isTruthyEnv(process.env["ACORE_ARMORY_ACCOUNTS__EMAIL_CONFIGURED"])
		);
	}

	private static shouldWarnMissingField(parentName: string, field: string): boolean {
		const isEmailField =
			parentName === "email." ||
			parentName.startsWith("email.") ||
			(parentName === "" && field === "email");
		if (isEmailField) {
			return Config.isEmailEnvRequired();
		}
		return true;
	}

	public static async load(logger: winston.Logger): Promise<Config> {
		let config: Config;
		try {
			await fsp.access("config.json");
			config = await Config.loadFromFile(logger);
			// Platform injects stack settings via env (compose). Those must win over a baked config.json
			// so account login, session secrets and SMTP settings actually take effect in containers.
			const defaultConfigJson = await fsp.readFile("config.default.json");
			const defaultConfig = JSON.parse(defaultConfigJson.toString());
			Config.applyEnvOverrides(logger, config as unknown as Record<string, unknown>, defaultConfig);
		} catch (err) {
			config = await Config.loadFromEnv(logger);
		}
		Config.applyAccountDefaults(config);
		return config;
	}

	/** Ensures the optional `accounts` section exists so older configs keep working (feature off). */
	private static applyAccountDefaults(config: Config): void {
		const defaults: IAccountsConfig = {
			enabled: false,
			allowRegistration: true,
			minPasswordLength: 8,
			maxPasswordLength: 16,
			sessionSecret: "",
			sessionHours: 24,
			emailConfirmationEnabled: false,
			emailConfigured: false,
		};
		config.accounts = { ...defaults, ...(config.accounts ?? {}) };
		Config.applyEmailDefaults(config);
	}

	private static applyEmailDefaults(config: Config): void {
		const defaults: IEmailConfig = {
			smtpHost: "",
			smtpPort: 587,
			smtpSecurity: "starttls",
			smtpUsername: "",
			smtpPassword: "",
			fromAddress: "",
			fromName: "",
			verificationSubject: "",
			verificationBodyHtml: "",
		};
		config.email = { ...defaults, ...(config.email ?? {}) };
	}

	private static async loadFromFile(logger: winston.Logger): Promise<Config> {
		const json: Buffer = await fsp.readFile("config.json");
		const config = JSON.parse(json.toString()) as Config;

		if (!Config.checkedMissingField) {
			const defaultConfigJson = await fsp.readFile("config.default.json");
			const defaultConfig = JSON.parse(defaultConfigJson.toString());
			Config.checkAllMissingFields(logger, config, defaultConfig);
			Config.checkedMissingField = true;
		}

		return config;
	}

	private static async loadFromEnv(logger: winston.Logger): Promise<Config> {
		const config = {};
		const json = await fsp.readFile("config.default.json");
		const defaultConfig = JSON.parse(json.toString());
		Config.loadObjFromEnv(logger, config, defaultConfig);
		Config.checkedMissingField = true;
		return config as Config;
	}

	/** Applies environment variables on top of an existing config object (container env wins). */
	private static applyEnvOverrides(logger: winston.Logger, obj: Record<string, unknown>, model: Record<string, unknown>, parentName = ""): void {
		if (parentName !== "") {
			parentName += ".";
		}

		for (const field in model) {
			if (!Object.hasOwnProperty.call(model, field)) {
				continue;
			}

			const modelValue = model[field];
			if (Array.isArray(modelValue)) {
				const arr = Config.loadArrayFromEnv(logger, modelValue[0], parentName + field);
				if (arr.length > 0) {
					obj[field] = arr;
				}
				continue;
			}

			if (typeof modelValue === "object" && modelValue !== null) {
				if (!obj[field] || typeof obj[field] !== "object" || Array.isArray(obj[field])) {
					obj[field] = {};
				}
				Config.applyEnvOverrides(logger, obj[field] as Record<string, unknown>, modelValue as Record<string, unknown>, parentName + field);
				continue;
			}

			const key = Config.getEnvKey(parentName + field);
			if (Object.hasOwnProperty.call(process.env, key)) {
				obj[field] = Config.parseEnvValue(process.env[key] as string, modelValue as boolean | number | string);
			}
		}
	}

	private static loadObjFromEnv(logger: winston.Logger, obj: Record<string, any>, model: Record<string, any>, parentName = "") {
		if (parentName !== "") {
			parentName += ".";
		}

		for (const field in model) {
			if (!Object.hasOwnProperty.call(model, field)) {
				continue;
			}

			if (Array.isArray(model[field])) {
				obj[field] = Config.loadArrayFromEnv(logger, model[field][0], parentName + field);
			} else if (typeof model[field] === "object") {
				obj[field] = {};
				Config.loadObjFromEnv(logger, obj[field], model[field], parentName + field);
			} else if (!Object.hasOwnProperty.call(obj, field)) {
				const key = Config.getEnvKey(parentName + field);
				if (Object.hasOwnProperty.call(process.env, key)) {
					obj[field] = Config.parseEnvValue(process.env[key] as string, model[field]);
				} else if (!Config.checkedMissingField && Config.shouldWarnMissingField(parentName, field)) {
					logger.warn(`Config field ${key} is missing from .env!`);
				}
			}
		}
	}

	private static loadArrayFromEnv(logger: winston.Logger, model: any, parentName = ""): unknown[] {
		if (parentName !== "") {
			parentName += ".";
		}

		const arr: unknown[] = [];
		let i = 0;
		for (;;) {
			const key = Config.getEnvKey(parentName + i);
			const found = Object.keys(process.env).some((k) => k.startsWith(key));
			if (!found) {
				break;
			}

			if (Array.isArray(model)) {
				arr.push(Config.loadArrayFromEnv(logger, model[0], parentName + i));
			} else if (typeof model === "object") {
				const obj: Record<string, unknown> = {};
				Config.loadObjFromEnv(logger, obj, model, parentName + i);
				if (Object.keys(obj).length) {
					arr.push(obj);
				}
			} else if (Object.hasOwnProperty.call(process.env, key)) {
				arr.push(Config.parseEnvValue(process.env[key] as string, model));
			} else {
				break;
			}

			++i;
		}

		return arr;
	}

	private static getEnvKey(key: string): string {
		return (
			Config.envPrefix +
			"_" +
			key
				.replace(/\./g, "__")
				.replace(/[A-Z]/g, (letter) => `_${letter.toLowerCase()}`)
				.toUpperCase()
		);
	}

	private static parseEnvValue(value: string, model: boolean | number | string): boolean | number | string {
		const type = typeof model;
		const lower = value.toLowerCase();
		if (type === "boolean") {
			return lower === "true" || value === "1";
		}
		if (type === "number") {
			return parseFloat(value);
		}
		return value;
	}

	private static checkAllMissingFields(logger: winston.Logger, obj: Record<string, any>, model: Record<string, any>, parentName = "") {
		const missing = Config.hasMissingFields(obj, model);
		if (parentName !== "") {
			parentName += ".";
		}
		for (const field of missing) {
			if (!Config.shouldWarnMissingField(parentName, field)) {
				continue;
			}
			logger.warn(`Field ${parentName}${field} is missing from config.json!`);
		}
		for (const key of Object.keys(model)) {
			if (typeof model[key] === "object" && Object.hasOwnProperty.call(obj, key)) {
				Config.checkAllMissingFields(logger, obj[key], model[key], parentName + key);
			}
		}
	}

	private static hasMissingFields(obj: object, model: object): string[] {
		const objProp = Object.keys(obj);
		const missingProps = Object.keys(model).filter((key) => !objProp.includes(key));
		return missingProps;
	}
}
