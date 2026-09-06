/**
 * Primitives for "the selected date range" — the single definition shared by the
 * reports area, the filter sidebar and the report remote functions.
 *
 * A range is two `YYYY-MM-DD` day strings and is **inclusive of both days**.
 * Resolving it to instants anchors on midnight in a calendar timezone (the
 * viewer's local zone by default, the patient's configured zone on the server),
 * never UTC midnight, and the end bound is the last millisecond of the last day.
 */
import { getLocalTimeZone, parseDate, today } from "@internationalized/date";

const MS_PER_DAY = 86_400_000;

export type DayRangeStrings = { from: string; to: string };

/** The URL-shaped range: explicit `from`/`to`, or a relative `days` window. */
export type DayRangeInput = {
  days?: number | null;
  from?: string | null;
  to?: string | null;
};

/**
 * `YYYY-MM-DD` for a Date read in the local calendar, not the UTC calendar.
 *
 * This is also the right key for grouping rows by day: a locale-formatted date
 * groups by whatever shape the region format produces, so changing the format
 * mid-session re-buckets every group, and two days that format alike merge.
 */
export function toDayString(date: Date = new Date()): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

/**
 * Day part of a day string; a longer ISO string keeps only its date.
 *
 * {@link isDayString} admits a longer ISO string by day-parting it internally but
 * returns it whole, so anything handing its result to a bare `parseDate` — a range
 * picker, a date arrow — has to day-part it first or `parseDate` throws.
 */
export function dayPart(value: string): string {
  return value.length > 10 ? value.slice(0, 10) : value;
}

function toDay(value: string | Date): string {
  return typeof value === "string" ? dayPart(value) : toDayString(value);
}

/** Whether `value` names a day this module can resolve to instants. */
export function isDayString(value: string | null | undefined): value is string {
  if (!value) return false;
  try {
    parseDate(dayPart(value));
    return true;
  } catch {
    return false;
  }
}

/**
 * The effective range: explicit `from`/`to` win, otherwise the last `days`
 * (or `defaultDays`) calendar days ending today in `timeZone`. A `from`/`to` pair
 * that isn't a resolvable day — a hand-edited URL, say — falls through to the
 * relative window rather than throwing.
 */
export function resolveDayRange(
  input: DayRangeInput | null | undefined,
  defaultDays: number,
  timeZone: string = getLocalTimeZone()
): DayRangeStrings {
  if (isDayString(input?.from) && isDayString(input?.to)) {
    return { from: dayPart(input.from), to: dayPart(input.to) };
  }
  const days = input?.days ?? defaultDays;
  const zone = isTimeZone(timeZone) ? timeZone : "UTC";
  const end = today(zone);
  const start = end.subtract({ days: Math.max(1, days) - 1 });
  return { from: start.toString(), to: end.toString() };
}

/** Whether `timeZone` is an IANA zone this runtime knows. */
export function isTimeZone(timeZone: string | null | undefined): timeZone is string {
  if (!timeZone) return false;
  try {
    new Intl.DateTimeFormat("en-CA", { timeZone });
    return true;
  } catch {
    return false;
  }
}

/** Midnight opening `day` in `timeZone`. */
export function startOfDay(day: string, timeZone: string = getLocalTimeZone()): Date {
  return parseDate(dayPart(day)).toDate(timeZone);
}

/**
 * Last millisecond of `day` in `timeZone`. Derived from the next day's midnight
 * so a day shortened or lengthened by a DST transition still ends correctly.
 */
export function endOfDay(day: string, timeZone: string = getLocalTimeZone()): Date {
  return new Date(
    parseDate(dayPart(day)).add({ days: 1 }).toDate(timeZone).getTime() - 1
  );
}

/**
 * Calendar days covered by an inclusive range: a Monday-to-Sunday range is 7,
 * and a single day is 1. Counted on the calendar, so DST transitions and
 * month/year boundaries do not shift it.
 */
export function dayCount(from: string | Date, to: string | Date): number {
  const start = parseDate(toDay(from)).toDate("UTC").getTime();
  const end = parseDate(toDay(to)).toDate("UTC").getTime();
  return Math.max(1, Math.round((end - start) / MS_PER_DAY) + 1);
}
