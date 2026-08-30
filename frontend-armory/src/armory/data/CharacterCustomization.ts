import * as fs from "fs";
const fsp = fs.promises;
import * as path from "path";

export class CharacterCustomization {
	private data!: { [key: number]: { [key: number]: unknown } };

	/** True once at least one customization file was found; false when the model-viewer data is absent. */
	public available = false;

	/**
	 * Loads the per-race/gender customization metadata. When {@link assetBase} is set the data is
	 * fetched from the shared asset sidecar (the same source the model viewer reads from); otherwise
	 * it is read from the local static/data/meta directory that may be baked into / mounted onto the image.
	 */
	public async loadData(assetBase = ""): Promise<void> {
		this.data = {};
		const races = [1, 2, 3, 4, 5, 6, 7, 8, 10, 11];
		const genders = [0, 1];
		let missing = 0;

		const base = assetBase.replace(/\/+$/, "");

		for (const race of races) {
			this.data[race] = {};

			for (const gender of genders) {
				try {
					const relPath = `meta/charactercustomization2/${race}_${gender}.json`;
					let json: string;
					if (base) {
						const res = await fetch(`${base}/${relPath}`, { signal: AbortSignal.timeout(15000) });
						if (!res.ok) {
							throw new Error(`asset sidecar returned ${res.status} for ${relPath}`);
						}
						json = await res.text();
					} else {
						const buffer = await fsp.readFile(path.join(process.cwd(), "static", "data", relPath));
						json = buffer.toString();
					}
					this.data[race][gender] = JSON.parse(json);
					this.available = true;
				} catch (err) {
					// The 3D model-viewer data (static/data/meta) is optional and intentionally excluded from
					// the platform's armory image to keep it small. Missing files simply disable the
					// character customization/model viewer for that race/gender instead of crashing.
					this.data[race][gender] = null;
					missing++;
				}
			}
		}

		if (missing > 0 && !this.available) {
			throw new CharacterCustomizationDataUnavailableError();
		}
	}

	public getCharacterCustomizationData(race: number, gender: number): unknown {
		return this.data?.[race]?.[gender] ?? null;
	}
}

/**
 * Signals that no character-customization/model-viewer data was found on disk. Callers can catch
 * this to keep the armory running with the 3D model viewer disabled.
 */
export class CharacterCustomizationDataUnavailableError extends Error {
	public constructor() {
		super("Character customization data (static/data/meta) not found; the 3D model viewer will be disabled.");
		this.name = "CharacterCustomizationDataUnavailableError";
		Object.setPrototypeOf(this, CharacterCustomizationDataUnavailableError.prototype);
	}
}
