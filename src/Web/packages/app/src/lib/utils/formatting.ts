/**
 * Centralized Formatting Utilities
 *
 * Consolidates all formatting functions for the application:
 * - Glucose formatting (with unit conversion)
 * - Treatment formatting
 * - Date formatting
 * - Insulin/carb/percentage display formatting
 */

import {
  glucoseUnits,
  carbRatioFormat,
  timeFormat,
  regionFormat,
  preferredLanguage,
  type CarbRatioFormat,
  type GlucoseUnits,
} from "$lib/stores/appearance-store.svelte";
import {
  relativeAge as sharedRelativeAge,
  relativeTime as sharedRelativeTime,
  type RelativeTimeOptions,
} from "@nocturne/ui/relative-time";
import type { OverallAverages, Treatment, TreatmentSummary } from "$api-clients";

// Re-export for backward compatibility
export type { GlucoseUnits, OverallAverages, Treatment, TreatmentSummary };

// =============================================================================
// Glucose Conversion & Formatting
// =============================================================================

// Pure unit conversion/formatting lives in the shared design system (@nocturne/ui/glucose) so the
// web app and the desktop companion render glucose identically. Re-exported here so existing
// import sites (`$lib/utils/formatting`) keep working unchanged.
export {
  convertToDisplayUnits,
  convertFromDisplayUnits,
  formatGlucoseValue,
  formatGlucoseDelta,
  getUnitLabel,
  isGlucoseUnits,
} from "@nocturne/ui/glucose";
export { toDate } from "@nocturne/ui/relative-time";
import {
  convertToDisplayUnits,
  formatGlucoseValue,
  formatGlucoseDelta,
  getUnitLabel,
} from "@nocturne/ui/glucose";

/**
 * Format a glucose range for display
 * @param lowMgdl - Low threshold in mg/dL
 * @param highMgdl - High threshold in mg/dL
 * @param units - Display units
 * @returns Formatted range string (e.g., "70-180 mg/dL" or "3.9-10.0 mmol/L")
 */
export function formatGlucoseRange(
  lowMgdl: number,
  highMgdl: number,
  units: GlucoseUnits
): string {
  const low = formatGlucoseValue(lowMgdl, units);
  const high = formatGlucoseValue(highMgdl, units);
  const label = getUnitLabel(units);
  return `${low}-${high} ${label}`;
}

// =============================================================================
// Glucose Convenience Functions (auto-detect units from global preference)
// These are the recommended functions for most use cases.
// =============================================================================

/**
 * Format a glucose value using the global unit preference
 * @param mgdl - Glucose value in mg/dL
 * @param units - A surface that carries its own units (a clock face) passes them here
 * @returns Formatted glucose string in user's preferred units
 */
export function bg(mgdl: number, units: GlucoseUnits = glucoseUnits.current) {
  return formatGlucoseValue(mgdl, units);
}

/**
 * Format a glucose delta using the global unit preference
 * @param deltaMgdl - Delta value in mg/dL
 * @param includeSign - Whether to include +/- sign (default: true)
 * @param units - As for {@link bg}
 * @returns Formatted delta string in user's preferred units
 */
export function bgDelta(
  deltaMgdl: number,
  includeSign: boolean = true,
  units: GlucoseUnits = glucoseUnits.current
): string {
  return formatGlucoseDelta(deltaMgdl, units, includeSign);
}

/**
 * Get the current unit label from global preference
 * @param units - As for {@link bg}
 * @returns "mg/dL" or "mmol/L" based on user preference
 */
export function bgLabel(units: GlucoseUnits = glucoseUnits.current): string {
  return getUnitLabel(units);
}

/**
 * Format a glucose range using the global unit preference
 * @param lowMgdl - Low threshold in mg/dL
 * @param highMgdl - High threshold in mg/dL
 * @returns Formatted range string in user's preferred units
 */
export function bgRange(lowMgdl: number, highMgdl: number): string {
  return formatGlucoseRange(lowMgdl, highMgdl, glucoseUnits.current);
}

/**
 * Convert a mg/dL value to the user's preferred units
 * @param mgdl - Value in mg/dL
 * @returns Numeric value in user's preferred units
 */
