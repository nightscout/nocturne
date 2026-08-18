import {
  MGDL_PER_MMOL,
  UNKNOWN_DIRECTION_GLYPH,
  canonicalDirection,
} from "@nocturne/ui/glucose";

// Plain-text arrows rather than the web app's Unicode glyphs: chat platforms render these
// legibly everywhere. Directions with no arrow fall through to UNKNOWN_DIRECTION_GLYPH.
export const TREND_ARROWS: Record<string, string> = {
  DoubleUp: "^^",
  SingleUp: "^",
  FortyFiveUp: "/",
  Flat: "->",
  FortyFiveDown: "\\",
  SingleDown: "v",
  DoubleDown: "vv",
};

export function formatGlucose(mgdl: number, unit: "mg/dL" | "mmol/L"): string {
  if (unit === "mmol/L") return `${(mgdl / MGDL_PER_MMOL).toFixed(1)} mmol/L`;
  return `${mgdl} mg/dL`;
}

export function trendArrow(direction: string): string {
  return TREND_ARROWS[canonicalDirection(direction)] ?? UNKNOWN_DIRECTION_GLYPH;
}
