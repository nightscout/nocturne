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

  const response = await apiClient.sensorGlucose.getAll(from, to, 10000);
  return response.data ?? [];
});

export const getBolusesAndCarbs = query(entriesSchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  const { from = new Date(), to = new Date() } = props.dateRange;
  if (!from || !to) throw new Error("Invalid date range");

  const [bolusResponse, carbResponse] = await Promise.all([
    apiClient.bolus.getAll(from, to, 10000),
    apiClient.nutrition.getCarbIntakes(from, to, 10000),
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

  const [entriesResponse, bolusResponse, carbResponse] = await Promise.all([
    apiClient.sensorGlucose.getAll(from, to, 10000),
    apiClient.bolus.getAll(from, to, 10000),
    apiClient.nutrition.getCarbIntakes(from, to, 10000),
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

const treatmentIdSchema = z.object({
  treatmentId: z.string(),
});

/**
 * Look up a v4 entry record by treatmentId, trying each entity type
 * sequentially. Returns the matching record or null if not found in any
 * collection.
 */
export const getEntryByTreatmentId = query(
  treatmentIdSchema,
  async ({ treatmentId }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    const fetchers = [
      {
        kind: "bolus" as const,
        fetch: () => apiClient.bolus.getById(treatmentId),
      },
      {
        kind: "carbs" as const,
        fetch: () => apiClient.nutrition.getCarbIntakeById(treatmentId),
      },
      {
        kind: "bgCheck" as const,
        fetch: () => apiClient.bGCheck.getById(treatmentId),
      },
      {
        kind: "note" as const,
        fetch: () => apiClient.note.getById(treatmentId),
      },
      {
        kind: "deviceEvent" as const,
        fetch: () => apiClient.deviceEvent.getById(treatmentId),
      },
    ];

    for (const { kind, fetch } of fetchers) {
      try {
        const data = await fetch();
        if (data?.id) return { kind, data };
      } catch {
        // 404 or other error — try the next type
      }
    }

    return null;
  }
);
