/**
 * Unit tests for formatting utilities.
 *
 * Tests only the pure functions that accept explicit parameters.
 * The convenience wrappers (bg, bgDelta, etc.) depend on a global
 * appearance store that requires Svelte runtime — those are covered
 * in browser tests instead.
 */
import { describe, it, expect, vi } from "vitest";

// `formatting.ts` imports from appearance-store which imports mode-watcher.
// mode-watcher has no node export. We stub the entire chain so Vite never
// resolves the real package.
vi.mock("$app/environment", () => ({ browser: false }));
vi.mock("mode-watcher", () => ({}));
vi.mock("runed", () => ({
	PersistedState: class { current: any; constructor(v: any) { this.current = v; } },
}));
vi.mock("$lib/stores/appearance-store.svelte", () => ({
	glucoseUnits: { current: "mg/dl" },
}));

// Dynamic import so mocks are established first
const {
	convertToDisplayUnits,
	convertFromDisplayUnits,
	formatGlucoseValue,
	formatGlucoseDelta,
	getUnitLabel,
	formatGlucoseRange,
	formatDateTime,
	formatDate,
	formatDateDetailed,
	formatDateForInput,
	formatDateTimeCompact,
	formatInsulinDisplay,
	formatCarbDisplay,
	formatPercentageDisplay,
	formatGlucose,
	formatEventType,
	formatNotes,
} = await import("./formatting");

describe("Glucose conversion", () => {
	describe("convertToDisplayUnits", () => {
		it("returns rounded mg/dL for mg/dl units", () => {
			expect(convertToDisplayUnits(120.7, "mg/dl")).toBe(121);
			expect(convertToDisplayUnits(100, "mg/dl")).toBe(100);
		});

		it("converts mg/dL to mmol/L", () => {
			expect(convertToDisplayUnits(180, "mmol")).toBe(10);
			expect(convertToDisplayUnits(90, "mmol")).toBe(5);
		});

		it("rounds mmol values to 1 decimal", () => {
			const result = convertToDisplayUnits(120, "mmol");
			expect(result).toBe(6.7);
		});
	});

	describe("convertFromDisplayUnits", () => {
		it("returns rounded value for mg/dl", () => {
			expect(convertFromDisplayUnits(120, "mg/dl")).toBe(120);
		});

		it("converts mmol/L back to mg/dL", () => {
			expect(convertFromDisplayUnits(10, "mmol")).toBe(180);
		});

		it("is roughly inverse of convertToDisplayUnits", () => {
			const original = 120;
			const mmol = convertToDisplayUnits(original, "mmol");
			const back = convertFromDisplayUnits(mmol, "mmol");
			expect(Math.abs(back - original)).toBeLessThanOrEqual(2);
		});
	});

	describe("formatGlucoseValue", () => {
		it("returns integer for mg/dl", () => {
			expect(formatGlucoseValue(120.5, "mg/dl")).toBe(121);
		});

		it("returns 1 decimal for mmol", () => {
			expect(formatGlucoseValue(180, "mmol")).toBe(10);
		});
	});

	describe("formatGlucoseDelta", () => {
		it("includes + sign for positive values in mg/dl", () => {
			expect(formatGlucoseDelta(10, "mg/dl")).toBe("+10");
		});

		it("includes - sign for negative values", () => {
			expect(formatGlucoseDelta(-15, "mg/dl")).toBe("-15");
		});

		it("omits sign when includeSign is false", () => {
			expect(formatGlucoseDelta(10, "mg/dl", false)).toBe("10");
		});

		it("formats mmol deltas with 1 decimal", () => {
			expect(formatGlucoseDelta(18, "mmol")).toBe("+1.0");
		});

		it("handles zero delta", () => {
			expect(formatGlucoseDelta(0, "mg/dl")).toBe("0");
		});
	});

	describe("getUnitLabel", () => {
		it("returns mg/dL for mg/dl", () => {
			expect(getUnitLabel("mg/dl")).toBe("mg/dL");
		});

		it("returns mmol/L for mmol", () => {
			expect(getUnitLabel("mmol")).toBe("mmol/L");
		});
	});

	describe("formatGlucoseRange", () => {
		it("formats range in mg/dL", () => {
			expect(formatGlucoseRange(70, 180, "mg/dl")).toBe("70-180 mg/dL");
		});

		it("formats range in mmol/L", () => {
			const result = formatGlucoseRange(70, 180, "mmol");
			expect(result).toContain("mmol/L");
		});
	});
});

