import nodemailer from "nodemailer";
import type { Transporter } from "nodemailer";
import * as winston from "winston";

import { Config } from "../Config";
import { VERIFICATION_EXPIRY_HOURS } from "../auth/PendingRegistrationStore";

export class EmailService {
	private transporter: Transporter | null = null;

	public constructor(
		private readonly config: Config,
		private readonly logger: winston.Logger,
	) {}

	public isReady(): boolean {
		const accounts = this.config.accounts;
		if (!accounts.emailConfirmationEnabled || !accounts.emailConfigured) {
			return false;
		}
		const email = this.config.email;
		return Boolean(email.smtpHost?.trim() && email.fromAddress?.trim());
	}

	private getTransporter(): Transporter {
		if (this.transporter) {
			return this.transporter;
		}

		const email = this.config.email;
		const secure = email.smtpSecurity === "tls";
		this.transporter = nodemailer.createTransport({
			host: email.smtpHost,
			port: email.smtpPort || 587,
			secure,
			requireTLS: email.smtpSecurity === "starttls",
			auth: email.smtpUsername
				? {
						user: email.smtpUsername,
						pass: email.smtpPassword,
					}
				: undefined,
		});
		return this.transporter;
	}

	public async sendVerificationEmail(toAddress: string, verifyUrl: string): Promise<void> {
		if (!this.isReady()) {
			throw new Error("Email delivery is not configured.");
		}

		const email = this.config.email;
		const subject = this.renderTemplate(
			email.verificationSubject || "Verify your account",
			verifyUrl,
		);
		const html = this.renderTemplate(email.verificationBodyHtml, verifyUrl);

		await this.getTransporter().sendMail({
			from: email.fromName
				? `"${email.fromName}" <${email.fromAddress}>`
				: email.fromAddress,
			to: toAddress,
			subject,
			html,
		});
		this.logger.info(`Verification email sent to ${toAddress}`);
	}

	private renderTemplate(template: string, verifyUrl: string): string {
		const siteName = this.config.websiteName || "Armory";
		return template
			.replaceAll("{{verifyUrl}}", verifyUrl)
			.replaceAll("{{siteName}}", siteName)
			.replaceAll("{{expiryHours}}", String(VERIFICATION_EXPIRY_HOURS));
	}
}
