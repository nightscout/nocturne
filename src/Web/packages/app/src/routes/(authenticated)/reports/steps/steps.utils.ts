/**
 * Sums step metrics by local calendar day.
 *
 * @param stepCounts - Raw step data (mills = Unix ms, metric = step count)
 * @param days - Local midnight Date objects defining the actogram rows
 * @returns Map from day.getTime() → total steps that day
 */
export function computeDayTotals(
  stepCounts: { mills: number; metric: number }[],
  days: Date[],
): Map<number, number> {
  const totals = new Map<number, number>(days.map((d) => [d.getTime(), 0]));
  for (const s of stepCounts) {
    const d = new Date(s.mills);
    const midnight = new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
    if (totals.has(midnight)) {
      totals.set(midnight, (totals.get(midnight) ?? 0) + s.metric);
    }
  }
  return totals;
}

/**
 * Converts a target timestamp into an actogram row offset.
 *
 * Finds the first day >= targetMs and returns its index, clamped so that
 * `visibleCount` rows always fit.
 *
 * @param targetMs - Must be a local midnight timestamp (e.g. `new Date(y, m, d).getTime()`).
 *   Passing a UTC midnight value (`new Date('YYYY-MM-DD').getTime()`) may give off-by-one
 *   results in UTC+ timezones.
 */
export function computeInitialOffset(
  days: Date[],
  targetMs: number,
  visibleCount: number,
): number {
  const idx = days.findIndex((d) => d.getTime() >= targetMs);
  if (idx === -1) return Math.max(0, days.length - visibleCount);
  return Math.max(0, Math.min(idx, days.length - visibleCount));
}
