import { getRequestEvent, query } from "$app/server";
import { z } from "zod";

/**
 * Spike only: a year of the punch card's per-day figures, without the raw readings it also
 * carries, so the browser gets about what a real per-day summary would send.
 */
export const getSpikeYearDays = query(z.number().int().min(1).max(400), async (days) => {
  const { apiClient } = getRequestEvent().locals;
  const end = new Date();
  const start = new Date(end.getTime() - (days - 1) * 86_400_000);
  const ymd = (d: Date) => d.toISOString().slice(0, 10);
  const result = await apiClient.statistics.getPunchCardData(ymd(start), ymd(end));
  return (result.months ?? [])
    .flatMap((m) => m.days ?? [])
    .filter((d) => d.date && (d.totalReadings ?? 0) > 0)
    .map((d) => ({
      date: d.date!,
      readings: d.totalReadings ?? 0,
      tir: d.inRangePercent ?? 0,
      below70: (d.lowPercent ?? 0) + (d.veryLowPercent ?? 0),
      below54: d.veryLowPercent ?? 0,
      above250: d.veryHighPercent ?? 0,
      carbs: d.totalCarbs ?? 0,
      insulin: d.totalInsulin ?? 0,
    }));
});
