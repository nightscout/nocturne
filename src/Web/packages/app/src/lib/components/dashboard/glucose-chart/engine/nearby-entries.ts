import type { EntryRecord } from "$lib/constants/entry-categories";
import { uniqueBy } from "$lib/utils/collections";
import { TREATMENT_PROXIMITY_MS } from "./chart-data-engine.svelte";

/** The entries behind the markers within the treatment proximity of `time`, one per entry id. */
export function findNearbyEntries(
  markers: readonly { time: Date; treatmentId?: string | null }[],
  time: Date,
  findEntry: (treatmentId: string) => EntryRecord | undefined
): EntryRecord[] {
  const at = time.getTime();
  const entries = markers
    .filter((m) => Math.abs(m.time.getTime() - at) < TREATMENT_PROXIMITY_MS)
    .map((m) => findEntry(m.treatmentId ?? ""))
    .filter((e) => e !== undefined);
  return uniqueBy(entries, (e) => e.data.id);
}
