import { endOfDay, isTimeZone, startOfDay } from "#lib/utils/date-range.js";
import type { DateSpan } from "./workspace.svelte.js";

/**
 * Two dates as the instants a run reads: from the start of the first day to the end of the last, both
 * days in `timeZone` (the patient's zone, which the report's days are cut in; UTC when none is set,
 * as the other reports do).
 */
export function spanRange(span: DateSpan, timeZone: string | null): { from: string; to: string } {
  const zone = isTimeZone(timeZone) ? timeZone : "UTC";
  return {
    from: startOfDay(span.from, zone).toISOString(),
    to: new Date(endOfDay(span.to, zone).getTime() + 1).toISOString(),
  };
}
