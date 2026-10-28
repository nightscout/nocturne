import { getRequestEvent, query } from "$app/server";
import { z } from "zod";
import { fetchAllGlucose } from "$lib/api/glucose-pagination";

/**
 * Spike only: raw readings as [mills, mgdl] pairs, bucketed in the browser so the
 * days follow the viewer's local time. A real chart would get its buckets from the API.
 */
export const getSpikeGlucose = query(z.number().int().min(1).max(120), async (days) => {
  const { apiClient } = getRequestEvent().locals;
  const to = new Date();
  const from = new Date(to.getTime() - days * 86_400_000);
  const readings = await fetchAllGlucose(apiClient, from.toISOString(), to.toISOString());
  return readings
    .filter((r) => r.mills != null && r.mgdl != null)
    .map((r) => [r.mills!, r.mgdl!] as const);
});
