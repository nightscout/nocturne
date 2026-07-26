/**
 * Halo-dial colour helpers - thin shim that delegates to the shared chart
 * colour module so callers keep their existing API.
 */
import { FALLBACK_GLUCOSE_THRESHOLDS } from "$lib/constants/glucose-thresholds";
import {
  getGlucoseColor,
  getGlucoseColorContinuous,
} from "$lib/utils/chart-colors";
import { HaloDialColorMode } from "$lib/api";

// The dial has no access to the tenant's thresholds yet, so it uses the shared
// fallback set. Previously it passed a set whose `low` was 55, which coloured
// 55-69 mg/dL as in-range on the dashboard's primary glucose display.
export function bgColorDiscrete(mgdl: number): string {
  return getGlucoseColor(mgdl, FALLBACK_GLUCOSE_THRESHOLDS);
}

export { getGlucoseColorContinuous as bgColorContinuous };

export function bgColor(mgdl: number, mode: HaloDialColorMode): string {
  return mode === HaloDialColorMode.Continuous
    ? getGlucoseColorContinuous(mgdl)
    : bgColorDiscrete(mgdl);
}
