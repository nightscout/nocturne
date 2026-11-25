import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
import type { GlucoseUnits } from "#lib/utils/formatting.js";

/**
 * The step of a condition value input, in the given units, for the rule editor
 * and the alert wizard.
 */
export function valueStep(
  kind: string,
  units: GlucoseUnits = glucoseUnits.current
): number {
  switch (kind) {
    case "threshold":
    case "predicted":
    case "eventual_glucose":
      return units === "mmol" ? 0.1 : 1;
    case "carbs_required":
      return 1;
    case "rate_of_change":
      return units === "mmol" ? 0.01 : 0.1;
    case "sensitivity_ratio":
      return 0.01;
    case "transmitter_battery":
      return 1;
    default:
      return 0.1;
  }
}
