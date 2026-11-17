/**
 * Remote functions for actogram report data. Thin wrappers around the generated
 * `getActogram` remote that add frontend-only glucose color resolution; a
 * refusal passes through with the status and reason the generated remote gave it.
 *
 * `getActogramRange` reads the picked days in the patient's zone, as every other report
 * reads them. `getActogramData` reads the instants it is given.
 */
import { query } from "$app/server";
import { parseDate } from "@internationalized/date";
import { z } from "zod";
import { DateRangeSchema, resolveReportRange } from "#lib/api/report-range.js";
import { ACTOGRAM_PADDING_DAYS } from "#lib/components/actogram/actogram.js";
import { getLocalDayBoundariesUtc } from "#lib/utils/timezone.js";
import { getActogram } from "#lib/api/generated/actograms.generated.remote.js";
import { getGlucoseColor } from "#lib/utils/chart-colors.js";
import { resolveChartThresholds } from "#lib/constants/glucose-thresholds.js";

const DEFAULT_DAYS = 14;

const actogramSchema = z.object({
  from: z.number(),
  to: z.number(),
});

const shiftDay = (day: string, days: number) => parseDate(day).add({ days }).toString();

async function readActogram(
  { from, to }: z.infer<typeof actogramSchema>,
  summary?: { from: number; to: number }
) {
  const data = await getActogram({
    startTime: from,
    endTime: to,
    summaryStartTime: summary?.from,
    summaryEndTime: summary?.to,
  });

  const thresholds = resolveChartThresholds(data.thresholds);

  const glucoseData = (data.glucose ?? []).map((p) => {
    const sgv = p.sgv ?? 0;
    return {
      mills: p.time ?? 0,
      sgv,
      color: getGlucoseColor(sgv, thresholds),
    };
  });

  const stepCounts = (data.stepCounts ?? []).map((s) => ({
    mills: s.time ?? 0,
    metric: s.steps ?? 0,
  }));

  const heartRates = (data.heartRates ?? []).map((h) => ({
    mills: h.time ?? 0,
    bpm: h.bpm ?? 0,
  }));

  const sleepSpans = (data.sleepSpans ?? []).map((s) => ({
    startMills: s.startMills ?? 0,
    endMills: s.endMills ?? s.startMills ?? 0,
    state: s.state ?? "Unknown",
  }));

  const days = (data.days ?? []).map((d) => ({
    date: d.date ?? "",
    startMills: d.startMills ?? 0,
    endMills: d.endMills ?? d.startMills ?? 0,
    hourMarks: d.hourMarks ?? [],
  }));

  return {
    days,
    stepCounts,
    stepDayTotals: data.stepDayTotals ?? {},
    stepSummary: data.stepSummary,
    heartRateSummary: data.heartRateSummary,
    timeZoneUnavailableReason: data.timeZoneUnavailableReason,
    heartRates,
    glucoseData,
    sleepSpans,
    thresholds,
  };
}

export const getActogramData = query(actogramSchema, (window) => readActogram(window));

export const getActogramRange = query(DateRangeSchema, async (input) => {
  const { startDate, endDate, timeZone, days } = await resolveReportRange(input, DEFAULT_DAYS);
  const rangeStart = Date.parse(startDate);
  const rangeEnd = Date.parse(endDate);

  const { start: windowStart } = getLocalDayBoundariesUtc(shiftDay(days[0], -ACTOGRAM_PADDING_DAYS), timeZone);
  const { end: windowEnd } = getLocalDayBoundariesUtc(
    shiftDay(days[days.length - 1], ACTOGRAM_PADDING_DAYS),
    timeZone
  );

  const data = await readActogram(
    { from: windowStart.getTime(), to: windowEnd.getTime() },
    // The range's last millisecond is inclusive; the API's summary end is not.
    { from: rangeStart, to: rangeEnd + 1 }
  );
  return { ...data, range: { from: rangeStart, to: rangeEnd } };
});
