/**
 * Hardcoded mapping from the active pump mode to a prediction-line dash style.
 * Per design (no per-tenant override): solid for fully-automatic modes,
 * sparse dashes for limited automation, dots for manual, long dashes for off.
 * History is always solid; this only applies to the prediction spline.
 */

import { PumpModeState } from "$lib/api";

export function predictionDashArray(
  mode: PumpModeState | null | undefined,
): string | undefined {
  switch (mode) {
    case PumpModeState.Suspended:
    case PumpModeState.Off:
      return "8 4";
    case PumpModeState.Limited:
      return "3 3";
    case PumpModeState.Manual:
      return "1 4";
    // Automatic, Boost, EaseOff, Sleep, Exercise, null, undefined render as solid.
    // If a future PumpModeState variant is added it will inherit "solid" here;
    // update this switch when introducing new dash styles.
    default:
      return undefined;
  }
}

export function predictionLineCap(
  mode: PumpModeState | null | undefined,
): "round" | "butt" {
  return mode === PumpModeState.Manual ? "round" : "butt";
}
