import { getRequestEvent, query } from "$app/server";
import type { Entry, Treatment } from "$lib/api";
import { z } from "zod";

const entriesSchema = z.object({
  dateRange: z.object({
    from: z.date().optional(),
    to: z.date().optional(),
  }),
});

export const getEntries = query(entriesSchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const { from = new Date(), to = new Date() } = props.dateRange;
  if (!from || !to) throw new Error("Invalid date range");
  const entriesQuery = JSON.stringify({
    date: {
      $gte: from.toISOString(),
      $lte: to.toISOString(),
    },
  });

  const entries: Entry[] = await apiClient.entries.getEntries2(entriesQuery);
  return entries;
});

export const getTreatments = query(entriesSchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const { from = new Date(), to = new Date() } = props.dateRange;
  if (!from || !to) throw new Error("Invalid date range");

  const treatmentsQuery = JSON.stringify({
    created_at: {
      $gte: from.toISOString(),
      $lte: to.toISOString(),
    },
  });
  const treatments: Treatment[] = await apiClient.treatments.getTreatments(undefined, undefined, undefined, treatmentsQuery);
  return treatments;
});

export const getStats = query(entriesSchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const { from = new Date(), to = new Date() } = props.dateRange;
  if (!from || !to) throw new Error("Invalid date range");

  const entriesQuery = JSON.stringify({
    date: {
      $gte: from.toISOString(),
      $lte: to.toISOString(),
    },
  });
  const treatmentsQuery = JSON.stringify({
    created_at: {
      $gte: from.toISOString(),
      $lte: to.toISOString(),
    },
  });

  const [entries, treatments] = await Promise.all([
    apiClient.entries.getEntries2(entriesQuery),
    apiClient.treatments.getTreatments(undefined, undefined, undefined, treatmentsQuery),
  ]);

  const stats = apiClient.statistics.analyzeGlucoseData({
    entries,
    treatments,
  });

  return stats;
});
