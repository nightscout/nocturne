import type { CarbMarkerDto } from "./dto.js";

// An empty id is a missing one: rows keyed by it would collide on "".
export const hasId = (id: unknown): id is string => id != null && id !== "";

/**
 * The key of a row in a keyed `{#each}`: its record id, or its time and position when
 * it has none, since rows without an id are never deduped against each other.
 */
export function markerKey(id: string | null | undefined, time: Date, index: number): string {
	return hasId(id) ? id : `${time.getTime()}-${index}`;
}

/**
 * The identity of a carb marker, or undefined when it has no record id. An offset
 * marker shares its meal's treatmentId, so its time tells it apart from the meal.
 */
export function carbMarkerKey(
	marker: Pick<CarbMarkerDto, "treatmentId" | "isOffset"> & { time: Date }
): string | undefined {
	if (!hasId(marker.treatmentId)) return undefined;
	return marker.isOffset ? `${marker.treatmentId}@${marker.time.getTime()}` : marker.treatmentId;
}
