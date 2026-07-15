const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export function normalizeEmail(value: string): string {
	return value.trim().toLowerCase();
}

export function isValidEmail(value: string): boolean {
	const normalized = normalizeEmail(value);
	return normalized.length > 0 && normalized.length <= 255 && EMAIL_REGEX.test(normalized);
}
