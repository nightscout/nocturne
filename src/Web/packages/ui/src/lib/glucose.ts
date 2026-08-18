/**
 * Shared glucose presentation helpers — unit conversion/formatting, trend-direction glyphs
 * and rotations, the trend-chevron angle, and the delta colour scale. Pure and
 * store-independent so both the web app and the desktop companion render glucose identically.
 * The web app re-exports these from its own `utils/formatting` to keep a single source of truth.
 */

/** Display unit preference. */
export type GlucoseUnits = "mg/dl" | "mmol";

/**
 * Milligrams per decilitre in one millimole per litre of glucose. Must equal
 * `GlucoseConstants.MgdlPerMmol`; `GlucoseConversionFactorMirrorTests` fails if it does not.
 */
export const MGDL_PER_MMOL = 18.0182;

/** Convert a glucose value from mg/dL to the given display units. */
export function convertToDisplayUnits(mgdl: number, units: GlucoseUnits): number {
  if (units === "mmol") {
    return Math.round((mgdl / MGDL_PER_MMOL) * 10) / 10;
  }
  return Math.round(mgdl);
}

/** Convert a glucose value from display units back to mg/dL. */
export function convertFromDisplayUnits(value: number, units: GlucoseUnits): number {
  if (units === "mmol") {
    return Math.round(value * MGDL_PER_MMOL);
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
 * Glyph shown when a direction cannot be drawn as an arrow. A CGM that reports no trend
 * must never read as a stable one, so the unknown case gets its own mark rather than the
 * Flat arrow or blank space a missing lookup would otherwise leave behind.
 */
export const UNKNOWN_DIRECTION_GLYPH = "?";

/** Unicode glyph per drawable direction. `None`/`NotComputable` are absent by design. */
const DIRECTION_GLYPHS: Record<string, string> = {
  DoubleUp: "⇈",
  SingleUp: "↑",
  FortyFiveUp: "↗",
  Flat: "→",
  FortyFiveDown: "↘",
  SingleDown: "↓",
  DoubleDown: "⇊",
  RateOutOfRange: "⇕",
};

/** Degrees to rotate an upward arrow icon by, per direction that an arrow can express. */
const DIRECTION_ROTATIONS: Record<string, number> = {
  DoubleUp: 0,
  SingleUp: 0,
  FortyFiveUp: 45,
  Flat: 90,
  FortyFiveDown: 135,
  SingleDown: 180,
  DoubleDown: 180,
};

const CANONICAL_DIRECTIONS = new Map(
  [
    "None",
    "DoubleUp",
    "SingleUp",
    "FortyFiveUp",
    "Flat",
    "FortyFiveDown",
    "SingleDown",
    "DoubleDown",
    "NotComputable",
    "RateOutOfRange",
  ].map((name) => [name.toUpperCase(), name] as const),
);

/**
 * Canonical `GlucoseDirection` name for any casing/separator variant a caller may hold
 * ("NONE", "NOT COMPUTABLE", "FORTY_FIVE_UP"), or `""` when nothing recognisable arrived.
 */
export function canonicalDirection(direction: string | null | undefined): string {
  if (!direction) return "";
  return CANONICAL_DIRECTIONS.get(direction.toUpperCase().replace(/[^A-Z]/g, "")) ?? "";
}

/** Glyph for a direction, or {@link UNKNOWN_DIRECTION_GLYPH} when it has none. */
export function directionGlyph(direction: string | null | undefined): string {
  return DIRECTION_GLYPHS[canonicalDirection(direction)] ?? UNKNOWN_DIRECTION_GLYPH;
}

/**
 * Rotation for an upward arrow icon, or `null` when no arrow can express the direction.
 * Callers must then render {@link directionGlyph} unrotated — a rotated glyph would read
 * as a trend the CGM never reported.
 */
export function directionRotation(direction: string | null | undefined): number | null {
  return DIRECTION_ROTATIONS[canonicalDirection(direction)] ?? null;
}

/** True when the direction is drawn as a doubled arrow. */
export function isDoubleArrow(direction: string | null | undefined): boolean {
  const name = canonicalDirection(direction);
  return name === "DoubleUp" || name === "DoubleDown";
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

/**
 * Text-colour class for a trend direction, scaled by trend severity.
 *
 * Uses the theme's status tokens rather than fixed Tailwind palette colours, so
 * the arrow matches the chart in every theme instead of only the default one.
 */
export function deltaColorClass(direction: string): string {
  switch (direction) {
    case "DoubleUp":
    case "DoubleDown":
      return "text-status-critical";
    case "SingleUp":
    case "SingleDown":
    case "FortyFiveUp":
    case "FortyFiveDown":
      return "text-status-warning";
    case "Flat":
      return "text-status-normal";
    default:
      return "text-muted-foreground";
  }
}
