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
  timeFormat,
  regionFormat,
  preferredLanguage,
  type GlucoseUnits,
} from "$lib/stores/appearance-store.svelte";
import type { Treatment } from "$lib/api";

// Re-export for backward compatibility
export type { GlucoseUnits, Treatment };

// Local type definitions for treatment summaries
export interface TreatmentSummary {
  [key: string]: any;
}

export interface OverallAverages {
  [key: string]: any;
}

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
} from "@nocturne/ui/glucose";
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
 * @returns Formatted glucose string in user's preferred units
 */
export function bg(mgdl: number) {
  return formatGlucoseValue(mgdl, glucoseUnits.current);
}

/**
 * Format a glucose delta using the global unit preference
 * @param deltaMgdl - Delta value in mg/dL
 * @param includeSign - Whether to include +/- sign (default: true)
 * @returns Formatted delta string in user's preferred units
 */
export function bgDelta(deltaMgdl: number, includeSign: boolean = true): string {
  return formatGlucoseDelta(deltaMgdl, glucoseUnits.current, includeSign);
}

/**
 * Get the current unit label from global preference
 * @returns "mg/dL" or "mmol/L" based on user preference
 */
export function bgLabel(): string {
  return getUnitLabel(glucoseUnits.current);
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

/**
 * Whether times render in 12-hour form. `override` pins the answer for a surface
 * that carries its own format (a clock face element), ignoring the preference.
 */
export function prefersHour12(override?: boolean): boolean {
  return override ?? timeFormat.current !== "24";
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
  // Coerced like every sibling helper: NSwag types DTO date fields as `Date` but
  // the client parses with no reviver, so what arrives is an ISO string.
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
 * Coarse relative age for a "last seen" or "last synced" field: "Just now",
 * "12m ago", "3h ago", "2d ago", then an absolute date past a week.
 *
 * This is the short form. `formatTimeSince` in the alerts folder is minute-
 * precise ("3h 5m ago") because an alert card is read while it is firing; use
 * this one everywhere else so the same age doesn't render two ways on one row.
 *
 * @param now Reference time. Pass a ticking value (see `Now`) where the result
 *   is rendered, or the text freezes at the age it had on first render.
 */
export function lastSeen(
  date: Date | string | undefined | null,
  now: number = Date.now()
): string {
  if (!date) return "Never";
  const d = date instanceof Date ? date : new Date(date);
  if (Number.isNaN(d.getTime())) return "Never";

  const minutes = Math.floor((now - d.getTime()) / 60000);
  if (minutes < 1) return "Just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days < 7) return `${days}d ago`;
  return formatNumericDate(d);
}

/** Format elapsed time as minutes ago in the user's regional format. */
export function minutesAgo(from: number, to: number = Date.now()): string {
  const minutes = Math.max(0, Math.floor((to - from) / 60000));
  return new Intl.RelativeTimeFormat(formatLocale(), {
    numeric: "always",
    style: "short",
  }).format(-minutes, "minute");
}

// =============================================================================
// Date Formatting
// =============================================================================

/**
 * NSwag-generated fields typed `Date` arrive over the wire as ISO strings
 * (the client's jsonParseReviver is a no-op) — coerce defensively, and return
 * null rather than an Invalid Date so callers can take their empty branch
 * instead of feeding NaN to Intl.
 */
export function toDate(value: Date | string | undefined | null): Date | null {
  if (value == null) return null;
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

/**
 * A number with the regional format's own group and decimal separators — "1.234"
 * for a German reader, "1,234" for an American one.
 */
export function formatNumber(value: number | undefined | null): string {
  return (value ?? 0).toLocaleString(formatLocale());
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
  return date.toLocaleDateString(formatLocale()) + " " + time(date);
}

/**
 * Formats a date string or Date object to locale string
 * @param date - Date object, ISO date string, or undefined
 * @returns Formatted date and time string, or "N/A"
 */
export function formatDate(date: Date | string | number | undefined): string {
  if (date == null || date === "") return "N/A";
  return new Date(date).toLocaleString(formatLocale());
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
 * Read off a `timeStyle: "short"` formatter rather than guessed, because the two
 * shapes HEAD used per site (`hour: "2-digit"` and `"numeric"`) each render wrongly
 * in half the locales. `timeStyle` itself cannot be combined with date fields, and
 * formatting the halves separately would hardcode a joiner that ja-JP, zh-CN and
 * ko-KR write as a space and ar-EG as U+060C — so the fields are extracted and
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
      hour12: formatter.resolvedOptions().hour12,
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
 * Follows the locale's hour cycle, not the 12/24 preference — which is what these
 * surfaces did before they were routed through a shared formatter, and changing it
 * is not the locale sweep's business. The app is inconsistent about this: sibling
 * surfaces use {@link formatDateTimeCompact} and do follow the preference. Settling
 * it means giving the preference a "follow the locale" default, which needs the
 * API's `AllowedTimeFormats` widened to accept it and every direct
 * `timeFormat.current === "24"` comparison updated.
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
export function formatInsulinDisplay(insulin: number | undefined): string {
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
