/**
 * Monochrome chart textures: the colour-blind and black-and-white counterpart
 * of the `--glucose-*` / `--insulin-*` colour tokens. Each concept is keyed
 * once, so a bolus or a low wears the same texture in every report.
 *
 * {@link CHART_TEXTURES} is the single source. `ChartPrintPatterns.svelte`
 * renders one SVG `<pattern>` per key and emits {@link textureStylesheet}.
 * Every rule is gated on `.chart-patterns-on`, which is set while printing and
 * by the always-show-patterns appearance setting; without it marks keep colour.
 */

export type Texture =
	| { kind: "solid" }
	/** Parallel strokes. `angle` in degrees: 0 = "|", 90 = "—", 45 = "/", -45 = "\". */
	| { kind: "lines"; angle: number; gap: number }
	| { kind: "grid"; gap: number }
	| { kind: "dots"; gap: number };

export interface TextureSpec {
	/**
	 * Colour painted under the texture. Omitted for categorical keys, whose
	 * colour differs per chart; those print texture-only over the paper.
	 */
	color?: string;
	texture: Texture;
	/** `stroke-dasharray` for when the key is drawn as a line rather than a fill. */
	dash?: string;
}

const lines = (angle: number, gap: number): Texture => ({ kind: "lines", angle, gap });
const solid: Texture = { kind: "solid" };

/**
 * Lightest to heaviest, for scales whose steps are ordered by severity (GRI
 * zones, eHbA1c zones, detection confidence): heavier ink means worse.
 */
const ORDINAL: readonly Texture[] = [
	solid,
	{ kind: "dots", gap: 6 },
	lines(45, 8),
	lines(45, 5),
	lines(45, 3),
];

/**
 * Glucose bands read as a density scale out from in-range: "\" for lows, "/"
 * for highs, denser the further out. Keep that pairing when adding keys so a
 * reader learns it once.
 */
const VERY_LOW = lines(-45, 4);
const LOW = lines(-45, 8);
const HIGH = lines(45, 8);
const VERY_HIGH = lines(45, 4);