export function bgValue(mgdl: number): number {
  return convertToDisplayUnits(mgdl, glucoseUnits.current);
}

/**
 * Get standard glucose range thresholds in user's preferred units
 * @returns Object with common threshold values
 */
export function bgThresholds(): {
  urgentLow: number;
  low: number;
  targetLow: number;
  targetHigh: number;
  high: number;
  urgentHigh: number;
} {
  const units = glucoseUnits.current;
  return {
    urgentLow: convertToDisplayUnits(54, units),
    low: convertToDisplayUnits(70, units),
    targetLow: convertToDisplayUnits(70, units),
    targetHigh: convertToDisplayUnits(180, units),
    high: convertToDisplayUnits(180, units),
    urgentHigh: convertToDisplayUnits(250, units),
  };
}

// =============================================================================
// Time Formatting (auto-detect format from global preference)
// =============================================================================

/**
 * Parse a schedule time string ("HH:mm" or "HH:mm:ss") into seconds from midnight.
 * Missing or malformed components fall back to 0.
 */
export function timeStringToSeconds(time: string | undefined): number {
  if (!time) return 0;
  const [h = 0, m = 0, s = 0] = time.split(":").map((p) => parseInt(p, 10) || 0);
  return h * 3600 + m * 60 + s;
}

/**
 * The locale every Intl call in the app should format with: the regional-format
 * preference when the user has picked one, otherwise their display language.
 *
 * Keeping these separate is what lets someone read an English interface on a
 * European calendar — "en-GB" gives DD/MM/YYYY and Monday-first weeks without
 * translating the UI.
 */
export function formatLocale(): string {
  return regionFormat.current || preferredLanguage.current;
}

/** Whether times render in 12-hour form under the viewer's time-format preference. */
export function prefersHour12(): boolean {
  return timeFormat.current !== "24";
}

/**
 * Format a time using the global time format and language preferences
 * @param date - Date object or Unix milliseconds
 * @param opts.compact - If true, use numeric minutes in 12h mode
 * @param opts.seconds - If true, append 2-digit seconds
 * @returns Formatted time string (e.g. "2:30 pm" or "14:30")
 */
export function time(
  date: Date | string | number | null | undefined,
  opts: { compact?: boolean; seconds?: boolean } = {}
): string {
  // Coerced for the reason {@link toDate} gives.
  if (date == null) return "—";
  const d = date instanceof Date ? date : new Date(date);
  if (Number.isNaN(d.getTime())) return "—";
  const options: Intl.DateTimeFormatOptions = {
    hour: "numeric",
    minute: opts.compact && prefersHour12() ? "numeric" : "2-digit",
    hour12: prefersHour12(),
  };
  if (opts.seconds) options.second = "2-digit";
  return d.toLocaleTimeString(formatLocale(), options);
}

/**
 * Format an hour-granularity label using the global time format and language
 * preferences. Chart time axes tick on the hour, where `time`'s minutes are
 * noise; layerchart's own `format="hour"` emits no hour12 token and so falls
 * back to the browser locale, ignoring the preference entirely.
 * @param date - Date object or Unix milliseconds
 * @returns Formatted hour string (e.g. "2 pm", or "14" — 24-hour output is
 * zero-padded and may carry a locale suffix, e.g. "09" or "09 Uhr")
 */
export function hourLabel(date: Date | number): string {
  const d = typeof date === "number" ? new Date(date) : date;
  return d.toLocaleTimeString(formatLocale(), {
    hour: "numeric",
    hour12: prefersHour12(),
  });
}

/**
 * How long ago a moment was, in the largest whole unit: "now", "5m ago", "3h ago",
 * "2d ago", then the absolute date from a week on. The words are the regional
 * format's own. A moment in the future reads as "now".
 *
 * Returns `fallback` when `at` is missing or not a date. The caller owns that
 * wording, so it is extracted for translation with the rest of its component.
 *
 * @param opts.now Reference time. Pass a ticking value (see `Now`) where the
 *   result is rendered, or the text freezes at the age it had on first render.
 */
