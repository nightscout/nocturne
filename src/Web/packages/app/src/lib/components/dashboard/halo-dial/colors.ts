/**
 * Halo-dial colour helpers - thin shim that delegates to the shared chart
 * colour module so callers keep their existing API.
 */
import { DEFAULT_THRESHOLDS } from "$lib/constants";
import {
  getGlucoseColor,
  getGlucoseColorContinuous,
} from "$lib/utils/chart-colors";
import { HaloDialColorMode } from "$lib/api";

export function bgColorDiscrete(mgdl: number): string {
  return getGlucoseColor(mgdl, DEFAULT_THRESHOLDS);
}

export { getGlucoseColorContinuous as bgColorContinuous };

export function bgColor(mgdl: number, mode: HaloDialColorMode): string {
  return mode === HaloDialColorMode.Continuous
    ? getGlucoseColorContinuous(mgdl)
    : bgColorDiscrete(mgdl);
}
