import { fromAbsolute, getLocalTimeZone, parseDate, toCalendarDate } from "@internationalized/date";
import { endOfDay, startOfDay } from "#lib/utils/date-range.js";

/**
 * The instant a membership ends for the last day the person picked: the start of the next day, so
 * access holds through the whole picked day. Whether it has ended is the API's `hasEnded`.
 */
export function accessEndInstant(day: string, timeZone: string = getLocalTimeZone()): string {
  return new Date(endOfDay(day, timeZone).getTime() + 1).toISOString();
}

/** The `YYYY-MM-DD` day access still holds on, the inverse of {@link accessEndInstant}. */
export function accessEndDay(instant: string, timeZone: string = getLocalTimeZone()): string {
  return toCalendarDate(fromAbsolute(Date.parse(instant) - 1, timeZone)).toString();
}

/** Local midnight of {@link accessEndDay}, for the date formatters. */
export function accessEndDate(instant: string, timeZone: string = getLocalTimeZone()): Date {
  return startOfDay(accessEndDay(instant, timeZone), timeZone);
}

/** What limits the days that can be picked: the clock, and the caller's own end if it has one. */
export interface AccessEndBounds {
  /** Unix milliseconds from a clock that moves. */
  now: number;
  /** When the caller's own access ends. */
  ceiling?: string;
  timeZone?: string;
}

/** The earliest day an end can be picked: the day `now` falls in. */
export function earliestAccessEndDay(now: number, timeZone: string = getLocalTimeZone()): string {
  return toCalendarDate(fromAbsolute(now, timeZone)).toString();
}

/**
 * The latest day whose end does not outlast `ceiling`, the caller's own end, or undefined when the
 * caller's access does not end.
 */
export function latestAccessEndDay(
  ceiling: string | undefined,
  timeZone: string = getLocalTimeZone()
): string | undefined {
  if (!ceiling) return undefined;
  const day = accessEndDay(ceiling, timeZone);
  return Date.parse(accessEndInstant(day, timeZone)) <= Date.parse(ceiling)
    ? day
    : parseDate(day).subtract({ days: 1 }).toString();
}

/** Whether a picked day is one the API will accept: not before today, not past the ceiling. */
export function isAccessEndDayOffered(day: string, { now, ceiling, timeZone }: AccessEndBounds): boolean {
  const latest = latestAccessEndDay(ceiling, timeZone);
  return day >= earliestAccessEndDay(now, timeZone) && (latest === undefined || day <= latest);
}

/** Whether any day can be picked. None can on the last day of a caller whose end is not at midnight. */
export function hasOfferedAccessEndDay({ now, ceiling, timeZone }: AccessEndBounds): boolean {
  const latest = latestAccessEndDay(ceiling, timeZone);
  return latest === undefined || latest >= earliestAccessEndDay(now, timeZone);
}

/**
 * Whether the field's value can be sent. An empty value is valid unless `mustKeep`: the member has
 * an end and the caller's own end means it cannot be removed.
 */
export function isAccessEndChoiceValid(day: string, bounds: AccessEndBounds, mustKeep = false): boolean {
  return day === "" ? !(bounds.ceiling && mustKeep) : isAccessEndDayOffered(day, bounds);
}
