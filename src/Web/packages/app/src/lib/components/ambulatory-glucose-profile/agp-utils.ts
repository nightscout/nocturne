import { convertToDisplayUnits } from "$lib/utils/formatting";
import type { GlucoseUnits } from "$lib/stores/appearance-store.svelte";
import type { AveragedStats } from "$lib/api";

/** Standard AGP low threshold in mg/dL (clinical hypoglycemia) */
export const AGP_LOW_THRESHOLD = 70;

/** Format an hour (0-23) as a time string */
export function formatHour(hour: number, is24Hour: boolean): string {
	if (is24Hour) {
		return `${hour.toString().padStart(2, "0")}:00`;
	}
	if (hour === 0) return "12am";
	if (hour === 12) return "12pm";
	if (hour < 12) return `${hour}am`;
	return `${hour - 12}pm`;
}

/** Transform raw AveragedStats into display-unit-aware chart data */
export function transformStats(rawData: AveragedStats[], units: GlucoseUnits) {
	return rawData.map((d) => ({
		...d,
		median: convertToDisplayUnits(d.median ?? 0, units),
		percentiles: d.percentiles
			? {
					p10: convertToDisplayUnits(d.percentiles.p10 ?? 0, units),
					p25: convertToDisplayUnits(d.percentiles.p25 ?? 0, units),
					p75: convertToDisplayUnits(d.percentiles.p75 ?? 0, units),
					p90: convertToDisplayUnits(d.percentiles.p90 ?? 0, units),
				}
			: undefined,
	}));
}
