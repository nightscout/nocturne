/**
 * Unit tests for formatting utilities.
 *
 * Tests only the pure functions that accept explicit parameters.
 * The convenience wrappers (bg, bgDelta, etc.) depend on a global
 * appearance store that requires Svelte runtime — those are covered
 * in browser tests instead.
 */
import { describe, it, expect, vi } from "vitest";
import {
	relativeAge as sharedRelativeAge,
	relativeTime as sharedRelativeTime,
} from "@nocturne/ui/relative-time";
import type { RegionFormat } from "$lib/stores/appearance-store.svelte";

// `formatting.ts` imports from appearance-store which imports mode-watcher.
// mode-watcher has no node export. We stub the entire chain so Vite never
// resolves the real package.
vi.mock("$app/environment", () => ({ browser: false }));
vi.mock("mode-watcher", () => ({}));
vi.mock("runed", () => ({
	PersistedState: class { current: unknown; constructor(v: unknown) { this.current = v; } },
}));
vi.mock("$lib/stores/appearance-store.svelte", () => ({
	glucoseUnits: { current: "mg/dl" },
	carbRatioFormat: { current: "1:x" },
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
	formatFileSize,
	formatInsulinToCarbRatio,
	writesUnitsPerTenGrams,
	formatNumber,
	formatNumericDate,
	formatMediumDateRange,
	time,
	formatShortDate,
	formatWeekdayDate,
	formatDateTime,
	formatDateDetailed,
	formatDateForInput,
	formatDateTimeCompact,
	relativeAge,
	relativeTime,
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
	describe("relativeAge", () => {
		const NOW = Date.UTC(2026, 7, 29, 14, 5);
		const MIN = 60_000;
		const HOUR = 60 * MIN;
		const DAY = 24 * HOUR;
		const narrow = (locale: string) =>
			new Intl.RelativeTimeFormat(locale, { style: "narrow", numeric: "auto" });
		const ago = (ms: number, opts: { fallback?: string } = {}) =>
			relativeAge(NOW - ms, { now: NOW, ...opts });

		it("reads under a minute as now, and a minute from 60 seconds", () => {
			expect(ago(59_000)).toBe(narrow("en").format(0, "second"));
			expect(ago(60_000)).toBe(narrow("en").format(-1, "minute"));
		});

		it("switches from minutes to hours at 60 minutes", () => {
			expect(ago(59 * MIN)).toBe(narrow("en").format(-59, "minute"));
			expect(ago(60 * MIN)).toBe(narrow("en").format(-1, "hour"));
		});

		it("switches from hours to days at 24 hours", () => {
			expect(ago(23 * HOUR + 59 * MIN)).toBe(narrow("en").format(-23, "hour"));
			expect(ago(24 * HOUR)).toBe(narrow("en").format(-1, "day"));
		});

		it("names one whole unit, never a compound", () => {
			expect(ago(3 * HOUR + 5 * MIN)).toBe(narrow("en").format(-3, "hour"));
			expect(ago(2 * DAY + 7 * HOUR)).toBe(narrow("en").format(-2, "day"));
		});

		it("gives the absolute date from a week on", () => {
			expect(ago(6 * DAY + 23 * HOUR)).toBe(narrow("en").format(-6, "day"));
			expect(ago(7 * DAY)).toBe(formatNumericDate(NOW - 7 * DAY));
		});

		it("reads a future moment as now", () => {
			expect(relativeAge(NOW + 5 * MIN, { now: NOW })).toBe(
				narrow("en").format(0, "second")
			);
		});

		it("accepts a Date, an ISO string and Unix milliseconds alike", () => {
			const at = NOW - 5 * MIN;
			const expected = narrow("en").format(-5, "minute");
			expect(relativeAge(at, { now: NOW })).toBe(expected);
			expect(relativeAge(new Date(at), { now: NOW })).toBe(expected);
			expect(relativeAge(new Date(at).toISOString(), { now: NOW })).toBe(expected);
		});

		it("returns the caller's fallback for a missing or invalid moment", () => {
			for (const bad of [undefined, null, "", 0, "not a date", new Date(NaN)]) {
				expect(relativeAge(bad, { now: NOW, fallback: "Never" })).toBe("Never");
				expect(relativeAge(bad, { now: NOW })).toBe("");
			}
		});

		it("writes the words in the regional format's language", () => {
			withRegion("de-DE", () => {
				const de = narrow("de");
				expect(ago(10_000)).toBe(de.format(0, "second"));
				expect(ago(5 * MIN)).toBe(de.format(-5, "minute"));
				expect(ago(3 * HOUR)).toBe(de.format(-3, "hour"));
				expect(ago(2 * DAY)).toBe(de.format(-2, "day"));
			});
			expect(narrow("de").format(-5, "minute")).not.toBe(
				narrow("en").format(-5, "minute")
			);
		});

		it("reads a moment ahead in the future tense and one behind in the past", () => {
			const at = (ms: number) => relativeTime(NOW + ms, { now: NOW });
			expect(at(5 * MIN)).toBe(narrow("en").format(5, "minute"));
			expect(at(3 * HOUR + 5 * MIN)).toBe(narrow("en").format(3, "hour"));
			expect(at(2 * DAY + 7 * HOUR)).toBe(narrow("en").format(2, "day"));
			expect(at(-3 * HOUR)).toBe(narrow("en").format(-3, "hour"));
			expect(at(29_000)).toBe(narrow("en").format(0, "second"));
			expect(at(7 * DAY)).toBe(formatNumericDate(NOW + 7 * DAY));
			expect(at(5 * MIN)).not.toBe(at(-5 * MIN));
		});

		it("never words a future moment under two days as tomorrow", () => {
			const always = (locale: string) =>
				new Intl.RelativeTimeFormat(locale, { style: "narrow", numeric: "always" });
			const at = (ms: number) => relativeTime(NOW + ms, { now: NOW });
			expect(at(47 * HOUR)).toBe(always("en").format(2, "day"));
			expect(at(25 * HOUR)).toBe(always("en").format(1, "day"));
			expect(at(90 * MIN)).toBe(always("en").format(2, "hour"));
			withRegion("de-DE", () => {
				expect(at(47 * HOUR)).toBe(always("de").format(2, "day"));
				expect(at(25 * HOUR)).toBe(always("de").format(1, "day"));
				expect(at(90 * MIN)).toBe(always("de").format(2, "hour"));
			});
			expect(at(25 * HOUR)).not.toBe(narrow("en").format(1, "day"));
		});

		it("writes a future moment in the regional format's language", () => {
			withRegion("de-DE", () => {
				expect(relativeTime(NOW + 3 * HOUR, { now: NOW })).toBe(
					narrow("de").format(3, "hour")
				);
			});
			expect(narrow("de").format(3, "hour")).not.toBe(narrow("en").format(3, "hour"));
		});

		it("builds one formatter per locale, however many times it is called", () => {
			// ICU answers "nb-NO" with "nb", so a cache keyed on the resolved tag never matches.
			expect(new Intl.RelativeTimeFormat("nb-NO").resolvedOptions().locale).toBe("nb");
			const real = Intl.RelativeTimeFormat;
			let built = 0;
			Reflect.set(
				Intl,
				"RelativeTimeFormat",
				class extends real {
					constructor(...args: ConstructorParameters<typeof real>) {
						built++;
						super(...args);
					}
				}
			);
			try {
				withRegion("nb-NO", () => {
					for (let i = 0; i < 5; i++) ago(5 * MIN);
				});
			} finally {
				Reflect.set(Intl, "RelativeTimeFormat", real);
			}
			expect(built).toBe(1);
		});
	});

	describe("formatMediumDateTime", () => {
		it("reads every unusable value as no value rather than as 1970", () => {
			// null is the case that needs the explicit guard: it coerces to the epoch and would
			// otherwise render a date. The rest reach the NaN branch on their own.
			expect(formatMediumDateTime(null)).toBe("—");
			expect(formatMediumDateTime(undefined)).toBe("—");
			expect(formatMediumDateTime("")).toBe("—");
			expect(formatMediumDateTime("not a date")).toBe("—");
		});

		it("names the month and drops the seconds, whatever the region writes", () => {
			// What the "Created"/"Last used" rows across the app are built from: a bare
			// toLocaleString gave "4/30/2026, 11:00:39 PM", which is unreadable to anyone who
			// does not share the locale's field order, and precise to a second nothing needs.
			const stamp = new Date(2026, 7, 29, 14, 5, 9);
			const british = withRegionValue("en-GB", () => formatMediumDateTime(stamp));
			expect(british).toContain("29 Aug 2026");
			expect(british).toContain("14:05");
			expect(british).not.toContain(":09");

			expect(withRegionValue("de-DE", () => formatMediumDateTime(stamp))).toMatch(/Aug/);
			expect(withRegionValue("en-US", () => formatMediumDateTime(stamp))).toMatch(
				/Aug 29, 2026/
			);
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
			expect(formatInsulinDisplay(null)).toBe("N/A");
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
			expect(formatGlucose({})).toBe("-");
		});

		it("returns - when glucose is 0", () => {
			expect(formatGlucose({ glucose: 0 })).toBe("-");
		});

		it("formats glucose with type", () => {
			expect(
				formatGlucose({ glucose: 120, glucoseType: "Finger" }),
			).toBe("120 (Finger)");
		});

		it("formats glucose without type", () => {
			expect(formatGlucose({ glucose: 120 })).toBe("120");
		});
	});

	describe("formatEventType", () => {
		it("returns event type", () => {
			expect(formatEventType({ eventType: "BG Check" })).toBe("BG Check");
		});

		it("appends reason when present", () => {
			expect(
				formatEventType({ eventType: "Correction", reason: "High BG" }),
			).toBe("Correction - High BG");
		});

		it("returns Unknown when eventType is missing", () => {
			expect(formatEventType({})).toBe("Unknown");
		});
	});

	describe("formatNotes", () => {
		it("returns empty string when no notes or enteredBy", () => {
			expect(formatNotes({})).toBe("");
		});

		it("returns notes when present", () => {
			expect(formatNotes({ notes: "Test note" })).toBe("Test note");
		});

		it("returns enteredBy when present", () => {
			expect(formatNotes({ enteredBy: "admin" })).toBe("by admin");
		});

		it("combines notes and enteredBy", () => {
			expect(
				formatNotes({ notes: "Test note", enteredBy: "admin" }),
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

	it("writes a range through ICU rather than joining two dates", () => {
		const end = new Date(2026, 8, 3, 9, 0);
		// Collapsing the shared year is the tell that ICU formatted the range: hyphenating two
		// whole dates by hand would repeat "2026" on both sides.
		const british = withRegionValue("en-GB", () => formatMediumDateRange(date, end));
		expect(british).toContain("29 Aug");
		// "Sept", not "Sep": the abbreviation is ICU's to choose, and en-GB writes four letters.
		expect(british).toContain("3 Sept 2026");
		expect(british.match(/2026/g)).toHaveLength(1);

		expect(withRegionValue("en-US", () => formatMediumDateRange(date, end))).toContain(
			"Aug 29"
		);
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

	it("names a byte count in the largest unit that keeps it above one", () => {
		withRegion("en-US", () => {
			expect(formatFileSize(512)).toBe("512 B");
			expect(formatFileSize(1536)).toBe("1.5 KB");
			expect(formatFileSize(5 * 1024 * 1024)).toBe("5.0 MB");
			expect(formatFileSize(3 * 1024 ** 3)).toBe("3.0 GB");
		});
		withRegion("de-DE", () => expect(formatFileSize(1536)).toBe("1,5 KB"));
	});

	it("reads a missing count as zero rather than as text", () => {
		withRegion("en-US", () => {
			expect(formatNumber(undefined)).toBe("0");
			expect(formatNumber(null)).toBe("0");
		});
	});
});

describe("prefersHour12", () => {

	it("follows the time-format preference", () => {
		expect(prefersHour12()).toBe(true);
	});
});

describe("formatInsulinToCarbRatio", () => {
	it("writes grams per unit as 1:x with one decimal", () => {
		expect(formatInsulinToCarbRatio(10, "1:x")).toBe("1:10.0");
		expect(formatInsulinToCarbRatio(12.34, "1:x")).toBe("1:12.3");
	});

	it("writes grams per unit as g/U", () => {
		expect(formatInsulinToCarbRatio(9.8, "g/U")).toBe("9.8 g/U");
	});

	it("writes units for ten grams with two decimals", () => {
		expect(formatInsulinToCarbRatio(8, "U/10g")).toBe("1.25 U/10 g");
		expect(formatInsulinToCarbRatio(11.5, "U/10g")).toBe("0.87 U/10 g");
	});

	it("writes a dash for a ratio that is not positive", () => {
		expect(formatInsulinToCarbRatio(0, "U/10g")).toBe("–");
	});

	it("writes a dash when there is no ratio", () => {
		expect(formatInsulinToCarbRatio(null)).toBe("–");
		expect(formatInsulinToCarbRatio(undefined)).toBe("–");
	});

	it("uses the chosen format when none is given", () => {
		expect(formatInsulinToCarbRatio(10)).toBe("1:10.0");
	});
});

describe("writesUnitsPerTenGrams", () => {
	it("is true only for units per 10 g", () => {
		expect(writesUnitsPerTenGrams("U/10g")).toBe(true);
		expect(writesUnitsPerTenGrams("g/U")).toBe(false);
		expect(writesUnitsPerTenGrams("1:x")).toBe(false);
	});

	it("follows the chosen format when none is given", () => {
		expect(writesUnitsPerTenGrams()).toBe(false);
	});
});

describe("shared relative time (@nocturne/ui)", () => {
	const NOW = Date.UTC(2026, 7, 29, 14, 5);
	const MIN = 60_000;
	const narrow = (locale: string, numeric: Intl.RelativeTimeFormatNumeric = "auto") =>
		new Intl.RelativeTimeFormat(locale, { style: "narrow", numeric });

	it("words a past moment in the locale it is given", () => {
		expect(sharedRelativeAge(NOW - 5 * MIN, "de", { now: NOW })).toBe(
			narrow("de").format(-5, "minute")
		);
		expect(sharedRelativeAge(NOW - 5 * MIN, "en", { now: NOW })).not.toBe(
			sharedRelativeAge(NOW - 5 * MIN, "de", { now: NOW })
		);
	});

	it("reads a future moment as now for an age and as ahead for a time", () => {
		expect(sharedRelativeAge(NOW + 5 * MIN, "de", { now: NOW })).toBe(
			narrow("de").format(0, "second")
		);
		expect(sharedRelativeTime(NOW + 47 * 60 * MIN, "de", { now: NOW })).toBe(
			narrow("de", "always").format(2, "day")
		);
	});

	it("gives the absolute date, in the given locale, from a week on", () => {
		const at = NOW - 8 * 24 * 60 * MIN;
		expect(sharedRelativeAge(at, "de-DE", { now: NOW })).toBe(
			new Date(at).toLocaleDateString("de-DE")
		);
	});
});
