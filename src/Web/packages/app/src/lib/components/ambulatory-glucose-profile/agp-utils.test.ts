import { describe, it, expect, vi } from "vitest";

vi.mock("$app/environment", () => ({ browser: false }));
vi.mock("mode-watcher", () => ({}));
vi.mock("runed", () => ({
	PersistedState: class {
		current: any;
		constructor(v: any) {
			this.current = v;
		}
	},
}));
vi.mock("$lib/stores/appearance-store.svelte", () => ({
	glucoseUnits: { current: "mg/dl" },
}));

const { formatHour, transformStats, AGP_LOW_THRESHOLD } = await import(
	"./agp-utils"
);

// ---------------------------------------------------------------------------
// formatHour
// ---------------------------------------------------------------------------
describe("formatHour", () => {
	describe("12-hour format", () => {
		it("formats midnight as 12am", () => {
			expect(formatHour(0, false)).toBe("12am");
		});

		it("formats noon as 12pm", () => {
			expect(formatHour(12, false)).toBe("12pm");
		});

		it("formats morning hours", () => {
			expect(formatHour(1, false)).toBe("1am");
			expect(formatHour(6, false)).toBe("6am");
			expect(formatHour(11, false)).toBe("11am");
		});

		it("formats afternoon hours", () => {
			expect(formatHour(13, false)).toBe("1pm");
			expect(formatHour(18, false)).toBe("6pm");
			expect(formatHour(23, false)).toBe("11pm");
		});
	});

	describe("24-hour format", () => {
		it("zero-pads single-digit hours", () => {
			expect(formatHour(0, true)).toBe("00:00");
			expect(formatHour(1, true)).toBe("01:00");
			expect(formatHour(9, true)).toBe("09:00");
		});

		it("does not pad double-digit hours", () => {
			expect(formatHour(10, true)).toBe("10:00");
			expect(formatHour(23, true)).toBe("23:00");
		});
	});
});

// ---------------------------------------------------------------------------
// AGP_LOW_THRESHOLD
// ---------------------------------------------------------------------------
describe("AGP_LOW_THRESHOLD", () => {
	it("is 70 mg/dL (clinical hypoglycemia, not Level 2 at 55)", () => {
		expect(AGP_LOW_THRESHOLD).toBe(70);
	});
});

// ---------------------------------------------------------------------------
// transformStats
// ---------------------------------------------------------------------------
describe("transformStats", () => {
	it("returns empty array for empty input", () => {
		expect(transformStats([], "mg/dl")).toEqual([]);
	});

	it("converts mg/dL values (rounds to nearest integer)", () => {
		const stats = [
			{
				hour: 6,
				median: 140,
				percentiles: { p10: 80, p25: 100, p75: 180, p90: 220 },
			},
		];
		const result = transformStats(stats, "mg/dl");

		expect(result[0].median).toBe(140);
		expect(result[0].percentiles).toEqual({
			p10: 80,
			p25: 100,
			p75: 180,
			p90: 220,
		});
	});

	it("converts to mmol/L with 1 decimal precision", () => {
		const stats = [
			{
				hour: 12,
				median: 180,
				percentiles: { p10: 70, p25: 100, p75: 200, p90: 300 },
			},
		];
		const result = transformStats(stats, "mmol");

		expect(result[0].median).toBe(10.0);
		expect(result[0].percentiles!.p10).toBe(3.9);
		expect(result[0].percentiles!.p25).toBe(5.6);
		expect(result[0].percentiles!.p75).toBe(11.1);
		expect(result[0].percentiles!.p90).toBe(16.7);
	});

	it("defaults missing median to 0", () => {
		const stats = [{ hour: 0 }];
		const result = transformStats(stats, "mg/dl");

		expect(result[0].median).toBe(0);
	});

	it("defaults missing percentile values to 0", () => {
		const stats = [
			{
				hour: 0,
				median: 100,
				percentiles: {},
			},
		];
		const result = transformStats(stats, "mg/dl");

		expect(result[0].percentiles).toEqual({
			p10: 0,
			p25: 0,
			p75: 0,
			p90: 0,
		});
	});

	it("preserves undefined percentiles when not provided", () => {
		const stats = [{ hour: 0, median: 100 }];
		const result = transformStats(stats, "mg/dl");

		expect(result[0].percentiles).toBeUndefined();
	});

	it("preserves extra fields from AveragedStats", () => {
		const stats = [
			{
				hour: 3,
				median: 120,
				count: 48,
				standardDeviation: 25,
				timeInRange: { normal: 85, low: 5, high: 10 },
			},
		];
		const result = transformStats(stats, "mg/dl");

		expect(result[0].hour).toBe(3);
		expect(result[0].count).toBe(48);
		expect(result[0].standardDeviation).toBe(25);
		expect(result[0].timeInRange).toEqual({
			normal: 85,
			low: 5,
			high: 10,
		});
	});

	it("handles all 24 hours of data", () => {
		const stats = Array.from({ length: 24 }, (_, i) => ({
			hour: i,
			median: 100 + i * 5,
			percentiles: {
				p10: 70 + i,
				p25: 85 + i * 2,
				p75: 115 + i * 3,
				p90: 130 + i * 4,
			},
		}));
		const result = transformStats(stats, "mg/dl");

		expect(result).toHaveLength(24);
		expect(result[0].hour).toBe(0);
		expect(result[23].hour).toBe(23);
		expect(result[23].median).toBe(215);
	});
});
