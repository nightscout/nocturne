/**
 * Time rendering for clock faces — the single place the 12h/24h decision is made
 * for the builder preview, the live renderer and the public clock link, so a face
 * reads the same on all three.
 */

import { formatLocale, prefersHour12 } from "$lib/utils/formatting";

/**
 * A time element's format. "auto" follows the viewer's time-format preference;
 * "12h"/"24h" pin the element to one format regardless of it.
 */
export type ClockTimeFormat = "auto" | "12h" | "24h";

/** Default for a newly added time element, and for a face saved before "auto" existed. */
export const DEFAULT_CLOCK_TIME_FORMAT: ClockTimeFormat = "auto";

/**
 * Resolve a stored `format` value to the 12-hour flag to render with.
 * Anything other than an explicit "12h"/"24h" pin defers to the preference.
 */
function hour12For(format: string | undefined | null): boolean {
  if (format === "12h") return true;
  if (format === "24h") return false;
  return prefersHour12();
}

/**
 * Render the time for a clock element. Zero-padded hours because a clock face is
 * read at a glance from across a room and a jumping digit is harder to track.
 */
export function formatClockTime(
  date: Date,
  format: string | undefined | null
): string {
  return date.toLocaleTimeString(formatLocale(), {
    hour: "2-digit",
    minute: "2-digit",
    hour12: hour12For(format),
  });
}
