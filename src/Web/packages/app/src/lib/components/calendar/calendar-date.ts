import {
  CalendarDate,
  getLocalTimeZone,
  startOfWeek,
} from "@internationalized/date";

export function parseCalendarDate(date: string): Date {
  const [year, month, day] = date.split("-").map(Number);

  return new Date(year, month - 1, day);
}

export function getCalendarDayNumber(date: string): number {
  return parseCalendarDate(date).getDate();
}

export function formatCalendarDate(
  date: string,
  locales: Intl.LocalesArgument,
  options: Intl.DateTimeFormatOptions
): string {
  return parseCalendarDate(date).toLocaleDateString(locales, options);
}

/** 2026-01-04 is a Sunday — the anchor the helpers below walk a week from. */
const A_SUNDAY = new CalendarDate(2026, 1, 4);

/** `offset` days after the anchor Sunday, as a plain Date for Intl formatting. */
function daysAfterAnchorSunday(offset: number): Date {
  return new Date(2026, 0, 4 + offset);
}

/**
 * The day a week starts on for a locale, as a JS day number (0 = Sunday).
 * Read from the same table the date pickers use, so the month grid and the
 * pickers can never disagree about where a week begins.
 */
export function firstDayOfWeek(locale: string): number {
  return startOfWeek(A_SUNDAY, locale).toDate(getLocalTimeZone()).getDay();
}

/**
 * Weekday names for a locale, ordered from its first day of the week.
 *
 * `maxLength` clips each name. Some locales have no genuinely abbreviated
 * weekday form — pt-PT's "short" names are the full words ("domingo") — which
 * would blow out a fixed seven-column header. Omit it where width is free.
 */
export function weekdayLabels(
  locale: string,
  weekday: Intl.DateTimeFormatOptions["weekday"] = "short",
  maxLength?: number
): string[] {
  const start = firstDayOfWeek(locale);
  const format = new Intl.DateTimeFormat(locale, { weekday });
  return Array.from({ length: 7 }, (_, i) => {
    const label = format.format(daysAfterAnchorSunday(start + i));
    return maxLength === undefined ? label : label.slice(0, maxLength);
  });
}

/**
 * Name of the day `locale`'s weeks start on, spelled in `nameLocale`. The two differ
 * where a setting describes one region to a reader of another language — "Germany …
 * weeks start Monday" rather than "… Montag".
 */
export function weekStartName(locale: string, nameLocale: string): string {
  return new Intl.DateTimeFormat(nameLocale, { weekday: "long" }).format(
    daysAfterAnchorSunday(firstDayOfWeek(locale))
  );
}

/**
 * How many blank cells precede the 1st of a month in a grid whose columns start
 * at the locale's first day of the week.
 */
export function leadingBlankDays(
  year: number,
  month: number,
  locale: string
): number {
  const firstOfMonth = new Date(year, month, 1).getDay();
  return (firstOfMonth - firstDayOfWeek(locale) + 7) % 7;
}
