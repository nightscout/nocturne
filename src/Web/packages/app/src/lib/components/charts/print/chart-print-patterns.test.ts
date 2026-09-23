import { describe, expect, it } from "vitest";
import {
	CHART_TEXTURES,
	bgPatternClass,
	categoryKey,
	dashClass,
	patternClass,
	patternId,
	patternTiles,
	textureStylesheet,
	type TextureKey,
} from "./chart-print-patterns";

const keys = Object.keys(CHART_TEXTURES) as TextureKey[];

describe("textureStylesheet", () => {
	const css = textureStylesheet();

	it("gives every key a fill rule pointing at its own pattern", () => {
		for (const key of keys) {
			expect(css).toContain(`.chart-patterns-on .${patternClass(key)} { fill: url(#${patternId(key)}) !important; }`);
		}
	});

	it("emits a dash rule exactly for keys that declare one", () => {
		for (const key of keys) {
			expect(css.includes(`.${dashClass(key)} `)).toBe("dash" in CHART_TEXTURES[key]);
		}
	});

	it("keeps the element's own background colour under an HTML texture", () => {
		const rule = css.split("\n").find((r) => r.includes(`.${bgPatternClass("low")} `));
		expect(rule).toContain("background-image:");
		expect(rule).not.toMatch(/background:/);
	});

	it("emits no HTML texture for solid keys", () => {
		expect(css).not.toContain(`.${bgPatternClass("in-range")} `);
	});
});

describe("patternTiles", () => {
	it("renders one uniquely identified tile per key", () => {
		const ids = patternTiles().map((t) => t.id);
		expect(ids).toHaveLength(keys.length);
		expect(new Set(ids).size).toBe(keys.length);
	});
});

describe("categoryKey", () => {
	it("wraps any index onto cat-1..cat-6", () => {
		expect(categoryKey(1)).toBe("cat-1");
		expect(categoryKey(6)).toBe("cat-6");
		expect(categoryKey(7)).toBe("cat-1");
		expect(categoryKey(0)).toBe("cat-6");
		expect(categoryKey(-1)).toBe("cat-5");
	});
});
