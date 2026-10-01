/**
 * Remote function for actogram report data. Thin wrapper around the generated
 * `getActogram` remote that adds frontend-only glucose color resolution; a
 * refusal passes through with the status and reason the generated remote gave it.
 */
import { query } from "$app/server";
import { z } from "zod";
import { getActogram } from "$api/generated/actograms.generated.remote";
import { getGlucoseColor } from "$lib/utils/chart-colors";
import { resolveChartThresholds } from "$lib/constants/glucose-thresholds";

const actogramSchema = z.object({
  from: z.number(),
  to: z.number(),
});

export const getActogramData = query(actogramSchema, async ({ from, to }) => {
  const data = await getActogram({ startTime: from, endTime: to });

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

  return {
    stepCounts,
    stepDayTotals: data.stepDayTotals ?? {},
    heartRates,
    glucoseData,
    sleepSpans,
    thresholds,
  };
});
