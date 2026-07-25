/**
 * Server-side resolution of a report's date range.
 *
 * A report's "day" is the patient's day, so the day boundaries are computed in
 * the timezone on their therapy settings rather than the container's timezone.
 * The window is inclusive: it runs from midnight opening `from` to the last
 * millisecond of `to`, both read in that timezone.
 */
import { z } from "zod";
import { error } from "@sveltejs/kit";
import { getProfileSummary } from "$api/generated/profiles.generated.remote";
import { getLocalDayBoundariesUtc } from "$lib/utils/timezone";
import { dayCount, isTimeZone, resolveDayRange } from "$lib/utils/date-range";

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
  /** The patient's IANA timezone, or null when their profile does not set one. */
  timeZone: string | null;
}

/**
 * The patient's configured IANA timezone, or null when it is unset or is a value
 * this runtime does not recognise. Null means the day boundaries fall back to UTC.
 */
export async function getPatientTimeZone(): Promise<string | null> {
  const profile = await getProfileSummary(undefined);
  const timeZone = profile?.therapySettings?.[0]?.timezone;
  return isTimeZone(timeZone) ? timeZone : null;
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

  return { startDate, endDate, dayCount: dayCount(from, to), timeZone };
}
