/**
 * Unit tests for formatting utilities.
 *
 * Tests only the pure functions that accept explicit parameters.
 * The convenience wrappers (bg, bgDelta, etc.) depend on a global
 * appearance store that requires Svelte runtime — those are covered
 * in browser tests instead.
 */
import { describe, it, expect, vi } from "vitest";
import type { RegionFormat } from "$lib/stores/appearance-store.svelte";

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
	timeFormat: { current: "12" },
	regionFormat: { current: "" },
	preferredLanguage: { current: "en" },
}));

// Dynamic import so mocks are established first
const {
	convertToDisplayUnits,
	convertFromDisplayUnits,
	formatGlucoseValue,
	formatGlucoseDelta,
	getUnitLabel,
	formatGlucoseRange,
	formatLocale,
	prefersHour12,
	formatLongDate,
	formatMediumDate,
	formatMediumDateTime,
	formatMonthYear,
	formatMonthLabel,
	formatWeekdayLabel,
	formatClock,
	formatDayTime,
	formatNumber,
	formatNumericDate,
	time,
	formatShortDate,
	formatWeekdayDate,
	formatDateTime,
	formatDate,
	formatDateDetailed,
	formatDateForInput,
	formatDateTimeCompact,
	minutesAgo,
	formatInsulinDisplay,
	formatCarbDisplay,
	formatPercentageDisplay,
	formatGlucose,
	formatEventType,
	formatNotes,
} = await import("./formatting");

// The mocked store holds plain objects, so a test can move a preference and read
// the effect the same way the app does.
const store = await import("$lib/stores/appearance-store.svelte");
/** Run `body` with the 12/24 time-format preference set to `value`. */
function withTimeFormat<T>(value: "12" | "24", run: () => T): T {
	const previous = store.timeFormat.current;
	store.timeFormat.current = value;
	try {
		return run();
	} finally {
		store.timeFormat.current = previous;
	}
}

/** Run `body` with the regional-format preference set to `tag`. */
function withRegionValue<T>(tag: RegionFormat, run: () => T): T {
	const previous = store.regionFormat.current;
	store.regionFormat.current = tag;
	try {
		return run();
	} finally {
		store.regionFormat.current = previous;
	}
}

function withRegion(tag: RegionFormat, run: () => void): void {
	withRegionValue(tag, run);
}

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
	describe("minutesAgo", () => {
		it("formats elapsed minutes with Intl relative time formatting", () => {
			const expected = new Intl.RelativeTimeFormat("en", {
				numeric: "always",
				style: "short",
			}).format(-5, "minute");

			expect(minutesAgo(1_000, 301_000)).toBe(expected);
		});

		it("does not return negative elapsed minutes", () => {
			const expected = new Intl.RelativeTimeFormat("en", {
				numeric: "always",
				style: "short",
			}).format(-0, "minute");

			expect(minutesAgo(301_000, 1_000)).toBe(expected);
		});
	});

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

describe("Regional format", () => {
	it("falls back to the display language when no region is chosen", () => {
		expect(formatLocale()).toBe("en");
	});

	it("prefers the regional format over the display language", () => {
		withRegion("en-GB", () => expect(formatLocale()).toBe("en-GB"));
	});

	it("writes the day before the month for a European region", () => {
		const date = new Date(2026, 11, 31, 9, 5);
		withRegion("en-GB", () =>
			expect(formatShortDate(date, true)).toBe("31 Dec 2026"),
		);
		withRegion("en-US", () =>
			expect(formatShortDate(date, true)).toBe("Dec 31, 2026"),
		);
	});

	it("names the weekday in the regional format", () => {
		const date = new Date(2026, 11, 31);
		withRegion("de-DE", () => expect(formatWeekdayDate(date)).toContain("Do"));
		withRegion("en-US", () => expect(formatWeekdayDate(date)).toContain("Thu"));
	});
});

