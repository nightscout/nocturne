/**
 * Shared server-side helper to page through all sensor glucose readings for a date
 * range. Used by report remote functions that render raw readings.
 */
import { getRequestEvent } from "$app/server";

type ApiClient = ReturnType<typeof getRequestEvent>["locals"]["apiClient"];

const PAGE_SIZE = 10000;
const SAFETY_LIMIT = 200000;

/** Paginate through all sensor glucose readings for a date range. */
export async function fetchAllGlucose(
  apiClient: ApiClient,
  startDate: Date,
  endDate: Date
) {
  type GlucoseItem = NonNullable<
    Awaited<ReturnType<typeof apiClient.sensorGlucose.getAll>>["data"]
  >[number];
  let all: GlucoseItem[] = [];
  let offset = 0;
  let hasMore = true;

  while (hasMore) {
    const batch = await apiClient.sensorGlucose.getAll(
      startDate,
      endDate,
      PAGE_SIZE,
      offset
    );
    all = all.concat(batch.data ?? []);

    if ((batch.data?.length ?? 0) < PAGE_SIZE) {
      hasMore = false;
    } else {
      offset += PAGE_SIZE;
    }

    if (offset >= SAFETY_LIMIT) {
      console.warn(`Glucose fetch reached safety limit of ${SAFETY_LIMIT} records`);
      hasMore = false;
    }
  }

  return all;
}
