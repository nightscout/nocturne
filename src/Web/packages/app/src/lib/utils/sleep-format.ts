/**
 * Shared date/duration presentation helpers for the sleep reports, used by both
 * the trends page and the single-night drill-down. Kept alongside sleep-stages
 * and sleep-night-mapping so the sleep report has one home for its helpers.
 */

/** Format a minute count as "7h 42m" / "45m" / "3h". */
export function formatMinutesDuration(totalMinutes: number): string {
  const total = Math.max(0, Math.round(totalMinutes));
  const h = Math.floor(total / 60);
  const m = total % 60;
  if (h === 0) return `${m}m`;
  if (m === 0) return `${h}h`;
  return `${h}h ${m}m`;
}