describe("Shared date shapes", () => {
	// A fixed local instant: these helpers read the viewer's clock, so building it
	// locally is what a caller does.
	const date = new Date(2026, 7, 29, 14, 5);

	function shapes() {
		return {
			long: formatLongDate(date),
			medium: formatMediumDate(date),
			mediumTime: formatMediumDateTime(date),
			monthYear: formatMonthYear(date),
			month: formatMonthLabel(date),
			weekday: formatWeekdayLabel(date),
			numeric: formatNumericDate(date),
		};
	}

	it("names months and weekdays in the regional format", () => {
		const english = withRegionValue("en-GB", shapes);
		const german = withRegionValue("de-DE", shapes);

		// August is spelled alike; the ordering and the weekday are what differ.
		expect(english.long).toMatch(/^Saturday, 29 August 2026$/);
		expect(german.long).toMatch(/^Samstag, 29\. August 2026$/);
		expect(english.long).toContain("Saturday");
		expect(german.long).toContain("Samstag");
		expect(english.weekday).toBe("Sat");
		expect(german.weekday).toMatch(/^Sa\.?$/);
		expect(english.month).toBe("Aug");
		expect(english.monthYear).toBe("August 2026");
	});

	it("writes each shape at its own precision", () => {
		const s = withRegionValue("en-GB", shapes);

		expect(s.medium).toBe("29 Aug 2026");
		expect(s.mediumTime).toContain("29 Aug 2026");
		// en-GB writes a 24-hour clock, and this helper follows the locale.
		expect(s.mediumTime).toContain("14:05");
		expect(s.numeric).toBe("29/08/2026");
	});

	it("writes the clock in the preferred format where the surface follows it", () => {
		withRegion("en-GB", () => expect(time(date)).toBe("2:05 pm"));
	});

	it("leaves the clock to the locale on the surfaces that never followed a preference", () => {
		// German has no day-period abbreviation, so ICU would hand a German reader the
		// English "AM" if the preference won here. The second iteration is what pins it:
		// making these helpers read `prefersHour12()` fails only under "12".
		for (const preference of ["12", "24"] as const) {
			withTimeFormat(preference, () => {
				expect(withRegionValue("en-GB", () => formatDayTime(date))).toContain("14:05");
				expect(withRegionValue("de-DE", () => formatDayTime(date))).toContain("14:05");
				// Normalised: ICU emits a narrow no-break space before the day period
				// in some builds and a plain one in others.
				expect(
					withRegionValue("en-US", () => formatDayTime(date)).replace(/\s/g, " ")
				).toContain("2:05 PM");
				expect(withRegionValue("de-DE", () => formatMediumDateTime(date))).toContain("14:05");
			});
		}
	});

	it("follows the 12/24 preference on the surfaces that always did", () => {
		withRegionValue("en-GB", () => {
			withTimeFormat("24", () => {
				expect(time(date)).toBe("14:05");
				expect(formatDateTimeCompact(date)).toContain("14:05");
			});
			withTimeFormat("12", () => {
				expect(time(date)).toBe("2:05 pm");
				expect(formatDateTimeCompact(date)).toMatch(/02:05\s*pm/i);
			});
		});
	});

	it("lets the locale glue the date to the time", () => {
		// Composing the halves and joining them by hand hardcodes ", " — ja-JP, zh-CN
		// and ko-KR write a space there, and ar-EG U+060C.
		const japanese = withRegionValue("ja-JP", () => formatDayTime(date));
		expect(japanese).toContain("14:05");
		expect(japanese).not.toContain(",");
	});

	it("takes the hour style from the locale, not from the call site", () => {
		// Asserted literally rather than against a re-derived option set, which would
		// move with the implementation.
		const american = withRegionValue("en-US", () => formatDayTime(date)).replace(/\s/g, " ");
		expect(american).toContain("2:05 PM");
		expect(american).not.toContain("02:05");

		expect(withRegionValue("en-GB", () => formatDayTime(date))).toContain("14:05");
		expect(withRegionValue("en-GB", () => formatClock(date))).toBe("14:05");
		expect(withRegionValue("en-GB", () => formatClock(date, { seconds: true }))).toBe(
			"14:05:00"
		);
	});

	it("reads an unusable value as no time rather than as 1970", () => {
		// A nullable `mills` would otherwise render "Invalid Date" or the epoch into a
		// treatment row.
		expect(time(undefined)).toBe("—");
		expect(time(null)).toBe("—");
		expect(time("not a date")).toBe("—");
	});

	it("reads a wire value, not only a Date", () => {
		// NSwag types DTO date fields as `Date`, but the generated client parses with
		// no reviver, so what actually arrives is an ISO string.
		const iso = "2026-08-29T14:05:00.000Z";
		withRegion("en-GB", () => {
			expect(() => time(iso)).not.toThrow();
			expect(time(iso)).toBe(time(new Date(iso)));
			expect(time(new Date(iso).getTime())).toBe(time(new Date(iso)));
		});
	});

	it("groups numbers in the regional format", () => {
		withRegion("de-DE", () => expect(formatNumber(1234567)).toBe("1.234.567"));
		withRegion("en-US", () => expect(formatNumber(1234567)).toBe("1,234,567"));
	});

	it("reads a missing count as zero rather than as text", () => {
		withRegion("en-US", () => {
			expect(formatNumber(undefined)).toBe("0");
			expect(formatNumber(null)).toBe("0");
		});
	});
});

describe("prefersHour12", () => {

	it("follows the time-format preference when not overridden", () => {
		expect(prefersHour12()).toBe(true);
	});

	it("lets a caller pin the format", () => {
		expect(prefersHour12(false)).toBe(false);
		expect(prefersHour12(true)).toBe(true);
	});
});
