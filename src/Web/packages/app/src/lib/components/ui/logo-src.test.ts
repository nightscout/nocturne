import { describe, it, expect } from "vitest";
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import {
	FALLBACK_LOGO,
	logoAliases,
	logoExtensions,
	monochromeLogos,
	resolveLogoName,
	resolveLogoSrc,
} from "./logo-src";

/** Resolve a `/logos/...` src to its path under `static/`. */
function staticPath(src: string): string {
	return fileURLToPath(new URL(`../../../../static${src}`, import.meta.url));
}

describe("resolveLogoSrc", () => {
	it("uses the mapped extension", () => {
		expect(resolveLogoSrc("dexcom")).toBe("/logos/dexcom.png");
		expect(resolveLogoSrc("medtronic")).toBe("/logos/medtronic.jpg");
	});

	it("assumes svg for an unmapped id", () => {
		expect(resolveLogoSrc("prelude")).toBe("/logos/prelude.svg");
	});

	it("passes a bare filename through", () => {
		expect(resolveLogoSrc("mylogo.png")).toBe("/logos/mylogo.png");
	});

	it("falls back to the device mark when no icon is given", () => {
		expect(resolveLogoSrc(undefined)).toBe("/logos/device.svg");
	});

	it("resolves CareLink to the Medtronic mark it shares", () => {
		expect(resolveLogoName("carelink")).toBe("medtronic");
		expect(resolveLogoSrc("carelink")).toBe("/logos/medtronic.jpg");
	});
});

describe("logo assets", () => {
	// Regression: carelink, iaps and gluroo had no entry and no file, so each
	// rendered a 404 broken image in the connectors UI.
	it.each(["carelink", "iaps", "gluroo"])("ships an asset for %s", (icon) => {
		expect(existsSync(staticPath(resolveLogoSrc(icon)))).toBe(true);
	});

	it("ships a file for every mapped id", () => {
		const missing = Object.keys(logoExtensions)
			.map((icon) => resolveLogoSrc(icon))
			.filter((src) => !existsSync(staticPath(src)));

		expect(missing).toEqual([]);
	});

	it("ships a file for every alias target", () => {
		const missing = Object.keys(logoAliases)
			.map((icon) => resolveLogoSrc(icon))
			.filter((src) => !existsSync(staticPath(src)));

		expect(missing).toEqual([]);
	});

	it("ships the fallback mark", () => {
		expect(existsSync(staticPath(FALLBACK_LOGO))).toBe(true);
	});

	it("only marks ids that resolve to an asset as monochrome", () => {
		for (const icon of monochromeLogos) {
			expect(existsSync(staticPath(resolveLogoSrc(icon)))).toBe(true);
		}
	});
});
