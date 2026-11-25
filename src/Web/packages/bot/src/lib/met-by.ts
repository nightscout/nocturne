import { AlertLimitFact, ForecastCurve } from "@nocturne/api-client";
import type { MetPoint } from "../types.js";
import { formatGlucose } from "./format.js";

/** The words for each forecast curve. A curve this build does not know is shown as the engine sent it. */
export const CURVE_NAMES: Record<ForecastCurve, string> = {
  [ForecastCurve.Default]: "Default forecast",
  [ForecastCurve.Iob]: "IOB forecast",
  [ForecastCurve.Cob]: "COB forecast",
  [ForecastCurve.Uam]: "UAM forecast",
  [ForecastCurve.Zt]: "Zero temp forecast",
};

/** The words for each fact a limit can name. A fact this build does not know is shown as the engine sent it. */
export const LIMIT_FACT_NAMES: Record<AlertLimitFact, string> = {
  [AlertLimitFact.TargetLow]: "target low",
  [AlertLimitFact.TargetHigh]: "target high",
  [AlertLimitFact.LatestGlucose]: "latest glucose",
};

const isForecastCurve = (value: string): value is ForecastCurve => Object.values<string>(ForecastCurve).includes(value);
const isLimitFact = (value: string): value is AlertLimitFact => Object.values<string>(AlertLimitFact).includes(value);

function minutes(offset: number): string {
  return offset === 1 ? "1 minute" : `${offset} minutes`;
}

/**
 * ", limit 70 mg/dL (target low minus 10 mg/dL)" for a limit from a fact: the number compared with,
 * then where it came from. Nothing for a number.
 */
function comparedWith(point: MetPoint, unit: "mg/dL" | "mmol/L"): string {
  if (point.limitFact === undefined || point.limit === undefined) return "";
  const fact = isLimitFact(point.limitFact) ? LIMIT_FACT_NAMES[point.limitFact] : point.limitFact;
  const offset = point.limitOffset ?? 0;
  const source =
    offset > 0
      ? `${fact} plus ${formatGlucose(offset, unit)}`
      : offset < 0
        ? `${fact} minus ${formatGlucose(-offset, unit)}`
        : fact;
  return `, limit ${formatGlucose(point.limit, unit)} (${source})`;
}

/**
 * A field for each condition the alert met with a forecast point, a loop value or a limit from a
 * fact: which curve and where, and the target it was compared with, so the reader knows what fired.
 * A kind this build does not know is left out.
 */
export function describeMetBy(points: MetPoint[] | undefined, unit: "mg/dL" | "mmol/L") {
  return (points ?? []).flatMap((point, index) => {
    if (point.value === undefined) return [];
    const key = String(point.leafId ?? `at-${index}`);
    switch (point.kind) {
      case "threshold":
        return [{ key, label: "Glucose", value: `${formatGlucose(point.value, unit)}${comparedWith(point, unit)}` }];
      case "predicted": {
        const curve =
          point.curve === undefined ? "Forecast" : isForecastCurve(point.curve) ? CURVE_NAMES[point.curve] : point.curve;
        const at = point.offsetMinutes === undefined ? "" : ` in ${minutes(point.offsetMinutes)}`;
        return [{ key, label: curve, value: `${formatGlucose(point.value, unit)}${at}${comparedWith(point, unit)}` }];
      }
      case "eventual_glucose":
        return [
          { key, label: "Eventual glucose", value: `${formatGlucose(point.value, unit)}${comparedWith(point, unit)}` },
        ];
      case "carbs_required":
        return [{ key, label: "Carbs needed", value: `${point.value} g` }];
      default:
        return [];
    }
  });
}
