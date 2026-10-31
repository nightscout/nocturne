import { lineStopsFor, type GradientStop, type LineColorMode } from "../view/glucose-gradient-stops.js";
import type { ReportThresholds } from "./types.js";

/**
 * The gradient stops that colour a glucose line on a report panel's display-unit axis, in the
 * report's colour mode. The stops are placed in mg/dL; the API relates the axis unit to mg/dL
 * (`mgdlPerUnit`), so a panel in mmol/L is coloured at the same glucose as one in mg/dL.
 */
export function reportGlucoseStops(
  t: ReportThresholds,
  mode: Exclude<LineColorMode, "single">,
  yScale: (value: number) => number,
  height: number,
): GradientStop[] {
  const f = t.mgdlPerUnit;
  const mgdl = { veryLow: t.veryLow * f, low: t.low * f, high: t.high * f, veryHigh: t.veryHigh * f };
  return lineStopsFor(mode, mgdl, (value) => yScale(value / f), height) ?? [];
}
