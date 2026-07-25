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
