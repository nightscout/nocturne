import type { MetPoint } from "../types.js";
import { formatGlucose } from "./format.js";

/** The words for each forecast curve. A curve this build does not know is shown as the engine sent it. */
const CURVE_NAMES: Record<string, string> = {
  default: "Forecast",
  iob: "IOB forecast",
  cob: "COB forecast",
  uam: "UAM forecast",
  zt: "Zero temp forecast",
};

function minutes(offset: number): string {
  return offset === 1 ? "1 minute" : `${offset} minutes`;
}

/**
 * A field for each condition the alert met with a forecast point or a loop value: which curve and
 * where, so the reader knows which forecast fired. A kind this build does not know is left out.
 */
export function describeMetBy(points: MetPoint[] | undefined, unit: "mg/dL" | "mmol/L") {
  return (points ?? []).flatMap((point, index) => {
    if (point.value === undefined) return [];
    const key = String(point.leafId ?? `at-${index}`);
    switch (point.kind) {
      case "predicted": {
        const curve = point.curve === undefined ? "Forecast" : (CURVE_NAMES[point.curve] ?? point.curve);
        const at = point.offsetMinutes === undefined ? "" : ` in ${minutes(point.offsetMinutes)}`;
        return [{ key, label: curve, value: `${formatGlucose(point.value, unit)}${at}` }];
      }
      case "eventual_glucose":
        return [{ key, label: "Eventual glucose", value: formatGlucose(point.value, unit) }];
      case "carbs_required":
        return [{ key, label: "Carbs needed", value: `${point.value} g` }];
      default:
        return [];
    }
  });
}
