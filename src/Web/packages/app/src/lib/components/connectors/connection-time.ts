import { formatShortDate, relativeAge, toDate } from "#lib/utils/formatting.js";

const MINUTE_MS = 60 * 1000;
const DAY_MS = 24 * 60 * MINUTE_MS;

/** Whether a moment is under a minute old, which `ConnectionTime` words itself. */
export function isJustNow(
  at: Date | string | number | null | undefined,
  now: number = Date.now()
): boolean {
  const date = toDate(at);
  return date !== null && now - date.getTime() < MINUTE_MS;
}

/**
 * The one date style of the connections page: relative under a day ("3h ago"), then the short
 * date, with its year once that is not this year. Null when `at` is missing or not a date.
 */
export function formatConnectionTime(
  at: Date | string | number | null | undefined,
  now: number = Date.now()
): string | null {
  const date = toDate(at);
  if (!date) return null;
  if (now - date.getTime() < DAY_MS) return relativeAge(date, { now });
  return formatShortDate(date, date.getFullYear() !== new Date(now).getFullYear());
}