export const CHART_TEXTURES = {
	"very-low": { color: "var(--glucose-very-low)", texture: VERY_LOW, dash: "2 2" },
	low: { color: "var(--glucose-low)", texture: LOW, dash: "5 3" },
	"in-range": { color: "var(--glucose-in-range)", texture: solid },
	"tight-range": { color: "var(--glucose-tight-range)", texture: { kind: "dots", gap: 6 } },
	/** The target range shaded behind a glucose trace, light enough to read the trace through. */
	"target-band": { color: "oklch(from var(--glucose-in-range) l c h / 0.14)", texture: solid },
	high: { color: "var(--glucose-high)", texture: HIGH, dash: "5 3" },
	"very-high": { color: "var(--glucose-very-high)", texture: VERY_HIGH, dash: "2 2" },
	/**
	 * The glucose bands as texture alone, laid over a mark whose own fill carries
	 * a continuous glucose colour (the year heatmap's daily-average ramp).
	 */
	"very-low-hatch": { texture: VERY_LOW },
	"low-hatch": { texture: LOW },
	"high-hatch": { texture: HIGH },
	"very-high-hatch": { texture: VERY_HIGH },
	/** A target-range boundary drawn as a reference line over a glucose chart. */
	"target-range-limit": { color: "var(--glucose-in-range)", texture: solid, dash: "4 4" },
	/** The patient's personal target, drawn finer than the clinical limits it sits inside. */
	"glucose-target": { color: "var(--glucose-in-range)", texture: solid, dash: "2 4" },
	/** Basal the pump never reported, hatched rather than drawn as a rate. */
	"basal-unreported": { texture: lines(45, 4) },

	/** A glucose trace drawn as a plain line under another series (the actograms). */
	"glucose-trace": { color: "var(--muted-foreground)", texture: solid },
	steps: { color: "var(--primary)", texture: solid },
	"heart-rate": { color: "var(--chart-1)", texture: solid },

	"insulin-bolus": { color: "var(--insulin-bolus)", texture: solid },
	"insulin-scheduled-basal": {
		color: "var(--insulin-scheduled-basal)",
		texture: lines(90, 5),
		dash: "6 3",
	},
	"insulin-temp-basal": {
		color: "var(--insulin-additional-basal)",
		texture: lines(0, 5),
		dash: "2 3",
	},
	carbs: { color: "var(--carbs)", texture: { kind: "grid", gap: 6 }, dash: "8 3 2 3" },

	"percentile-outer": { color: "var(--percentile-outer)", texture: lines(45, 6), dash: "2 3" },
	"percentile-inner": { color: "var(--percentile-inner)", texture: solid, dash: "6 3" },
	"percentile-median": { color: "var(--percentile-median)", texture: solid },

	"sleep-awake": { color: "var(--chart-5)", texture: { kind: "dots", gap: 5 } },
	"sleep-light": { color: "var(--chart-1)", texture: lines(0, 5) },
	"sleep-rem": { color: "var(--chart-2)", texture: lines(45, 5) },
	"sleep-deep": { color: "var(--chart-3)", texture: solid },
	/** Asleep without a staged breakdown (asleep, in bed, unknown). */
	"sleep-unspecified": { color: "var(--muted-foreground)", texture: { kind: "grid", gap: 6 } },

	/**
	 * Seven dash families that stay apart in black and white at a 2px stroke:
	 * solid, long, dot, dash-dot, short, long-short, dash-dot-dot.
	 */
	"weekday-sun": { color: "var(--weekday-sun)", texture: solid },
	"weekday-mon": { color: "var(--weekday-mon)", texture: solid, dash: "12 4" },
	"weekday-tue": { color: "var(--weekday-tue)", texture: solid, dash: "2 3" },
	"weekday-wed": { color: "var(--weekday-wed)", texture: solid, dash: "8 3 2 3" },
	"weekday-thu": { color: "var(--weekday-thu)", texture: solid, dash: "5 4" },
	"weekday-fri": { color: "var(--weekday-fri)", texture: solid, dash: "12 3 4 3" },
	"weekday-sat": { color: "var(--weekday-sat)", texture: solid, dash: "7 2 2 2 2 2" },

	"gri-zone-a": { color: "var(--gri-zone-a)", texture: ORDINAL[0] },
	"gri-zone-b": { color: "var(--gri-zone-b)", texture: ORDINAL[1] },
	"gri-zone-c": { color: "var(--gri-zone-c)", texture: ORDINAL[2] },
	"gri-zone-d": { color: "var(--gri-zone-d)", texture: ORDINAL[3] },
	"gri-zone-e": { color: "var(--gri-zone-e)", texture: ORDINAL[4] },

	"ehba1c-zone-healthy": { color: "var(--ehba1c-zone-healthy)", texture: ORDINAL[0] },
	"ehba1c-zone-target": { color: "var(--ehba1c-zone-target)", texture: ORDINAL[1] },
	"ehba1c-zone-high": { color: "var(--ehba1c-zone-high)", texture: ORDINAL[3] },
	"ehba1c-zone-very-high": { color: "var(--ehba1c-zone-very-high)", texture: ORDINAL[4] },

	"cluster-low": { color: "var(--cluster-low)", texture: ORDINAL[1] },
	"cluster-medium": { color: "var(--cluster-medium)", texture: ORDINAL[2] },
	"cluster-high": { color: "var(--cluster-high)", texture: ORDINAL[4] },

	"cat-1": { texture: lines(45, 6), dash: "6 3" },
	"cat-2": { texture: lines(-45, 6), dash: "2 3" },
	"cat-3": { texture: { kind: "grid", gap: 6 }, dash: "8 3 2 3" },
	"cat-4": { texture: { kind: "dots", gap: 6 }, dash: "1 3" },
	"cat-5": { texture: lines(90, 6), dash: "10 4" },
	"cat-6": { texture: lines(0, 6), dash: "4 2 1 2" },
} as const satisfies Record<string, TextureSpec>;

export type TextureKey = keyof typeof CHART_TEXTURES;

/** The six glucose buckets, matching the `--glucose-*` CSS variables. */
export type GlucoseRange = "very-low" | "low" | "in-range" | "tight-range" | "high" | "very-high";

const CATEGORY_KEYS = ["cat-1", "cat-2", "cat-3", "cat-4", "cat-5", "cat-6"] as const satisfies readonly TextureKey[];

export const CATEGORY_PATTERN_COUNT = CATEGORY_KEYS.length;

/** Fill texture for an SVG mark (rect, path, arc, area). */
export function patternClass(key: TextureKey): string {
	return `chart-pat-${key}`;
}

/** Texture for an HTML element whose background carries the colour. */
export function bgPatternClass(key: TextureKey): string {
	return `chart-bgpat-${key}`;
}