export function relativeAge(
  at: Date | string | number | null | undefined,
  opts: RelativeTimeOptions = {}
): string {
  return sharedRelativeAge(at, formatLocale(), opts);
}

/**
 * How far a moment is from `now`, in either direction: "3h ago" or "in 3h". A
 * moment behind reads in the largest whole unit, as in `relativeAge`. A moment
 * ahead reads in the nearest whole unit, so 47 hours is "in 2d", never "tomorrow".
 */
export function relativeTime(
  at: Date | string | number | null | undefined,
  opts: RelativeTimeOptions = {}
): string {
  return sharedRelativeTime(at, formatLocale(), opts);
}

// =============================================================================
// Date Formatting
// =============================================================================

/**
 * A number with the regional format's own group and decimal separators — "1.234"
 * for a German reader, "1,234" for an American one.
 */
export function formatNumber(value: number | undefined | null): string {
  return (value ?? 0).toLocaleString(formatLocale());
}

/** A byte count in the largest unit that keeps it above 1, to one decimal place: "1.5 GB". */
export function formatFileSize(bytes: number): string {
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  const digits = unit === 0 ? 0 : 1;
  return `${value.toLocaleString(formatLocale(), { minimumFractionDigits: digits, maximumFractionDigits: digits })} ${units[unit]}`;
}

/**
 * The regional format's own numeric date, e.g. "31/12/2026" or "12/31/2026". The
 * shape is the locale's; use {@link formatShortDate} or {@link formatMediumDate}
 * where a named month reads better.
 */
export function formatNumericDate(date: Date | string | number): string {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleDateString(formatLocale());
}

/**
 * Formats a date string to display date and time
 * @param dateStr - ISO date string or undefined
 * @returns Formatted date and time string, or fallback
 */
export function formatDateTime(dateStr: string | undefined): string {
  if (!dateStr) return "—";
  const date = new Date(dateStr);
  if (Number.isNaN(date.getTime())) return "—";
  return formatNumericDate(date) + " " + time(date);
}

/**
 * Formats a date string with detailed formatting options
 * @param dateString - ISO date string or undefined
 * @returns Formatted date and time with full details, or "Unknown"
 */
export function formatDateDetailed(dateString: string | undefined): string {
  if (!dateString) return "Unknown";
  try {
    return new Date(dateString).toLocaleDateString(formatLocale(), {
      year: "numeric",
      month: "long",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
      hour12: prefersHour12(),
    });
  } catch {
    return dateString;
  }
}

/**
 * Date with its weekday and no time, e.g. "Tue, 28 Jul" — ordering and names
 * follow the regional format.
 */
export function formatWeekdayDate(date: Date | string | number): string {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleDateString(formatLocale(), {
    weekday: "short",
    month: "short",
    day: "numeric",
  });
}

/**
 * Short date with no time, e.g. "28 Jul" or "28 Jul 2026" with `withYear`.
 */
export function formatShortDate(
  date: Date | string | number,
  withYear = false
): string {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleDateString(formatLocale(), {
    month: "short",
    day: "numeric",
    ...(withYear ? { year: "numeric" as const } : {}),
  });
}

/**
 * Date with its weekday, month name and year, e.g. "Saturday, 29 August 2026" —
 * names and ordering follow the regional format.
 */
export function formatLongDate(date: Date | string | number): string {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleDateString(formatLocale(), {
    weekday: "long",
    year: "numeric",
    month: "long",
    day: "numeric",
  });
}

/** Date with an abbreviated month and its year, e.g. "29 Aug 2026". */
export function formatMediumDate(date: Date | string | number): string {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleDateString(formatLocale(), {
    year: "numeric",
    month: "short",
    day: "numeric",
  });
}

/**
 * A date range at {@link formatMediumDate}'s precision, e.g. "29 Aug - 3 Sep 2026".
 *
 * Built by ICU rather than by joining two formatted dates with a hyphen, so the parts the two
 * share are collapsed and the separator is the one the region writes.
 */
export function formatMediumDateRange(start: Date, end: Date): string {
  return new Intl.DateTimeFormat(formatLocale(), {
    year: "numeric",
    month: "short",
    day: "numeric",
  }).formatRange(start, end);
}

