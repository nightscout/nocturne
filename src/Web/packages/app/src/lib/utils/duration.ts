/**
 * How long something lasted, rendered the same way everywhere it is shown.
 *
 * Hours never roll over into days: a 30-hour discharge reads "30h", because a
 * reader comparing one span against another does that arithmetic in hours.
 */

import { toDate } from "./formatting";

/** "3h" / "45m" / "7h 42m". Negative input clamps to "0m". */
export function formatMinutesDuration(totalMinutes: number): string {
  const total = Math.max(0, Math.round(totalMinutes));
  const h = Math.floor(total / 60);
  const m = total % 60;
  if (h === 0) return `${m}m`;
  if (m === 0) return `${h}h`;
  return `${h}h ${m}m`;
}

/**
 * Time between two instants, as {@link formatMinutesDuration}. Anything under a
 * minute reads "< 1m", so a span that exists is never rendered as "0m".
 *
 * A missing `end` means still running, and measures to now; a missing `start`
 * means there is nothing to measure and yields `absent`.
 */
export function formatElapsedDuration(
  start: Date | string | undefined | null,
  end: Date | string | undefined | null,
  absent = ""
): string {
  const from = toDate(start);
  if (!from) return absent;

  const to = toDate(end);
  const minutes = ((to ? to.getTime() : Date.now()) - from.getTime()) / 60_000;
  return minutes < 1 ? "< 1m" : formatMinutesDuration(minutes);
}

/**
 * Sub-second-precision elapsed time: "820ms" / "1.20s". For latencies, where
 * the interesting differences are smaller than the minute the other two round
 * to.
 */
export function formatElapsedMs(
  ms: number | undefined | null,
  absent = "N/A"
): string {
  if (ms == null) return absent;
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(2)}s`;
}