/**
 * A key's `stroke-dasharray`, for marks that are dashed on screen as well as on
 * paper. Unlike {@link dashClass} it applies whether or not patterns are on.
 */
export function dashOf(key: TextureKey): string | undefined {
	const spec: TextureSpec = CHART_TEXTURES[key];
	return spec.dash;
}

/** Dash pattern for an SVG stroke. Keys without a `dash` stay solid. */
export function dashClass(key: TextureKey): string {
	return `chart-dash-${key}`;
}

/**
 * Categorical key for a series whose colour is arbitrary (a CGM source, a
 * cluster). `slot` is 1-based and wraps past {@link CATEGORY_PATTERN_COUNT}.
 */
export function categoryKey(slot: number): TextureKey {
	const n = CATEGORY_PATTERN_COUNT;
	const wrapped = ((Math.trunc(slot) % n) + n) % n || n;
	return CATEGORY_KEYS[wrapped - 1];
}

export function categoryPatternClass(slot: number): string {
	return patternClass(categoryKey(slot));
}

export function patternId(key: TextureKey): string {
	return `print-pat-${key}`;
}

const INK = "rgba(0, 0, 0, 0.6)";

function isTextureKey(key: string): key is TextureKey {
	return Object.hasOwn(CHART_TEXTURES, key);
}

function entries(): (readonly [TextureKey, TextureSpec])[] {
	return Object.entries(CHART_TEXTURES).flatMap(([key, spec]) =>
		isTextureKey(key) ? [[key, spec] as const] : []
	);
}

export const TEXTURE_KEYS: readonly TextureKey[] = entries().map(([key]) => key);

/**
 * CSS declarations laying a texture over an element's own background colour,
 * for elements that cannot use an SVG paint server.
 */
function cssTexture(texture: Texture): string | null {
	switch (texture.kind) {
		case "solid":
			return null;
		case "lines":
			// A gradient's stripes run perpendicular to its direction, and CSS angles
			// are measured from "up", so the stripe angle is the gradient angle + 90.
			return `background-image: repeating-linear-gradient(${texture.angle + 90}deg, ${INK} 0 1px, transparent 1px ${texture.gap}px) !important;`;
		case "grid":
			return (
				`background-image: linear-gradient(${INK} 1px, transparent 1px), linear-gradient(90deg, ${INK} 1px, transparent 1px) !important; ` +
				`background-size: ${texture.gap}px ${texture.gap}px !important;`
			);
		case "dots":
			return (
				`background-image: radial-gradient(${INK} 0.9px, transparent 1.1px) !important; ` +
				`background-size: ${texture.gap}px ${texture.gap}px !important;`
			);
	}
}

/** Every `.chart-patterns-on` rule, emitted once by `ChartPrintPatterns`. */
export function textureStylesheet(): string {
	// Translucent fills would wash the texture out, so textured marks go opaque.
	const rules = [".chart-patterns-on [class*='chart-pat-'] { fill-opacity: 1 !important; }"];
	for (const [key, spec] of entries()) {
		rules.push(`.chart-patterns-on .${patternClass(key)} { fill: url(#${patternId(key)}) !important; }`);
		const layer = cssTexture(spec.texture);
		if (layer) rules.push(`.chart-patterns-on .${bgPatternClass(key)} { ${layer} }`);
		if (spec.dash) {
			rules.push(`.chart-patterns-on .${dashClass(key)} { stroke-dasharray: ${spec.dash} !important; }`);
		}
	}
	return rules.join("\n");
}

export interface PatternTile {
	id: string;
	size: number;
	/** `patternTransform` rotation; the tile's stroke is vertical before it. */
	rotate: number;
	color?: string;
	shape: "none" | "line" | "grid" | "dot";
	ink: string;
}

export function patternTiles(): PatternTile[] {
	return entries().map(([key, spec]) => {
		const base = { id: patternId(key), color: spec.color, ink: INK };
		const t = spec.texture;
		switch (t.kind) {
			case "solid":
				return { ...base, size: 4, rotate: 0, shape: "none" };
			case "lines":
				return { ...base, size: t.gap, rotate: t.angle, shape: "line" };
			case "grid":
				return { ...base, size: t.gap, rotate: 0, shape: "grid" };
			case "dots":
				return { ...base, size: t.gap, rotate: 0, shape: "dot" };
		}
	});
}
