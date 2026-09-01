/**
 * Server-side resolution of a report's date range.
 *
 * A report's "day" is the patient's day, so the day boundaries are computed on
 * the patient's calendar rather than the container's — see
 * `$lib/server/patient-timezone`. The window is inclusive: it runs from midnight
 * opening `from` to the last millisecond of `to`, both read in that timezone.
 */
import { z } from "zod";
import { error } from "@sveltejs/kit";
import { getRequestEvent } from "$app/server";
import { getLocalDayBoundariesUtc } from "$lib/utils/timezone";
import { dayCount, resolveDayRange } from "$lib/utils/date-range";
import { resolvePatientTimeZone } from "$lib/server/patient-timezone";

/**
 * Input schema for date range queries. Uses nullish() to accept both null and
 * undefined, matching the date-params hook which uses nullable defaults for
 * runed compatibility.
 */
export const DateRangeSchema = z.object({
  days: z.number().nullish(),
  from: z.string().nullish(),
  to: z.string().nullish(),
});

export type DateRangeInput = z.infer<typeof DateRangeSchema>;

export interface ReportRange {
  /** UTC instant at which the patient's first day begins. */
  startDate: Date;
  /** UTC instant of the last millisecond of the patient's last day. */
  endDate: Date;
  /** Calendar days the window covers, counting both end days. */
  dayCount: number;
}

/**
 * The patient's configured IANA timezone, or null when no source names one. See
 * `$lib/server/patient-timezone` for where it comes from and what null means.
 */
export async function getPatientTimeZone(): Promise<string | null> {
  return resolvePatientTimeZone(getRequestEvent().locals);
}

/** Resolve a report window against the patient's timezone. */
export async function resolveReportRange(
  input?: DateRangeInput | null,
  defaultDays = 7
): Promise<ReportRange> {
  const timeZone = await getPatientTimeZone();
  const { from, to } = resolveDayRange(input, defaultDays, timeZone ?? "UTC");

  const { start: startDate } = getLocalDayBoundariesUtc(from, timeZone);
  const { end: endDate } = getLocalDayBoundariesUtc(to, timeZone);

  if (isNaN(startDate.getTime()) || isNaN(endDate.getTime())) {
    throw error(400, "Invalid date parameters provided");
  }

  return { startDate, endDate, dayCount: dayCount(from, to) };
}
