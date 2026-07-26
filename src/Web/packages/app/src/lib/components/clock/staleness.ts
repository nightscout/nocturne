/**
 * Clock-face reading age, shared by ClockFaceRenderer and the public clock route.
 *
 * `now` is a parameter rather than a `Date.now()` read inside these functions:
 * when the CGM stops, the poll keeps returning the same reading, so
 * `lastUpdated` never changes. A derivation that reads the clock internally has
 * no reactive dependency that ticks and would never recompute, leaving a
 * wall-mounted clock showing an hours-old value with no age indication.
 */

/** Whole minutes elapsed between `lastUpdated` and `now`, floored at 0. */
export function readingAgeMinutes(lastUpdated: number, now: number): number {
  return Math.max(0, Math.floor((now - lastUpdated) / 60000));
}

/**
 * Whether the reading is older than the clock face's configured stale window.
 * A `staleMinutes` of 0 or undefined disables the check.
 */
export function isClockReadingStale(
  staleMinutes: number | undefined,
  lastUpdated: number,
  now: number
): boolean {
  if (!staleMinutes) return false;
  return readingAgeMinutes(lastUpdated, now) >= staleMinutes;
}

/** Compact age label for a clock face: "now", "7m". */
export function readingAgeLabel(lastUpdated: number, now: number): string {
  const mins = readingAgeMinutes(lastUpdated, now);
  return mins < 1 ? "now" : `${mins}m`;
}
