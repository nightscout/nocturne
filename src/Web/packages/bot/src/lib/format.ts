import { MGDL_PER_MMOL } from "@nocturne/ui/glucose";

export const TREND_ARROWS: Record<string, string> = {
  DoubleUp: "^^",
  SingleUp: "^",
  FortyFiveUp: "/",
  Flat: "->",
  FortyFiveDown: "\\",
  SingleDown: "v",
  DoubleDown: "vv",
  "NOT COMPUTABLE": "?",
  "RATE OUT OF RANGE": "?",
};

export function formatGlucose(mgdl: number, unit: "mg/dL" | "mmol/L"): string {
  if (unit === "mmol/L") return `${(mgdl / MGDL_PER_MMOL).toFixed(1)} mmol/L`;
  return `${mgdl} mg/dL`;
}

export function trendArrow(direction: string): string {
  return TREND_ARROWS[direction] ?? direction;
}
