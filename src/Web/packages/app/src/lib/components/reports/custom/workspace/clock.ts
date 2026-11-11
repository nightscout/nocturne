import {
  chartFormat,
  formatMediumDateRange,
  time,
} from "#lib/utils/formatting.js";

/**
 * A minute of the day, 0 to 1440, in the member's clock format; 1440 is the end
 * of the day.
 */
export function minuteLabel(minute: number): string {
  if (minute >= 1440)
    return chartFormat().hour12 ? time(new Date(2000, 0, 2, 0, 0)) : "24:00";
  return time(new Date(2000, 0, 1, Math.floor(minute / 60), minute % 60));
}

/**
 * A window of the day as words: "06:00 to 08:30". An end of 0 after a later
 * start is midnight.
 */
export function windowLabel(from: number, to: number): string {
  return `${minuteLabel(from)} to ${minuteLabel(to === 0 && from > 0 ? 1440 : to)}`;
}

/** Two local dates, yyyy-MM-dd, in the app's medium date format. */
export function spanLabel(span: { from: string; to: string }): string {
  return formatMediumDateRange(
    new Date(`${span.from}T12:00:00`),
    new Date(`${span.to}T12:00:00`)
  );
}
