/**
 * Shared glucose presentation helpers — unit conversion/formatting, the trend-chevron
 * angle, and the delta colour scale. Pure and store-independent so both the web app and the
 * desktop companion render glucose identically. The web app re-exports these from its own
 * `utils/formatting` and `halo-dial/geometry` to keep a single source of truth.
 */

/** Display unit preference. */
export type GlucoseUnits = "mg/dl" | "mmol";

/** Conversion factor from mg/dL to mmol/L. */
const MGDL_TO_MMOL = 18.01559;

/** Convert a glucose value from mg/dL to the given display units. */
export function convertToDisplayUnits(mgdl: number, units: GlucoseUnits): number {
  if (units === "mmol") {
    return Math.round((mgdl / MGDL_TO_MMOL) * 10) / 10;
  }
  return Math.round(mgdl);
}

/** Convert a glucose value from display units back to mg/dL. */
export function convertFromDisplayUnits(value: number, units: GlucoseUnits): number {
  if (units === "mmol") {
    return Math.round(value * MGDL_TO_MMOL);
  }
  return Math.round(value);
}

/** Format a glucose value for display (number; 1 dp for mmol, integer for mg/dL). */
export function formatGlucoseValue(mgdl: number, units: GlucoseUnits): number {
  const value = convertToDisplayUnits(mgdl, units);
  if (units === "mmol") {
    return Number(value.toFixed(1));
  }
  return Math.round(value);
}

/** Format a glucose delta for display, with a leading +/- sign by default. */
export function formatGlucoseDelta(
  deltaMgdl: number,
  units: GlucoseUnits,
  includeSign: boolean = true,
): string {
  const value = convertToDisplayUnits(deltaMgdl, units);
  const sign = includeSign && value > 0 ? "+" : "";
  if (units === "mmol") {
    return `${sign}${value.toFixed(1)}`;
  }
  return `${sign}${Math.round(value)}`;
}

/** Human-readable unit label. */
export function getUnitLabel(units: GlucoseUnits): string {
  return units === "mmol" ? "mmol/L" : "mg/dL";
}

/**
 * Convert a 5-minute glucose delta to a Dexcom-style trend chevron angle.
 * 0° = steady (chevron points right), negative = up, positive = down,
 * clamped at ±12 mg/dL/5min so very-fast trends don't go past the ring.
 */
export function trendAngle(deltaPer5: number): number {
  const clamped = Math.max(-12, Math.min(12, deltaPer5));
  if (clamped === 0) return 0;
  return -clamped * 6;
}

/** Tailwind text-colour class for a trend direction, scaled by trend severity. */
export function deltaColorClass(direction: string): string {
  switch (direction) {
    case "DoubleUp":
    case "DoubleDown":
      return "text-red-500";
    case "SingleUp":
    case "SingleDown":
      return "text-orange-500";
    case "FortyFiveUp":
    case "FortyFiveDown":
      return "text-yellow-500";
    case "Flat":
      return "text-green-500";
    default:
      return "text-muted-foreground";
  }
}