/**
 * {@link formatMediumDate} with the time of day, e.g. "29 Aug 2026, 02:30".
 * Follows the locale's hour cycle — see {@link formatDayTime}.
 */
export function formatMediumDateTime(
  date: Date | string | number | null | undefined
): string {
  if (date == null) return "—";
  const d = date instanceof Date ? date : new Date(date);
  if (Number.isNaN(d.getTime())) return "—";
  return d.toLocaleString(formatLocale(), {
    year: "numeric",
    month: "short",
    day: "numeric",
    ...localeHourFields(),
  });
}

/**
 * The hour fields the locale's own short time uses — zero-padded and 24-hour where
 * it writes them that way, `2:05 pm` where it doesn't.
 *
 * `timeStyle` cannot be combined with date fields, and formatting the halves
 * separately would hardcode a joiner that ja-JP, zh-CN and ko-KR write as a space
 * and ar-EG as U+060C — so the fields are read off a `timeStyle: "short"` probe and
 * spread into a single `Intl` call, which lets ICU supply the glue.
 */
const localeHourFields = (() => {
  // 05:05, so a padded locale renders "05" and an unpadded one "5".
  const probe = new Date(Date.UTC(2020, 0, 1, 5, 5));
  const cache = new Map<string, Intl.DateTimeFormatOptions>();
  return (): Intl.DateTimeFormatOptions => {
    const locale = formatLocale();
    const cached = cache.get(locale);
    if (cached) return cached;

    const formatter = new Intl.DateTimeFormat(locale, {
      timeStyle: "short",
      timeZone: "UTC",
    });
    const hour = formatter.formatToParts(probe).find((part) => part.type === "hour");
    const fields: Intl.DateTimeFormatOptions = {
      hour: (hour?.value.length ?? 1) > 1 ? "2-digit" : "numeric",
      minute: "2-digit",
    };
    cache.set(locale, fields);
    return fields;
  };
})();

/**
 * Time of day at the locale's own convention. Deliberately not the 12/24
 * preference — see {@link formatDayTime}.
 */
export function formatClock(
  date: Date | string | number | null | undefined,
  opts: { seconds?: boolean } = {}
): string {
  if (date == null) return "—";
  const d = date instanceof Date ? date : new Date(date);
  if (Number.isNaN(d.getTime())) return "—";
  return d.toLocaleTimeString(formatLocale(), {
    timeStyle: opts.seconds ? "medium" : "short",
  });
}

/**
 * Day and time with no year, e.g. "30 Aug, 00:05".
 *
 * Follows the locale's hour cycle, not the 12/24 preference; sibling surfaces
 * ({@link formatDateTimeCompact}) follow the preference. Unifying them needs a
 * "follow the locale" value in the preference itself.
 */
export function formatDayTime(date: Date | string | number | undefined): string {
  if (date == null) return "—";
  const d = date instanceof Date ? date : new Date(date);
  if (Number.isNaN(d.getTime())) return "—";
  return d.toLocaleString(formatLocale(), {
    month: "short",
    day: "numeric",
    ...localeHourFields(),
  });
}

/** Month name and year with no day, e.g. "August 2026". */
export function formatMonthYear(date: Date | string | number): string {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleDateString(formatLocale(), {
    month: "long",
    year: "numeric",
  });
}

/** Abbreviated month name alone, for a chart's month band. */
export function formatMonthLabel(date: Date | string | number): string {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleDateString(formatLocale(), {
    month: "short",
  });
}

/** Abbreviated weekday name alone, for a chart axis or a row label. */
export function formatWeekdayLabel(date: Date | string | number): string {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleDateString(formatLocale(), {
    weekday: "short",
  });
}

/** A weekday's short name in the viewer's locale. 4 January 2026 is a Sunday, so day d falls on the 4th + d. */
export function weekdayLabel(day: number): string {
  return formatWeekdayLabel(new Date(2026, 0, 4 + day));
}

/**
 * Formats a date string for use in datetime-local input fields
 * @param dateStr - ISO date string or undefined
 * @returns Date in YYYY-MM-DDTHH:MM format for HTML input
 */
