/**
 * Shared date/time formatting helpers for alerts UI.
 *
 * Generic time/date-time formatting lives in "#lib/utils/formatting.js" and
 * durations in "#lib/utils/duration.js"; only alert-specific formatters remain here.
 */

import { formatDateTimeCompact } from "#lib/utils/formatting.js";

/** "Mar 5, 14:32 — Mar 5, 15:00" — compact date-time range. Empty when either side missing. */
export function formatRange(
  start: Date | string | undefined,
  end: Date | string | undefined
): string {
  if (!start || !end) return "";
  return `${formatDateTimeCompact(start)} — ${formatDateTimeCompact(end)}`;
}
