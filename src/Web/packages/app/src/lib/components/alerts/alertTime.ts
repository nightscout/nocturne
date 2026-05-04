/**
 * Shared date/time formatting helpers for alerts UI.
 *
 * All helpers accept Date | string | undefined and return "" on missing
 * or unparseable input (call sites supply their own dash fallback when
 * they want one). formatTime / formatDateTime fall back to ISO if the
 * runtime's Intl rejects the options, matching the behaviour the
 * helpers had when they lived inside ReplayPanel.
 */

function toDate(at: Date | string | undefined): Date | null {
  if (!at) return null;
  const d = at instanceof Date ? at : new Date(at);
  if (Number.isNaN(d.getTime())) return null;
  return d;
}

/** "14:32" — locale time. */
export function formatTime(at: Date | string | undefined): string {
  const d = toDate(at);
  if (!d) return "";
  try {
    return new Intl.DateTimeFormat(undefined, {
      hour: "2-digit",
      minute: "2-digit",
    }).format(d);
  } catch {
    return d.toISOString();
  }
}

/** "Mar 5, 14:32" — short date + time. */
export function formatDateTime(at: Date | string | undefined): string {
  const d = toDate(at);
  if (!d) return "";
  try {
    return new Intl.DateTimeFormat(undefined, {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    }).format(d);
  } catch {
    return d.toISOString();
  }
}

/** "Mar 5, 14:32 — Mar 5, 15:00" — formatDateTime range. Empty when either side missing. */
export function formatRange(
  start: Date | string | undefined,
  end: Date | string | undefined
): string {
  if (!start || !end) return "";
  return `${formatDateTime(start)} — ${formatDateTime(end)}`;
}

/** Relative: "Just now", "12m ago", "3h 5m ago", "2d ago". */
export function formatTimeSince(at: Date | string | undefined): string {
  const d = toDate(at);
  if (!d) return "Unknown";
  const diffMs = Date.now() - d.getTime();
  const diffMin = Math.floor(diffMs / 60000);
  if (diffMin < 1) return "Just now";
  if (diffMin < 60) return `${diffMin}m ago`;
  const diffHr = Math.floor(diffMin / 60);
  if (diffHr < 24) return `${diffHr}h ${diffMin % 60}m ago`;
  return `${Math.floor(diffHr / 24)}d ago`;
}

/** "1h 12m" / "45m" / "< 1m". When `end` is undefined, uses `Date.now()`. */
export function formatDuration(
  start: Date | string | undefined,
  end: Date | string | undefined
): string {
  const s = toDate(start);
  if (!s) return "";
  const e = toDate(end);
  const endMs = e ? e.getTime() : Date.now();
  const ms = Math.max(0, endMs - s.getTime());
  const min = Math.floor(ms / 60_000);
  if (min < 1) return "< 1m";
  if (min < 60) return `${min}m`;
  const h = Math.floor(min / 60);
  return `${h}h ${min % 60}m`;
}
