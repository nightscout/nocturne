/**
 * Shared date/time formatting helpers for alerts UI.
 *
 * All helpers accept Date | string | undefined and return "" on missing
 * or unparseable input (call sites supply their own dash fallback when
 * they want one). Generic time/date-time formatting lives in
 * "$lib/utils/formatting" and durations in "$lib/utils/duration"; only
 * alert-specific relative formatters remain here.
 */

import { formatDateTimeCompact, toDate } from "$lib/utils/formatting";

/** "Mar 5, 14:32 — Mar 5, 15:00" — compact date-time range. Empty when either side missing. */
export function formatRange(
  start: Date | string | undefined,
  end: Date | string | undefined
): string {
  if (!start || !end) return "";
  return `${formatDateTimeCompact(start)} — ${formatDateTimeCompact(end)}`;
}

/**
 * Relative: "Just now", "12m ago", "3h 5m ago", "2d ago".
 *
 * `now` defaults to the current time; pass a reactive value to let a
 * long-lived card age without being re-created (e.g. FiringToast).
 */
export function formatTimeSince(
  at: Date | string | undefined,
  now: number = Date.now()
): string {
  const d = toDate(at);
  if (!d) return "Unknown";
  const diffMs = now - d.getTime();
  const diffMin = Math.floor(diffMs / 60000);
  if (diffMin < 1) return "Just now";
  if (diffMin < 60) return `${diffMin}m ago`;
  const diffHr = Math.floor(diffMin / 60);
  if (diffHr < 24) return `${diffHr}h ${diffMin % 60}m ago`;
  return `${Math.floor(diffHr / 24)}d ago`;
}