export function formatDateForInput(dateStr: string | undefined): string {
  if (!dateStr) return "";
  const date = new Date(dateStr);
  if (Number.isNaN(date.getTime())) return "";
  // Local getters, deliberately: a `datetime-local` value carries no offset, so
  // the browser parses it back in its own zone. Rendering it on any other
  // calendar makes the round trip asymmetric and moves the instant on save.
  const year = date.getFullYear();
  const month = (date.getMonth() + 1).toString().padStart(2, "0");
  const day = date.getDate().toString().padStart(2, "0");
  const hours = date.getHours().toString().padStart(2, "0");
  const minutes = date.getMinutes().toString().padStart(2, "0");
  return `${year}-${month}-${day}T${hours}:${minutes}`;
}

/**
 * Formats a date string to compact date and time (short month)
 * @param dateStr - ISO date string or undefined
 * @returns Compact formatted date and time, or "—"
 */
export function formatDateTimeCompact(date: Date | string | number | undefined): string {
  if (date == null) return "—";
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleDateString(formatLocale(), {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    hour12: prefersHour12(),
  });
}

// =============================================================================
// Treatment & Insulin Formatting
// =============================================================================

/**
 * Formats an insulin value for display.
 * @param insulin The insulin value.
 * @returns The formatted insulin string.
 */
export function formatInsulinDisplay(insulin: number | null | undefined): string {
  if (insulin === undefined || insulin === null) {
    return "N/A";
  }
  return insulin.toFixed(2);
}

/**
 * Formats a carb value for display.
 * @param carbs The carb value.
 * @returns The formatted carb string.
 */
export function formatCarbDisplay(carbs: number | undefined): string {
  if (carbs === undefined || carbs === null) {
    return "N/A";
  }
  return carbs.toFixed(0);
}

export function formatInsulinToCarbRatio(
  gramsPerUnit: number | null | undefined,
  format: CarbRatioFormat = carbRatioFormat.current
): string {
  if (gramsPerUnit == null || !(gramsPerUnit > 0)) return "–";
  switch (format) {
    case "g/U":
      return `${gramsPerUnit.toFixed(1)} g/U`;
    case "U/10g":
      return `${unitsPerTenGrams(gramsPerUnit).toFixed(2)} U/10 g`;
    default:
      return `1:${gramsPerUnit.toFixed(1)}`;
  }
}

export function unitsPerTenGrams(gramsPerUnit: number): number {
  return 10 / gramsPerUnit;
}

/**
 * Kept out of the templates: the catalogue would translate the format's literal there, and the
 * comparison would then fail in other locales.
 */
export function writesUnitsPerTenGrams(format: CarbRatioFormat = carbRatioFormat.current): boolean {
  return format === "U/10g";
}

/**
 * Formats a percentage value for display.
 * @param value The percentage value.
 * @returns The formatted percentage string.
 */
export function formatPercentageDisplay(value: number | undefined): string {
  if (value === undefined || value === null) {
    return "N/A";
  }
  return value.toFixed(1);
}

/**
 * Formats glucose reading with measurement method
 * @param treatment - Treatment object
 * @returns Formatted glucose string
 */
export function formatGlucose(treatment: Treatment): string {
  if (treatment.glucose && treatment.glucose > 0) {
    let glucoseStr = treatment.glucose.toString();
    if (treatment.glucoseType) {
      glucoseStr += ` (${treatment.glucoseType})`;
    }
    return glucoseStr;
  }
  return "-";
}

/**
 * Formats event type with optional reason
 * @param treatment - Treatment object
 * @returns Formatted event type string
 */
export function formatEventType(treatment: Treatment): string {
  let result = treatment.eventType || "Unknown";

  if (treatment.reason) {
    result += ` - ${treatment.reason}`;
  }

  return result;
}

/**
 * Formats notes and entered by information
 * @param treatment - Treatment object
 * @returns Formatted notes string
 */
export function formatNotes(treatment: Treatment): string {
  const parts: string[] = [];

  if (treatment.notes) {
    parts.push(treatment.notes);
  }

  if (treatment.enteredBy) {
    parts.push(`by ${treatment.enteredBy}`);
  }

  return parts.join(" ");
}
