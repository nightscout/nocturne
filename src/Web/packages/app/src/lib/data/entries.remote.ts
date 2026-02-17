import { getRequestEvent, query } from "$app/server";
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

  const response = await apiClient.glucose.getSensorGlucose(
    from.getTime(),
    to.getTime(),
    10000
  );
  return response.data ?? [];
});

export const getTreatments = query(entriesSchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const { from = new Date(), to = new Date() } = props.dateRange;
  if (!from || !to) throw new Error("Invalid date range");

  const fromMs = from.getTime();
  const toMs = to.getTime();

  const [bolusResponse, carbResponse] = await Promise.all([
    apiClient.insulin.getBoluses(fromMs, toMs, 10000),
    apiClient.nutrition.getCarbIntakes(fromMs, toMs, 10000),
  ]);

  return {
    boluses: bolusResponse.data ?? [],
    carbIntakes: carbResponse.data ?? [],
  };
});

export const getStats = query(entriesSchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const { from = new Date(), to = new Date() } = props.dateRange;
  if (!from || !to) throw new Error("Invalid date range");

  const fromMs = from.getTime();
  const toMs = to.getTime();

  const [entriesResponse, bolusResponse, carbResponse] = await Promise.all([
    apiClient.glucose.getSensorGlucose(fromMs, toMs, 10000),
    apiClient.insulin.getBoluses(fromMs, toMs, 10000),
    apiClient.nutrition.getCarbIntakes(fromMs, toMs, 10000),
  ]);

  const entries = entriesResponse.data ?? [];
  const boluses = bolusResponse.data ?? [];
  const carbIntakes = carbResponse.data ?? [];

  const stats = apiClient.statistics.analyzeGlucoseData({
    entries,
    boluses,
    carbIntakes,
  });

  return stats;
});
