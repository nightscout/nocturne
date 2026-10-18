/**
 * Time rendering for clock faces: the single place the 12h/24h decision is made
 * for the builder preview, the live renderer and the public clock link. The
 * face's own `timeFormat` decides, never the viewer's preference, so a face
 * reads the same to everyone who opens it.
 */

import { formatLocale } from "$lib/utils/formatting";

/**
 * Render the time for a clock face. Zero-padded hours because a clock face is
 * read at a glance from across a room and a jumping digit is harder to track.
 */
export function formatClockTime(
  date: Date,
  timeFormat: string | undefined
): string {
  return date.toLocaleTimeString(formatLocale(), {
    hour: "2-digit",
    minute: "2-digit",
    hour12: timeFormat !== "24",
  });
}
