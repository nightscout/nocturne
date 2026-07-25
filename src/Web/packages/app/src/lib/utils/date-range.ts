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

/** `YYYY-MM-DD` for a Date read in the local calendar, not the UTC calendar. */
export function toDayString(date: Date = new Date()): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

/** Day part of a day string; a longer ISO string keeps only its date. */
function dayPart(value: string): string {
  return value.length > 10 ? value.slice(0, 10) : value;
}

function toDay(value: string | Date): string {
  return typeof value === "string" ? dayPart(value) : toDayString(value);
}

/**
 * The effective range: explicit `from`/`to` win, otherwise the last `days`
 * (or `defaultDays`) calendar days ending today in `timeZone`.
 */
export function resolveDayRange(
  input: DayRangeInput | null | undefined,
  defaultDays: number,
  timeZone: string = getLocalTimeZone()
): DayRangeStrings {
  if (input?.from && input?.to) {
    return { from: dayPart(input.from), to: dayPart(input.to) };
  }
  const days = input?.days ?? defaultDays;
  const end = today(timeZone);
  const start = end.subtract({ days: Math.max(1, days) - 1 });
  return { from: start.toString(), to: end.toString() };
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