describe("Date formatting", () => {
	describe("formatDateTime", () => {
		it("returns — for undefined", () => {
			expect(formatDateTime(undefined)).toBe("—");
		});

		it("formats a valid date string", () => {
			const result = formatDateTime("2025-06-15T10:30:00Z");
			expect(result).toBeTruthy();
			expect(result).not.toBe("—");
		});
	});

	describe("formatDate", () => {
		it("returns N/A for undefined", () => {
			expect(formatDate(undefined)).toBe("N/A");
		});

		it("formats a Date object", () => {
			const result = formatDate(new Date(2025, 0, 1));
			expect(result).not.toBe("N/A");
		});

		it("formats a string date", () => {
			const result = formatDate("2025-06-15T10:30:00Z");
			expect(result).not.toBe("N/A");
		});
	});

	describe("formatDateDetailed", () => {
		it("returns Unknown for undefined", () => {
			expect(formatDateDetailed(undefined)).toBe("Unknown");
		});

		it("formats a valid date with full details", () => {
			const result = formatDateDetailed("2025-06-15T10:30:00Z");
			expect(result).not.toBe("Unknown");
		});
	});

	describe("formatDateForInput", () => {
		it("returns empty string for undefined", () => {
			expect(formatDateForInput(undefined)).toBe("");
		});

		it("formats for datetime-local input", () => {
			const result = formatDateForInput("2025-06-15T10:30:00Z");
			expect(result).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);
		});
	});

	describe("formatDateTimeCompact", () => {
		it("returns — for undefined", () => {
			expect(formatDateTimeCompact(undefined)).toBe("—");
		});

		it("formats a valid date", () => {
			const result = formatDateTimeCompact("2025-06-15T10:30:00Z");
			expect(result).not.toBe("—");
		});
	});
});

describe("Treatment formatting", () => {
	describe("formatInsulinDisplay", () => {
		it("returns N/A for undefined", () => {
			expect(formatInsulinDisplay(undefined)).toBe("N/A");
		});

		it("returns N/A for null", () => {
			expect(formatInsulinDisplay(null as any)).toBe("N/A");
		});

		it("formats to 2 decimal places", () => {
			expect(formatInsulinDisplay(5)).toBe("5.00");
			expect(formatInsulinDisplay(1.5)).toBe("1.50");
			expect(formatInsulinDisplay(3.456)).toBe("3.46");
		});
	});

	describe("formatCarbDisplay", () => {
		it("returns N/A for undefined", () => {
			expect(formatCarbDisplay(undefined)).toBe("N/A");
		});

		it("formats to 0 decimal places", () => {
			expect(formatCarbDisplay(45)).toBe("45");
			expect(formatCarbDisplay(45.7)).toBe("46");
		});
	});

	describe("formatPercentageDisplay", () => {
		it("returns N/A for undefined", () => {
			expect(formatPercentageDisplay(undefined)).toBe("N/A");
		});

		it("formats to 1 decimal place", () => {
			expect(formatPercentageDisplay(72.5)).toBe("72.5");
			expect(formatPercentageDisplay(100)).toBe("100.0");
		});
	});

	describe("formatGlucose", () => {
		it("returns - when glucose is falsy", () => {
			expect(formatGlucose({} as any)).toBe("-");
		});

		it("returns - when glucose is 0", () => {
			expect(formatGlucose({ glucose: 0 } as any)).toBe("-");
		});

		it("formats glucose with type", () => {
			expect(
				formatGlucose({ glucose: 120, glucoseType: "Finger" } as any),
			).toBe("120 (Finger)");
		});

		it("formats glucose without type", () => {
			expect(formatGlucose({ glucose: 120 } as any)).toBe("120");
		});
	});

	describe("formatEventType", () => {
		it("returns event type", () => {
			expect(formatEventType({ eventType: "BG Check" } as any)).toBe("BG Check");
		});

		it("appends reason when present", () => {
			expect(
				formatEventType({ eventType: "Correction", reason: "High BG" } as any),
			).toBe("Correction - High BG");
		});

		it("returns Unknown when eventType is missing", () => {
			expect(formatEventType({} as any)).toBe("Unknown");
		});
	});

	describe("formatNotes", () => {
		it("returns empty string when no notes or enteredBy", () => {
			expect(formatNotes({} as any)).toBe("");
		});

		it("returns notes when present", () => {
			expect(formatNotes({ notes: "Test note" } as any)).toBe("Test note");
		});

		it("returns enteredBy when present", () => {
			expect(formatNotes({ enteredBy: "admin" } as any)).toBe("by admin");
		});

		it("combines notes and enteredBy", () => {
			expect(
				formatNotes({ notes: "Test note", enteredBy: "admin" } as any),
			).toBe("Test note by admin");
		});
	});
});
